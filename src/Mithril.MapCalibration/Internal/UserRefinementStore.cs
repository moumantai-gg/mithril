using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Per-user persistence for solved calibrations. Single global file at
/// <c>%LocalAppData%/Mithril/MapCalibration/refinements.json</c> &#8212; not
/// per-character, because the underlying anchors (NPCs + landmarks) don't
/// physically differ per character, so a calibration converged for area X on
/// one character is exactly as valid on another.
///
/// <para>Note: the in-game map pan/zoom that the user calibrated against
/// <em>can</em> differ across characters (different UI preferences). The
/// <see cref="AreaCalibration.CalibrationZoom"/> field captures the zoom; the
/// no-pan assumption is documented in <see cref="AreaCalibration.WorldToWindow(WorldCoord, double)"/>.
/// If a different character runs the game with a different pan, the projection
/// drifts and the user re-runs the walkthrough &#8212; an established Legolas UX
/// concern, not a data-shape concern.</para>
/// </summary>
internal sealed class UserRefinementStore
{
    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private Dictionary<string, AreaCalibration> _refinements = new(StringComparer.Ordinal);

    public UserRefinementStore(string directory, ILogger? logger = null)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "refinements.json");
        _logger = logger;
        Load();
    }

    /// <summary>
    /// Test-only factory. Creates a store backed by a throw-away temp file and
    /// optionally pre-seeds it with <paramref name="seed"/> entries. The backing
    /// file is written into <see cref="Path.GetTempPath"/> and is left for
    /// normal OS temp-dir reaping.
    /// </summary>
    internal static UserRefinementStore ForTests(IDictionary<string, AreaCalibration>? seed = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mithril-mapcal-fortests-{Guid.NewGuid():N}");
        var store = new UserRefinementStore(dir);
        if (seed is not null)
        {
            foreach (var kvp in seed)
                store.Save(kvp.Key, kvp.Value);
        }
        return store;
    }

    public IReadOnlyDictionary<string, AreaCalibration> All
    {
        get
        {
            lock (_gate) return new Dictionary<string, AreaCalibration>(_refinements, StringComparer.Ordinal);
        }
    }

    public bool TryGet(string areaKey, out AreaCalibration calibration)
    {
        lock (_gate)
        {
            return _refinements.TryGetValue(areaKey, out calibration!);
        }
    }

    public void Save(string areaKey, AreaCalibration calibration)
    {
        // Preserve UserRefinement and AutoCapture verbatim; stamp everything else as
        // UserRefinement (guards against a caller passing a BundledBaseline or
        // CommunitySync record into the user store by mistake).
        var stamped = calibration.Source is CalibrationSource.UserRefinement or CalibrationSource.AutoCapture
            ? calibration
            : calibration with { Source = CalibrationSource.UserRefinement };
        lock (_gate)
        {
            // Snapshot the prior value before mutating; if Persist throws
            // (disk full, AV scan lock, OneDrive placeholder hiccup) we must
            // restore so the in-memory state does not advance past on-disk
            // reality. Otherwise same-session reads succeed while the next
            // process boot reads the stale file — the silent data-loss path
            // the round-1 review caught, now wrapped transactionally.
            var hadPrior = _refinements.TryGetValue(areaKey, out var prior);
            _refinements[areaKey] = stamped;
            try { Persist(); }
            catch
            {
                if (hadPrior) _refinements[areaKey] = prior!;
                else _refinements.Remove(areaKey);
                throw;
            }
        }
    }

    public bool Remove(string areaKey)
    {
        lock (_gate)
        {
            if (!_refinements.TryGetValue(areaKey, out var prior)) return false;
            _refinements.Remove(areaKey);
            try { Persist(); }
            catch
            {
                _refinements[areaKey] = prior;
                throw;
            }
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            // Per-entry resilient parse (mithril#914 GATE-2 Fix A). Deserialising
            // the whole file in one call meant ONE unparseable calibration entry
            // (e.g. a downgraded pre-AutoCapture build hitting the unknown
            // "AutoCapture" enum NAME — UseStringEnumConverter THROWS on unknown
            // names) tripped the outer catch and discarded EVERY area's refinement
            // — total data loss, not a benign degrade. Instead we walk the
            // Calibrations object with JsonDocument and deserialise each value
            // individually so a single poisoned entry is skipped+warned while every
            // other area survives. Durable against any future additive enum/field
            // change, not just AutoCapture.
            var loaded = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
            bool needsMigration;

            // Open + parse + walk the JSON inside a tight scope so the read
            // stream is released BEFORE any migration Persist() runs — otherwise
            // the File.Replace in Persist hits a sharing violation on the
            // destination and the outer catch wipes the store. The doc/stream
            // are not needed past the dictionary population.
            using (var stream = File.OpenRead(_filePath))
            using (var doc = JsonDocument.Parse(stream))
            {
                // Detect file-level schema version. Absent → v1 (legacy shape that
                // predates this field, written by builds before #1021). v1 keyed
                // refinements by bare area name (e.g. "AreaSerbule"); v2+ uses the
                // per-scene Map_<X> grammar from the asset-load log (e.g.
                // "Map_AreaSerbule"). See docs/planning/map-calibration-1021-per-scene-keying/.
                var schemaVersion = 1;
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("schemaVersion", out var verProp) &&
                    verProp.ValueKind == JsonValueKind.Number &&
                    verProp.TryGetInt32(out var v))
                {
                    schemaVersion = v;
                }
                needsMigration = schemaVersion < 2;

                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("calibrations", out var calibrations) ||
                    calibrations.ValueKind != JsonValueKind.Object)
                {
                    // No (or malformed) calibrations object → nothing to load. An empty
                    // store is the correct result; a structurally-broken file is caught
                    // below by JsonDocument.Parse throwing. Do NOT persist here — an
                    // empty migration writes nothing back (idempotence + no spurious
                    // rewrite of a malformed-but-readable file).
                    _refinements = loaded;
                    return;
                }

                foreach (var entry in calibrations.EnumerateObject())
                {
                    try
                    {
                        var cal = entry.Value.Deserialize(MapCalibrationJsonContext.Default.AreaCalibration);
                        if (cal is null) continue;
                        // v1→v2 key migration: prefix bare area keys with "Map_". A
                        // pathological v1 file whose key ALREADY starts with "Map_"
                        // is kept verbatim (defensive — never double-prefix).
                        var key = entry.Name;
                        if (needsMigration && !key.StartsWith("Map_", StringComparison.Ordinal))
                        {
                            key = "Map_" + key;
                        }
                        // Schema-1 → Schema-2 frame inference (mithril#1076 Phase 6.2):
                        // when the on-disk entry has no "frame" property, infer the frame
                        // from the entry's Source per spec §7.2. Done at the JsonElement
                        // level (not the deserialised record) because the record default
                        // for Frame is Texture, which we cannot distinguish from an
                        // explicit "frame": "Texture" write — and the spec convention is
                        // that fresh writes ALWAYS include "frame". The pre-restamp Source
                        // is the discriminator (Phase 6.2 runs BEFORE the post-load
                        // Source-stamp that rewrites bundled/community values to
                        // UserRefinement) so a Schema-1 CommunitySync record can still be
                        // detected and surfaced via warn-log even though the surviving
                        // record's Source ends up as UserRefinement.
                        if (!entry.Value.TryGetProperty("frame", out _))
                        {
                            cal = cal with { Frame = InferFrameFromSource(entry.Value, key, _logger) };
                        }
                        // Stamp Source on surviving entries that don't carry an explicit
                        // user-store source. Older shapes may not carry it and the record
                        // default is UserRefinement, which matches; AutoCapture is
                        // preserved verbatim (it IS a valid user-store source). Anything
                        // else (BundledBaseline, CommunitySync) that somehow ended up
                        // persisted here is rewritten to UserRefinement defensively.
                        loaded[key] = cal.Source is CalibrationSource.UserRefinement or CalibrationSource.AutoCapture
                            ? cal
                            : cal with { Source = CalibrationSource.UserRefinement };
                    }
                    catch (JsonException ex)
                    {
                        // One unparseable entry (unknown future enum NAME / added field
                        // an older build can't read) — skip it, keep the rest. This is
                        // the durable downgrade-window degrade: the area re-runs
                        // calibration; no other area's data is touched.
                        _logger?.LogWarning(ex,
                            "Skipping unparseable user refinement entry {Area} in {Path} — {Reason}.",
                            entry.Name, _filePath, ex.Message);
                    }
                }
            }

            _refinements = loaded;

            // Persist immediately on a v1→v2 migration so subsequent boots are
            // no-op loads (idempotence) and so the file on disk reflects the
            // new key grammar before any consumer mutates the store. Use the
            // existing transactional Persist; if it throws, roll the in-memory
            // state back to empty (i.e. behave as though the load failed) so
            // we never end up with v2 keys in memory while v1 keys remain on
            // disk — that asymmetry is the silent data-loss path Save() guards
            // against, and the same invariant applies here.
            if (needsMigration && _refinements.Count > 0)
            {
                _logger?.LogInformation(
                    "Migrated {Count} user refinement(s) at {Path} to v2 (Map_<X> keying).",
                    _refinements.Count, _filePath);
                try { Persist(); }
                catch
                {
                    _refinements = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Genuine whole-file failure (IO error, or the file is not valid JSON
            // at all so JsonDocument.Parse threw). Degrade to empty + warn — the
            // store can't be trusted as a unit. Per-entry resilience above means
            // we only reach here for structural corruption, not a single bad value.
            _logger?.LogWarning(ex, "Failed to load user refinement store at {Path} — starting empty.", _filePath);
            _refinements = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Schema-1 &#8594; Schema-2 frame inference (mithril#1076 Phase 6.2 / spec §7.2).
    /// Reads the pre-restamp <c>"source"</c> property out of the raw JsonElement
    /// because the post-deserialise <see cref="AreaCalibration.Source"/> may have
    /// already been rewritten to <see cref="CalibrationSource.UserRefinement"/> by
    /// the defensive stamping above. Mapping:
    /// <list type="bullet">
    ///   <item><see cref="CalibrationSource.AutoCapture"/> &#8594; <see cref="CalibrationFrame.Texture"/> (RANSAC base-texture-pixel solve)</item>
    ///   <item><see cref="CalibrationSource.BundledBaseline"/> &#8594; <see cref="CalibrationFrame.Texture"/> (hand-authored texture-pixel anchor)</item>
    ///   <item><see cref="CalibrationSource.UserRefinement"/> &#8594; <see cref="CalibrationFrame.Overlay"/> (Legolas-wizard overlay-pixel fit; AutoCal hasn't shipped in a tagged release)</item>
    ///   <item><see cref="CalibrationSource.CommunitySync"/> &#8594; <see cref="CalibrationFrame.Overlay"/> + warn-log (aspirational; no consumer yet)</item>
    ///   <item>unknown / forward-compat enum name &#8594; <see cref="CalibrationFrame.Overlay"/> + warn-log</item>
    /// </list>
    /// Defaulting unknown / aspirational sources to Overlay is the safer fallback
    /// &#8212; AutoCal's drift-check rejects non-Texture records, so an inferred
    /// Overlay can&#8217;t silently feed a texture-frame consumer.
    /// </summary>
    private static CalibrationFrame InferFrameFromSource(System.Text.Json.JsonElement entry, string area, ILogger? logger)
    {
        string? rawSource = null;
        if (entry.TryGetProperty("source", out var srcProp) && srcProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            rawSource = srcProp.GetString();
        }

        switch (rawSource)
        {
            case "AutoCapture":
            case "BundledBaseline":
                return CalibrationFrame.Texture;
            case "UserRefinement":
            case null:
                // Absent source on a Schema-1 record means the bundled-baseline-style
                // default (BundledBaseline = 0) for older readers; but a Schema-1
                // record in the user store almost certainly was Legolas-wizard-produced,
                // so bias to Overlay. The drift-check rejects non-Texture records, so
                // this is fail-safe.
                return CalibrationFrame.Overlay;
            case "CommunitySync":
                logger?.LogWarning(
                    "User refinement entry {Area} has Source=CommunitySync but no consumer ships yet — defaulting Frame to Overlay (spec §7.2).",
                    area);
                return CalibrationFrame.Overlay;
            default:
                logger?.LogWarning(
                    "User refinement entry {Area} has unknown Source={Source} — defaulting Frame to Overlay (spec §7.2 forward-compat).",
                    area, rawSource);
                return CalibrationFrame.Overlay;
        }
    }

    /// <summary>
    /// Serialises the in-memory dictionary atomically. <b>Throws on IO failure</b>
    /// rather than swallowing &#8212; saving a calibration is a rare,
    /// user-initiated event (wizard Confirm), so a silent persist failure
    /// (transient AV scan lock, full disk, OneDrive placeholder hiccup) would
    /// leave the in-memory state advanced but lose the data on next process
    /// start with no surface signal. Callers (the public <see cref="Save"/> /
    /// <see cref="Remove"/> entry points) roll the in-memory state back and
    /// re-throw so the wizard / auto-solve sees the failure and can surface or retry.
    /// </summary>
    private void Persist()
    {
        var file = new UserRefinementFile(SchemaVersion: 2, Calibrations: _refinements);
        var json = JsonSerializer.Serialize(file, MapCalibrationJsonContext.Default.UserRefinementFile);
        // Atomic-ish write: temp file then move. Defends against a crash
        // mid-write turning the store into garbage.
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
        else File.Move(tmp, _filePath);
    }
}
