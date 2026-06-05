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
/// no-pan assumption is documented in <see cref="WorldToOverlayCalibration.ToOverlay(WorldCoord, double)"/>.
/// If a different character runs the game with a different pan, the projection
/// drifts and the user re-runs the walkthrough &#8212; an established Legolas UX
/// concern, not a data-shape concern.</para>
///
/// <para>Schema-3 (mithril#1082): the in-memory dictionary holds
/// <see cref="SceneRefinements"/> typed-slot containers so an AutoCal
/// texture-frame record and a Legolas-wizard overlay-frame record can coexist
/// under the same <c>MapAssetKey</c>. <see cref="Save"/> routes to the slot
/// named by <see cref="AreaCalibration.Frame"/>; the frame-scoped
/// <see cref="Remove(string, CalibrationFrame)"/> overload clears one slot and
/// compacts the scene entry when the last slot is emptied. Schema-1 and
/// Schema-2 files are migrated to Schema-3 transparently on <see cref="Load"/>
/// — see §4 of the spec for the migration table.</para>
/// </summary>
internal sealed class UserRefinementStore
{
    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private Dictionary<string, SceneRefinements> _refinements = new(StringComparer.Ordinal);

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
    /// <remarks>
    /// The seed signature stays <c>IDictionary&lt;string, AreaCalibration&gt;</c>
    /// for back-compat with the typed-frame test suite (e.g.
    /// <c>MapCalibrationServiceTypedFrameTests</c>) which seeds one record per
    /// scene and lets <see cref="Save"/> route by <see cref="AreaCalibration.Frame"/>.
    /// Tests that need to seed both slots for a scene can call
    /// <see cref="Save"/> twice with different-frame records.
    /// </remarks>
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

