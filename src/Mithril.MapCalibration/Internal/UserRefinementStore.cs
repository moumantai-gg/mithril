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
        var stamped = calibration with { Source = CalibrationSource.UserRefinement };
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

    /// <summary>
    /// Migration entry point: import each entry if the area is not already
    /// present in the store OR if its stored value's projection math differs
    /// from <paramref name="entries"/>'s value (the "downgrade window"
    /// recovery: legacy entry must be newer because we just came from a build
    /// that only wrote to the legacy field). Batched into a single persist
    /// and silent (no <see cref="IMapCalibrationService.Changed"/>; callers
    /// route through that interface's <c>ImportUserRefinements</c>).
    /// Returns the count actually written.
    /// </summary>
    public int ImportFromLegacy(IEnumerable<KeyValuePair<string, AreaCalibration>> entries)
    {
        var imported = 0;
        // Snapshot for rollback on Persist failure (same transactional invariant
        // as Save above — batched imports must be all-or-nothing on disk; the
        // in-memory state cannot diverge past the persisted state).
        Dictionary<string, AreaCalibration>? snapshot = null;
        lock (_gate)
        {
            foreach (var (key, cal) in entries)
            {
                if (_refinements.TryGetValue(key, out var existing) && MathEquals(existing, cal))
                    continue;
                snapshot ??= new Dictionary<string, AreaCalibration>(_refinements, StringComparer.Ordinal);
                _refinements[key] = cal with { Source = CalibrationSource.UserRefinement };
                imported++;
            }
            if (imported > 0)
            {
                try { Persist(); }
                catch
                {
                    _refinements = snapshot!; // restore pre-import state
                    throw;
                }
            }
        }
        return imported;
    }

    /// <summary>
    /// Compares only the fields that determine the world&#8596;pixel
    /// projection. Ignores <see cref="AreaCalibration.Source"/> (always
    /// re-stamped on import), <see cref="AreaCalibration.SchemaVersion"/>,
    /// <see cref="AreaCalibration.ReferenceCount"/>,
    /// <see cref="AreaCalibration.ResidualPixels"/>, and
    /// <see cref="AreaCalibration.LocatorScale"/> (all metadata, not transform).
    ///
    /// <para>Uses a relative tolerance instead of raw <c>==</c> so a one-ULP
    /// drift from JSON round-trip / cross-JIT codegen does not re-trigger an
    /// overwrite (and an unnecessary disk write) on every startup. The
    /// tolerance is tighter than any value the calibration math can produce
    /// in practice (scale ~1, rotation ~3, origin up to ~2000), so a real
    /// recalibration is never mistaken for "already in sync".</para>
    /// </summary>
    private static bool MathEquals(AreaCalibration a, AreaCalibration b) =>
        a.MirrorNorth == b.MirrorNorth &&
        AlmostEqual(a.Scale, b.Scale) &&
        AlmostEqual(a.RotationRadians, b.RotationRadians) &&
        AlmostEqual(a.OriginX, b.OriginX) &&
        AlmostEqual(a.OriginY, b.OriginY) &&
        AlmostEqual(a.CalibrationZoom, b.CalibrationZoom);

    private const double DoubleCompareRelTolerance = 1e-12;

    private static bool AlmostEqual(double a, double b)
    {
        if (a == b) return true; // covers NaN-stamped infinities and exact equality fast path
        var scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= DoubleCompareRelTolerance * scale;
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
            using var stream = File.OpenRead(_filePath);
            using var doc = JsonDocument.Parse(stream);
            var loaded = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);

            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("calibrations", out var calibrations) ||
                calibrations.ValueKind != JsonValueKind.Object)
            {
                // No (or malformed) calibrations object → nothing to load. An empty
                // store is the correct result; a structurally-broken file is caught
                // below by JsonDocument.Parse throwing.
                _refinements = loaded;
                return;
            }

            foreach (var entry in calibrations.EnumerateObject())
            {
                try
                {
                    var cal = entry.Value.Deserialize(MapCalibrationJsonContext.Default.AreaCalibration);
                    if (cal is null) continue;
                    // Stamp Source on every surviving entry; lifted records from
                    // older shapes may not carry it explicitly and the default on
                    // the record is UserRefinement, which matches this store's
                    // contents — but be defensive in case a future shape change
                    // reorders defaults.
                    loaded[entry.Name] = cal with { Source = CalibrationSource.UserRefinement };
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

            _refinements = loaded;
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
    /// Serialises the in-memory dictionary atomically. <b>Throws on IO failure</b>
    /// rather than swallowing &#8212; saving a calibration is a rare,
    /// user-initiated event (wizard Confirm), so a silent persist failure
    /// (transient AV scan lock, full disk, OneDrive placeholder hiccup) would
    /// leave the in-memory state advanced but lose the data on next process
    /// start with no surface signal. Callers (the public <see cref="Save"/> /
    /// <see cref="Remove"/> / <see cref="ImportFromLegacy"/> entry points)
    /// roll the in-memory state back and re-throw so the wizard / migration
    /// sees the failure and can surface or retry.
    /// </summary>
    private void Persist()
    {
        var file = new UserRefinementFile(SchemaVersion: 1, Calibrations: _refinements);
        var json = JsonSerializer.Serialize(file, MapCalibrationJsonContext.Default.UserRefinementFile);
        // Atomic-ish write: temp file then move. Defends against a crash
        // mid-write turning the store into garbage.
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
        else File.Move(tmp, _filePath);
    }
}
