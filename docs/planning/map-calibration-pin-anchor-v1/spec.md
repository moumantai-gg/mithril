# Spec — map pins as auto-calibration anchors v1 (mithril#1036)

**Tracked in:** [mithril#1036](https://github.com/moumantai-gg/mithril/issues/1036).
**Brainstormed:** 2026-06-03 with @arthur-conde; decisions captured below in §3.
**Related (sibling follow-ups):**
- [mithril#1037](https://github.com/moumantai-gg/mithril/issues/1037) — Palantir display decode (renders raw `Color N · Shape M` ints; will consume the new `MapPinDescriptor` helper after this PR lands).
- [mithril#1038](https://github.com/moumantai-gg/mithril/issues/1038) — `ProcessMapPinAdd` arg-A investigation (documented as "Opaque. Invariant 1 in every capture"; verification owed in aggregator subzones; outcome may add a one-line `Visible == 1` filter to the new pin provider).

**Related (upstream dependency):** [mithril#1021](https://github.com/moumantai-gg/mithril/issues/1021) — per-scene calibration keying. `IAreaReferenceProvider.ForArea` is renaming `string → MapSceneRef`; this work targets the post-#1021 signature.

**Related (downstream consumer):** [mithril#914](https://github.com/moumantai-gg/mithril/issues/914) — auto-calibration engine umbrella.

**Canonical references:**
- [`docs/player-pin-service.md`](../../player-pin-service.md) lines 44-60 — `ProcessMapPinAdd` arg grammar, Shape/Color decoded tables.
- [`docs/planning/map-calibration-1021-per-scene-keying/spec.md`](../map-calibration-1021-per-scene-keying/spec.md) — `MapSceneRef`, strict-gate semantics.
- [`CanonicalLandmarkTypes`](../../../src/Mithril.MapCalibration/CanonicalLandmarkTypes.cs) — vocabulary single-source-of-truth pattern.
- Live `sharedassets0.assets` byte-scan (PG 6000.3.11f1, 2026-06-03) — confirms pin sprite inventory.

---

## 1. Problem

`AutoCalibrationEngine` cannot calibrate any area whose only world-anchor references are user-placed map pins. Today the engine resolves references exclusively via `ReferenceDataAreaReferenceProvider`, which sources `landmarks.json` (Portal / MeditationPillar / TeleportationPlatform) and `npcs.json` (Npc). For PG areas with neither landmarks nor NPCs — dungeons, instanced sub-zones, the `AreaCave1` aggregator family — `ForArea` returns empty, the type-constrained RANSAC pool is empty, no inliers form, and the gate rejects.

This is silent inoperability, not a degraded fit. The user sees `RejectedSolveInsufficient` regardless of how many pins they have dropped, with no signal that pins could be used.

The fresh-world-coord property of map pins (the `ProcessMapPinAdd(1, Shape, Color, (X, 0.00, Z), "label")` line carries the world coord explicitly, the pin is static once placed) makes them the only viable additional anchor source available without invasive game-state instrumentation:

| Source | World coord availability | Sprite NCC viability |
|---|---|---|
| Map pin (`MapPin_Circle` / `MapPin_Square`) | **Fresh, exact**, from the log line | **Static, pivot-centred** (rotation invariant) |
| Player pin (`LocalPlayerPin_*`) | Stale — `IPlayerPositionTracker.Current` only refreshes on zone-in/teleport | Rotates 0–360° for facing; multi-angle sweep would 600× detection cost |
| Pet pin (`PetPin_*`) | Same staleness as player position | Static, but no usable world coord pairing |
| Landmark / NPC | Bundled JSON | Existing, working |

Pins are the only anchor source where both halves of the `(world, pixel)` pair are obtainable today.

## 2. Goal

Extend auto-calibration so that an area with **no landmarks and no NPCs** but **≥3 well-spread map pins** produces an accepted calibration without any user step beyond placing the pins. Concretely:

1. Add the two existing pin sprite assets (`MapPin_Circle`, `MapPin_Square`) to the bundled icon-template set so the detector emits typed detections for them.
2. Add two type discriminators to `CanonicalLandmarkTypes` (`MapPinCircle`, `MapPinSquare`) so detector and reference-provider sides speak the same vocabulary (per #974).
3. Add a new `MapPinAreaReferenceProvider : IAreaReferenceProvider` that maps each `IMapPinState.Pins` entry to a `LandmarkReference` typed by the entry's `Shape` int.
4. Compose the new provider alongside the existing reference-data provider via a thin `CompositeAreaReferenceProvider`, registered as the consumer-facing `IAreaReferenceProvider` in DI.
5. Emit a specific `RejectedNeedsMorePins` outcome with the user-facing string *"Drop ≥3 map pins at well-spread spots to enable auto-calibration for this area."* when references are pin-only and below the floor.
6. Introduce a shared decoder `MapPinDescriptor` in `Arda.Contracts` so the pin provider (and a sibling Palantir wire-up) render pin identity as human-readable strings (`Red Square "South"`) rather than raw ints.

Landmark-rich areas keep their existing behaviour byte-identical; pin-augmented areas get more inliers; pin-only areas get the v1 capability.

## 3. Ratified design decisions

Each row was litigated during the 2026-06-03 brainstorm. Option letters reference the alternatives surfaced at that time. No `Other` / open-ended outcomes — every decision is closed.

| Decision | Choice | Rationale |
|---|---|---|
| **D1. Pixel-half source** | Visual auto-detection (NCC against `MapPin_*` icon templates). | Keeps the autocal contract: the hotkey-fired headless pipeline still requires zero user clicks during the solve. The two existing sprites are 64×64, pivot-centred, and trivially NCC-discriminable from each other (curve vs corners). The user-click alternative (Legolas-style `PinCalibrationCoordinator` Drop/Pair) is rejected for v1 because it would not be autocal — it would be a separate manual flow. |
| **D2. Composition** | Always-augment via a new `CompositeAreaReferenceProvider`. | Pin provider and reference-data provider both contribute on every area. Landmark-rich areas get more inliers (more refs ⇒ better fit); landmark-free areas suddenly work. Rejected: fallback-only (would not improve landmark-sparse zones); per-area toggle (UX overkill if always-augment is clean). |
| **D3. Pin typing** | Per-shape: `MapPinCircle` and `MapPinSquare` `CanonicalLandmarkTypes` constants. | Verified empirically: only two sprite assets exist in `sharedassets0.assets` — `MapPin_Circle` and `MapPin_Square`. Hollow ring vs. hollow square frame at 16-px downsample is the most NCC-discriminable shape pair possible. Per-shape typing prevents cross-shape mis-pairs during RANSAC scoring with zero feasibility cost (the type-constrained pool needs only ≥1 ref per active type, not ≥2). Rejected: single-`MapPin` type (marginal noise gain). |
| **D4. Cold-start UX** | Status-hint string surfaced via `OutcomeVocabulary.RejectedNeedsMorePins` + `CalibrationStatusFormatter`. | When pin-only refs are below the floor (≥3), engine fails soft with a specific reason: *"Drop ≥3 map pins at well-spread spots to enable auto-calibration for this area."* No new UI. Rejected: silent fail (opaque); guided wizard (scope creep, separate work). |
| **D5. Cold-start hint placement** | After ref resolution, BEFORE asset-sidecar invocation. | Spare the sidecar / icon-template cost when the failure is certain. Diagnostic-bundle loss is acceptable (no actionable content in the partial bundle that other diagnostics don't also carry). |
| **D6. Pin name rendering** | `MapPinDescriptor.Describe(entry)` returning `"<Color> <Shape>"` or `"<Color> <Shape> \"<label>\""`. | World coords are math-internal; player never sees them in any PG UI. Shape + color is the canonical player-visible disambiguator. Shared helper lives in `Arda.Contracts` next to `MapPinEntry` so a sibling Palantir issue can consume the same decoder. |
| **D7. Player + pet pins** | Out of scope. | Both lack a fresh, paired world coord; the rotation cost of the player pin is the secondary blocker behind the world-coord-staleness primary blocker. Tracked as deferred-by-design, not verification-owed. |
| **D8. Seam alignment with #1021** | Target `IAreaReferenceProvider.ForArea(MapSceneRef)` from day one. | #1021 has a spec and plan on main; the `ForArea(string) → ForArea(MapSceneRef)` rename (D4 of #1021) is ratified. No merge race today (no PR for #1021 yet); pin-anchor work that targets the post-#1021 signature converges naturally regardless of merge order. |
| **D9. Aggregator-scene correctness** | Verification-owed; resolution arrives via the pin arg-A investigation sibling issue. | `IMapPinState.Pins` resets on `AreaChanged` (areas.json-keyed), NOT on `MapAssetChanged` (scene-keyed). In aggregator zones traversing between sibling Map_<X> scenes does not fire `AreaChanged`. Pins from different scenes may share the pin-state's view with world coords from different coordinate frames. Mitigations land at the parser level once arg-A semantics are known. |

## 4. Architecture overview

```
Player.log "LocalPlayer: ProcessMapPinAdd(1, Shape, Color, (X, 0.00, Z), \"label\")"
       │
       ▼
Arda.Dispatch ─► MapPins.OnAdd (existing)
       │  (upserts to _pins keyed by rounded (X,Z); publishes MapPinAdded)
       ▼
IMapPinState.Pins (existing; live snapshot, thread-safe by design)
       │
       ▼
Mithril.MapCalibration.Capture.MapPinAreaReferenceProvider     ← NEW IAreaReferenceProvider source
       │  (each MapPinEntry → LandmarkReference{Type, Name, World})
       │  Type via Shape switch: 0 → MapPinCircle, 1 → MapPinSquare, _ → drop
       │  Name via MapPinDescriptor.Describe(entry) — "Red Square \"South\""
       │  World = new WorldCoord(entry.X, 0, entry.Z)
       ▼
Mithril.MapCalibration.Capture.CompositeAreaReferenceProvider  ← NEW consumer-facing IAreaReferenceProvider
       │  (concatenates results from every IAreaReferenceProviderSource)
       ▼
Mithril.MapCalibration.Capture.AutoCalibrationEngine
       │  Cold-start hint (after refs resolved, before sidecar):
       │      references.Count < 3 && references.All(r => CanonicalLandmarkTypes.PinTypes.Contains(r.Type))
       │        → RejectedNeedsMorePins
       │  Otherwise: detect → solve → gate → persist (unchanged)
       ▼
Mithril.MapCalibration.Detection.WholeImageTemplateDetector
       │  (now iterates 6 templates: 4 landmark + 2 pin; type-buckets detections)
       ▼
Mithril.MapCalibration.Detection.TypeAwareRansacSolver
       │  (per-type candidate-ref pool; MapPinCircle detections pair with MapPinCircle refs)
       ▼
Existing gate + monotonicity (#988) + scale-aware regime (#1005) + persistence
```

**New shared decoder** (out-of-band of the pipeline diagram):

```
Arda.Contracts.State.Player.MapPinDescriptor   ← NEW static helper
  ShapeName(int) → "Dot" | "Square" | "Unknown"
  ColorName(int) → "White" | "Red" | ... | "Black" | "Unknown"
  Describe(MapPinEntry) → "Red Square" | "Red Square \"label\""

  consumed by:
    Mithril.MapCalibration.Capture.MapPinAreaReferenceProvider (this PR)
    Palantir.Module.Views.WorldStateView.xaml (sibling issue)
```

## 5. Layer-by-layer detail

### 5.1 Arda layer — shared decoder

**New file** (`src/Arda/Arda.Contracts/State/Player/MapPinDescriptor.cs`):

```csharp
namespace Arda.World.Player;

/// <summary>
/// Decodes the integer Shape + Color fields on <see cref="MapPinEntry"/> to
/// human-readable strings, per the canonical tables documented in
/// docs/player-pin-service.md lines 52-53.
///
/// <para>Single source of truth: <see cref="MapPinAreaReferenceProvider"/>
/// (auto-calibration) and Palantir's WorldStateView (display) both consume
/// this helper rather than re-implementing the lookups inline.</para>
/// </summary>
public static class MapPinDescriptor
{
    /// <summary>"Dot" for shape 0, "Square" for shape 1, "Unknown" otherwise.</summary>
    public static string ShapeName(int shape) => shape switch
    {
        0 => "Dot",
        1 => "Square",
        _ => "Unknown",
    };

    /// <summary>The 10-entry palette from the in-game pin-editor color row.</summary>
    public static string ColorName(int color) => color switch
    {
        0 => "White", 1 => "Red", 2 => "Orange", 3 => "Yellow", 4 => "Green",
        5 => "Cyan", 6 => "Blue", 7 => "Purple", 8 => "Pink", 9 => "Black",
        _ => "Unknown",
    };

    /// <summary>
    /// "Red Square" (no label) or "Red Square \"South\"" (with label). World
    /// coords are intentionally omitted — players never see them in any PG UI;
    /// the shape + color + label combo is the canonical human-visible identity.
    /// </summary>
    public static string Describe(MapPinEntry entry)
    {
        var prefix = $"{ColorName(entry.Color)} {ShapeName(entry.Shape)}";
        return string.IsNullOrWhiteSpace(entry.Label)
            ? prefix
            : $"{prefix} \"{entry.Label}\"";
    }
}
```

Pure code, no WPF, no DI. Lives next to `MapPinEntry` / `IMapPinState` in `Arda.Contracts/State/Player/`. Unit tests in `tests/Arda.Contracts.Tests/MapPinDescriptorTests.cs` cover the table + Unknown fallback + label-empty/non-empty paths.

### 5.2 Calibration core — vocabulary

**Modified** (`src/Mithril.MapCalibration/CanonicalLandmarkTypes.cs`):

```csharp
public static class CanonicalLandmarkTypes
{
    public const string Portal = "Portal";
    public const string MeditationPillar = "MeditationPillar";
    public const string TeleportationPlatform = "TeleportationPlatform";
    public const string Npc = "Npc";

    // NEW: user-placed map pin shape discriminators. Shape 0 → MapPinCircle,
    // Shape 1 → MapPinSquare per docs/player-pin-service.md line 52. Per-shape
    // typing prevents cross-shape mis-pairs during type-constrained RANSAC.
    public const string MapPinCircle = "MapPinCircle";
    public const string MapPinSquare = "MapPinSquare";

    public static readonly IReadOnlySet<string> LandmarkTypes =
        new HashSet<string>(StringComparer.Ordinal)
        { Portal, MeditationPillar, TeleportationPlatform };

    // NEW: the two pin types as a set, mirroring LandmarkTypes' allowlist
    // pattern. Used by AutoCalibrationEngine's cold-start hint to decide
    // whether ref-set is pin-only.
    public static readonly IReadOnlySet<string> PinTypes =
        new HashSet<string>(StringComparer.Ordinal)
        { MapPinCircle, MapPinSquare };

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Portal, MeditationPillar, TeleportationPlatform, Npc, MapPinCircle, MapPinSquare };
}
```

### 5.3 Calibration capture — pin reference provider

**New file** (`src/Mithril.MapCalibration.Capture/MapPinAreaReferenceProvider.cs`):

```csharp
public sealed class MapPinAreaReferenceProvider : IAreaReferenceProviderSource
{
    private readonly IMapPinState _pinState;
    private readonly ILogger? _logger;

    public MapPinAreaReferenceProvider(IMapPinState pinState, ILogger? logger = null)
    {
        _pinState = pinState;
        _logger = logger;
    }

    public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef scene)
    {
        var pins = _pinState.Pins;
        if (pins.Count == 0) return Array.Empty<LandmarkReference>();

        var refs = new List<LandmarkReference>(pins.Count);
        var unmapped = 0;
        foreach (var pin in pins)
        {
            var type = pin.Shape switch
            {
                0 => CanonicalLandmarkTypes.MapPinCircle,
                1 => CanonicalLandmarkTypes.MapPinSquare,
                _ => null,
            };
            if (type is null) { unmapped++; continue; }

            refs.Add(new LandmarkReference(
                Type: type,
                Name: MapPinDescriptor.Describe(pin),
                World: new WorldCoord(pin.X, 0, pin.Z)));
        }

        if (unmapped > 0)
        {
            // ThrottledWarn — a future PG patch could add a third shape; surface
            // the gap without spamming.
            ThrottledWarn.Log(_logger, $"Dropped {unmapped} pin(s) with unmapped Shape int.");
        }
        return refs;
    }
}
```

`MapSceneRef.SceneFriendlyName` is unused at this layer because pin state is already area-scoped via `MapPins.Reset`. Sub-zone semantics arrive once the arg-A investigation lands.

### 5.4 Calibration capture — composite + DI

**New marker interface** (`src/Mithril.MapCalibration.Capture/IAreaReferenceProviderSource.cs`):

```csharp
/// <summary>
/// Identifies a contributor to <see cref="CompositeAreaReferenceProvider"/>.
/// Separate from <see cref="IAreaReferenceProvider"/> (the consumer-facing
/// composite) so the composite's DI registration doesn't recursively resolve
/// itself.
/// </summary>
public interface IAreaReferenceProviderSource
{
    IReadOnlyList<LandmarkReference> ForArea(MapSceneRef scene);
}
```

`ReferenceDataAreaReferenceProvider` and `MapPinAreaReferenceProvider` both implement `IAreaReferenceProviderSource` (the existing provider's interface implementation changes — small mechanical edit).

**New file** (`src/Mithril.MapCalibration.Capture/CompositeAreaReferenceProvider.cs`):

```csharp
public sealed class CompositeAreaReferenceProvider : IAreaReferenceProvider
{
    private readonly IReadOnlyList<IAreaReferenceProviderSource> _sources;

    public CompositeAreaReferenceProvider(IEnumerable<IAreaReferenceProviderSource> sources)
    {
        _sources = sources.ToArray();
    }

    public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef scene)
    {
        var result = new List<LandmarkReference>();
        foreach (var src in _sources)
        {
            result.AddRange(src.ForArea(scene));
        }
        return result;
    }
}
```

**DI** (`src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`):

```csharp
services.AddSingleton<IAreaReferenceProviderSource, ReferenceDataAreaReferenceProvider>();
services.AddSingleton<IAreaReferenceProviderSource, MapPinAreaReferenceProvider>();
services.AddSingleton<IAreaReferenceProvider, CompositeAreaReferenceProvider>();
```

The marker-interface split prevents the composite from resolving itself; the DI graph has no `IAreaReferenceProvider → IAreaReferenceProvider` edge.

### 5.5 Calibration capture — engine cold-start hint

**Modified** (`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`):

After the existing `var references = _references.ForArea(scene);` line and the existing reference-count log line, before `EnsureIconTemplatesAsync`:

```csharp
// Cold-start hint: if pin-only refs are below the floor, return a specific
// reason BEFORE the icon-template sidecar runs. The asset-sidecar / icon
// sidecar pay-off is zero in this case (no detections can pair). The
// "all-pin" predicate distinguishes "no landmarks + few pins" (this hint)
// from "few landmarks + no pins" (existing RejectedSolveInsufficient).
const int PinFloorRefs = 3;
if (references.Count < PinFloorRefs
    && references.All(r => CanonicalLandmarkTypes.PinTypes.Contains(r.Type)))
{
    attempt.Outcome = OutcomeVocabulary.RejectedNeedsMorePins;
    return Fail(
        scene,
        "drop ≥3 map pins at well-spread spots to enable auto-calibration for this area",
        OutcomeVocabulary.RejectedNeedsMorePins);
}
```

`scene` here is the `MapSceneRef` resolved from `IMapState` per #1021's strict-gate D3 (the gate fires earlier; this code only runs when the scene is known).

### 5.6 Calibration capture — outcome vocabulary

**Modified** (`src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs`):

```csharp
public const string RejectedNeedsMorePins = "rejected-needs-more-pins";
```

**Modified** (`src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs`):

Adds the new category to the switch:

```csharp
OutcomeVocabulary.RejectedNeedsMorePins =>
    "Drop ≥3 map pins at well-spread spots to enable auto-calibration for this area.",
```

### 5.7 Asset layer — sidecar + bundled icons

**Modified** (`tools/Mithril.MapCalibration.Tools.Common/IconTemplateExtractor.cs`):

```csharp
private static readonly (string TextureName, string LandmarkType)[] LandmarkIcons =
[
    ("landmark_telepad", CanonicalLandmarkTypes.TeleportationPlatform),
    ("landmark_medipillar", CanonicalLandmarkTypes.MeditationPillar),
    ("landmark_portal", CanonicalLandmarkTypes.Portal),
    ("landmark_npc", CanonicalLandmarkTypes.Npc),
    // landmark_star is generic waypoint; skip — no Type to match on.
    // User-placed map pin sprites — hollow outline shapes, pivot centred.
    // Color tint is applied at runtime; NCC is luma-normalized so it's a no-op.
    ("MapPin_Circle", CanonicalLandmarkTypes.MapPinCircle),
    ("MapPin_Square", CanonicalLandmarkTypes.MapPinSquare),
];
```

**Sidecar re-run + bundle regeneration:**

```bash
dotnet run --project tools/Mithril.AssetExtractor -c Release -- \
  --install "<PG install>" --out "%LocalAppData%\Mithril\assets" --icons \
  --tpk "%LocalAppData%\Mithril\assets\classdata.tpk"
```

Produces a new `icon-templates.json` + `icon-templates.bin` with 6 entries and a new `pixelSha256`. The bundled blobs under `src/Mithril.MapCalibration.Detection/BundledData/` are replaced from the sidecar output.

**Canonical hash gate update** (`src/Mithril.MapCalibration.Detection/Internal/CanonicalAssetHashes.cs`): add the new `pixelSha256` for the 6-icon manifest. The existing hash for the 4-icon manifest is retired (or kept as a back-compat alias if appropriate at PR-review time; default = retired since we're shipping the new blob).

## 6. Test strategy

Three layers, mirroring the codebase's existing autocal test discipline (`xunit + FluentAssertions`, per-layer test projects, fakes for the engine layer, synthetic frames for the integration layer, gated PG-asset tests for the sidecar).

### 6.1 Unit tests

- `Arda.Contracts.Tests / MapPinDescriptorTests` — table-driven over `ShapeName(int)`, `ColorName(int)`, `Describe(MapPinEntry)`. Unknown fallback paths. Label empty + non-empty.
- `Mithril.MapCalibration.Capture.Tests / MapPinAreaReferenceProviderTests` — provider returns one ref per pin; Type via Shape switch; Name via `MapPinDescriptor.Describe`; unmapped Shape drops with throttled warn; `MapSceneRef` parameter is ignored (pins are area-scoped at source); snapshot is stable across calls.
- `Mithril.MapCalibration.Capture.Tests / CompositeAreaReferenceProviderTests` — concatenates all sources; preserves order; handles empty set; one empty source doesn't poison the result.
- `Mithril.MapCalibration.Capture.Tests / AutoCalibrationEngineTests` (cold-start hint) — pin-only refs below 3 → `RejectedNeedsMorePins`; mixed refs below 3 → falls through (existing `RejectedSolveInsufficient`); pin-only refs ≥3 → falls through; landmark-only refs below 3 → falls through (legacy behaviour preserved).
- `Mithril.MapCalibration.Tests / CanonicalLandmarkTypesTests` — `PinTypes` contains both new constants; `All` includes them; lexical ordinal consistency vs. `IconTemplate.LandmarkType` matching.

### 6.2 Integration tests

- `Mithril.MapCalibration.Detection.Tests / MapCalibrationSolveEngineTests` (extends existing) — synthetic screenshot with N fake pin detections + their refs at well-spread world coords; verify residual < gate; calibration non-null. Clustered detections (all within 50 px) → existing 100-px bbox guard rejects. Mixed pin+landmark → inlier set contains both types. Pin shape mismatch (`MapPinCircle` detection, only `MapPinSquare` refs) → detection dropped.
- `Mithril.MapCalibration.Tools.Tests / IconTemplateExtractorTests` (gated on PG install present) — extracts the 2 new sprites; manifest declares the matching `LandmarkType` strings; `pixelSha256` matches `CanonicalAssetHashes`.

### 6.3 Regression tests

- Landmark-rich-area no-pins path stays byte-identical via `PerfTracerTests`-style replay over the existing accepted bundles (e.g. `AreaEltibule-…-accepted`). Outcome category, residual, inlier count, persisted `AreaCalibration` must equal today's values to the bit.

### 6.4 Manual verification

Owner: @arthur-conde. Captures land in the PR's test-plan checklist.

1. Launch shell from the worktree.
2. In `AreaEltibule` (landmark-rich): re-run autocal → outcome unchanged from main; bundle's `04-references.json` lists only landmark/NPC refs.
3. Drop 3 well-spread pins in `AreaEltibule` → re-run autocal → outcome accepted; bundle shows mixed landmark + pin refs; residual marginally lower (more inliers).
4. Enter `AreaCave1` (the already-captured `AreaCave1-…-rejected` case): with 0 pins, run autocal → outcome `RejectedNeedsMorePins`; status reads the new hint.
5. Drop 3 well-spread pins in `AreaCave1` → re-run autocal → outcome accepted; overlay projection matches actual map landmarks visible in the dungeon.
6. Cross-reference findings against the pin arg-A investigation (sibling issue) if it has landed first.

## 7. Out of scope

- **Pet pins** (`PetPin_*`) — pet world coord has the same staleness as player position; no fresh anchor source. Designed-against, dropped at brainstorm. Not deferred-pending-investigation; deferred-by-design.
- **Player pin** (`LocalPlayerPin_*`) — facing-arrow rotates 0–360°; multi-angle NCC sweep would 600× detection cost per the existing [extractor comment](../../../tools/Mithril.MapCalibration.Tools.Common/IconTemplateExtractor.cs). World coord is stale anyway.
- **Palantir display decode** — Palantir's `WorldStateView.xaml` lines 156-159 render raw `Color N · Shape M` ints. Tracked in [mithril#1037](https://github.com/moumantai-gg/mithril/issues/1037); wires through a value converter to `MapPinDescriptor`.
- **Pin arg-A semantics** — `ProcessMapPinAdd` arg A is "Opaque. Invariant 1 in every capture" per existing docs; unsampled in aggregator subzones. Tracked in [mithril#1038](https://github.com/moumantai-gg/mithril/issues/1038). Resolution may add a one-line `Visible == 1` filter to `MapPinAreaReferenceProvider`; v1 ships without.
- **Wizard UI** — the cold-start status hint is the v1 affordance; a guided wizard ("drop a pin in each corner of the area") is potential future work but not a v1 blocker.

## 8. Verification owed

- **Cross-shape NCC discriminability at 16 px in vivo.** Empirically: the two sprite assets are maximally distinct (curve vs. corners), and NCC handles the contrast cleanly in theory. The empirical NCC scores against live captures will land at implementation time; if the centre-dot overlay (added by PG at render time, not in the sprite) lowers scores enough that `LowNcc = 0.5` rejects them, the threshold may need a `MapPin*`-specific override. Implementation watch-out, not v1 blocker.
- **Aggregator-scene pin scoping.** Pin state is per-area-keyed; calibration is per-scene-keyed after #1021. In aggregator zones, pins from sibling sub-scenes may share the live pin state. Verification arrives via the pin arg-A investigation. v1 ships with the verification-owed note; resolution backports as a one-line filter when arg-A semantics are confirmed.
- **#1021 merge ordering.** This work targets `IAreaReferenceProvider.ForArea(MapSceneRef)`. If #1021 has not yet merged at this PR's land time, the seam rename + a small `MapSceneRef` shim land together; if #1021 has merged, the signature is already in place.