    /// <summary>
    /// Snapshot of all scenes and their typed slots. Returns a defensive copy so
    /// callers can iterate without holding the store's lock; the inner
    /// <see cref="SceneRefinements"/> values are immutable records.
    /// </summary>
    public IReadOnlyDictionary<string, SceneRefinements> All
    {
        get
        {
            lock (_gate) return new Dictionary<string, SceneRefinements>(_refinements, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Frame-scoped read. Returns the <paramref name="areaKey"/>'s slot for
    /// <paramref name="frame"/>; false + null when the scene is absent or the
    /// named slot is empty.
    /// </summary>
    public bool TryGet(string areaKey, CalibrationFrame frame, out AreaCalibration calibration)
    {
        lock (_gate)
        {
            if (_refinements.TryGetValue(areaKey, out var slots))
            {
                var cal = slots.Get(frame);
                if (cal is not null)
                {
                    calibration = cal;
                    return true;
                }
            }
            calibration = null!;
            return false;
        }
    }

    /// <summary>
    /// Frame-agnostic accessor returning the whole <see cref="SceneRefinements"/>
    /// for a scene; used by frame-flattening consumers (e.g.
    /// <c>MapCalibrationService.GetAllSources</c>). Returns false + an empty
    /// <see cref="SceneRefinements"/> when the scene is absent.
    /// </summary>
    public bool TryGetAny(string areaKey, out SceneRefinements slots)
    {
        lock (_gate)
        {
            if (_refinements.TryGetValue(areaKey, out var found))
            {
                slots = found;
                return true;
            }
            slots = SceneRefinements.Empty;
            return false;
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
            var existing = hadPrior ? prior! : SceneRefinements.Empty;
            // Route by Frame: the slot named by calibration.Frame is replaced;
            // the other slot survives. Last-writer-wins is now scoped to one
            // frame per scene instead of obliterating the cross-frame sibling.
            var updated = existing.With(stamped.Frame, stamped);
            _refinements[areaKey] = updated;
            try { Persist(); }
            catch
            {
                if (hadPrior) _refinements[areaKey] = prior!;
                else _refinements.Remove(areaKey);
                throw;
            }
        }
    }

    /// <summary>
    /// Frame-agnostic remove: clears the entire scene entry (both slots). Used
    /// by <c>ClearUserRefinement</c> — "starting over for this scene" semantics.
    /// </summary>
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

    /// <summary>
    /// Frame-scoped remove: clears the named slot only. If the resulting
    /// <see cref="SceneRefinements"/> is empty, the scene entry is removed from
    /// the dict (compaction matches the v2→v3 invariant — no all-null
    /// containers persist). Idempotent: returns false if the named slot was
    /// already null. Transactional Persist + rollback matches <see cref="Save"/>.
    /// </summary>
    public bool Remove(string areaKey, CalibrationFrame frame)
    {
        lock (_gate)
        {
            if (!_refinements.TryGetValue(areaKey, out var prior)) return false;
            if (prior.Get(frame) is null) return false;

            var updated = prior.Without(frame);
            if (updated.IsEmpty)
            {
                _refinements.Remove(areaKey);
            }
            else
            {
                _refinements[areaKey] = updated;
            }
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
            var loaded = new Dictionary<string, SceneRefinements>(StringComparer.Ordinal);
            int schemaVersion;

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
                // refinements by bare area name (e.g. "AreaSerbule"); v2 used the
                // per-scene Map_<X> grammar from the asset-load log; v3
                // (mithril#1082) nests the AreaCalibration under a typed-frame
                // SceneRefinements slot. See docs/planning/calibration-1082-*.
                schemaVersion = 1;
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("schemaVersion", out var verProp) &&
                    verProp.ValueKind == JsonValueKind.Number &&
                    verProp.TryGetInt32(out var v))
                {
                    schemaVersion = v;
                }

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

                if (schemaVersion >= 3)
                {
                    LoadV3Calibrations(calibrations, loaded);
                }
                else
                {
                    // v1 and v2 share the per-entry AreaCalibration shape; v1 also
                    // needs the bare-key → Map_-prefixed key rewrite. Both convert
                    // to v3 by nesting under the slot named by cal.Frame (with the
                    // narrow-window fix-up from spec §4.1).
                    LoadV1OrV2Calibrations(calibrations, loaded, isV1: schemaVersion < 2);
                }
            }

            _refinements = loaded;

            // Persist immediately on a v1/v2 → v3 migration so subsequent boots are
            // no-op loads (idempotence) and so the file on disk reflects the new
            // shape before any consumer mutates the store. Use the existing
            // transactional Persist; if it throws, roll the in-memory state back
            // to empty (i.e. behave as though the load failed) so we never end up
            // with v3 keys in memory while v1/v2 keys remain on disk — that
            // asymmetry is the silent data-loss path Save() guards against, and
            // the same invariant applies here.
            if (schemaVersion < 3 && _refinements.Count > 0)
            {
                _logger?.LogInformation(
                    "Migrated {Count} user refinement scene(s) at {Path} from v{From} to v3 (per-frame SceneRefinements slots).",
                    _refinements.Count, _filePath, schemaVersion);
                try { Persist(); }
                catch
                {
                    _refinements = new Dictionary<string, SceneRefinements>(StringComparer.Ordinal);
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
            _refinements = new Dictionary<string, SceneRefinements>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// v3 load path: deserialise each scene entry directly into a
    /// <see cref="SceneRefinements"/>. Per-entry resilient parse: one bad entry
    /// skips with a warn-log, the rest of the file loads.
    /// </summary>
    private void LoadV3Calibrations(JsonElement calibrations, Dictionary<string, SceneRefinements> loaded)
    {
        foreach (var entry in calibrations.EnumerateObject())
        {
            try
            {
                var slots = entry.Value.Deserialize(MapCalibrationJsonContext.Default.SceneRefinements);
                if (slots is null || slots.IsEmpty) continue;
                // v3 records are only produced by Save, which already restamps
                // Source; the contract is the defense, no restamp needed here.
                loaded[entry.Name] = slots;
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex,
                    "Skipping unparseable user refinement scene {Scene} in {Path} — {Reason}.",
                    entry.Name, _filePath, ex.Message);
            }
        }
    }

    /// <summary>
    /// v1 and v2 load path: each entry deserialises into one
    /// <see cref="AreaCalibration"/>, which is then placed under the slot named
    /// by <see cref="AreaCalibration.Frame"/>. v1 entries also get the bare-key
    /// → <c>Map_</c>-prefix rewrite and Source-based Frame inference (spec
    /// §7.2). Spec §4.1 narrow-window fix-up: a v2 entry with
    /// <c>Source=UserRefinement</c> and <c>Frame=Texture</c> routes to the
    /// Overlay slot instead (Source is more reliable than the field value in
    /// the ~24-hour window between #1077 and #1083).
    /// </summary>
    private void LoadV1OrV2Calibrations(JsonElement calibrations, Dictionary<string, SceneRefinements> loaded, bool isV1)
    {
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
                if (isV1 && !key.StartsWith("Map_", StringComparison.Ordinal))
                {
                    key = "Map_" + key;
                }

                // Schema-1 → Schema-2 frame inference (mithril#1076 Phase 6.2):
                // when the on-disk entry has no "frame" property, infer the
                // frame from the entry's Source per spec §7.2.
                if (!entry.Value.TryGetProperty("frame", out _))
                {
                    cal = cal with { Frame = InferFrameFromSource(entry.Value, key, _logger) };
                }

                // Defensive Source restamp (same logic as Save).
                cal = cal.Source is CalibrationSource.UserRefinement or CalibrationSource.AutoCapture
                    ? cal
                    : cal with { Source = CalibrationSource.UserRefinement };

                // v2→v3 slot routing.
                var targetFrame = cal.Frame;

                // Spec §4.1 narrow-window fix-up: a v2 record with
                // Source=UserRefinement AND Frame=Texture was likely written by
                // the Legolas wizard in the ~24-hour window between #1077 (Frame
                // field added, defaulting Texture) and #1083 (save sites started
                // stamping Frame explicitly to Overlay). Source is the more
                // reliable signal here — route to Overlay anyway and warn.
                if (!isV1 &&
                    cal.Source == CalibrationSource.UserRefinement &&
                    cal.Frame == CalibrationFrame.Texture)
                {
                    _logger?.LogWarning(
                        "v2→v3 fix-up: scene {Scene} has Source=UserRefinement + Frame=Texture (narrow-window between #1077 and #1083); routing to Overlay slot per spec §4.1.",
                        key);
                    targetFrame = CalibrationFrame.Overlay;
                    cal = cal with { Frame = CalibrationFrame.Overlay };
                }

                var existing = loaded.TryGetValue(key, out var prior) ? prior : SceneRefinements.Empty;
                loaded[key] = existing.With(targetFrame, cal);
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
    private static CalibrationFrame InferFrameFromSource(JsonElement entry, string area, ILogger? logger)
    {
        string? rawSource = null;
        if (entry.TryGetProperty("source", out var srcProp) && srcProp.ValueKind == JsonValueKind.String)
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
    /// <see cref="Remove(string)"/> / <see cref="Remove(string, CalibrationFrame)"/>
    /// entry points) roll the in-memory state back and re-throw so the wizard /
    /// auto-solve sees the failure and can surface or retry.
    /// </summary>
    private void Persist()
    {
        var file = new UserRefinementFile(SchemaVersion: 3, Calibrations: _refinements);
        var json = JsonSerializer.Serialize(file, MapCalibrationJsonContext.Default.UserRefinementFile);
        // Atomic-ish write: temp file then move. Defends against a crash
        // mid-write turning the store into garbage.
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
        else File.Move(tmp, _filePath);
    }
}
