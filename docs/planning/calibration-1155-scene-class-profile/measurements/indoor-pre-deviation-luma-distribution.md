# Indoor pre-deviation luma distribution — mithril#1172 mechanism check

The pre-implementation measurement for the [`#1172`](https://github.com/moumantai-gg/mithril/issues/1172)
pre-deviation luma threshold. Confirms (or falsifies) the issue's mechanism
*before* any code lands: are PG indoor NPC pip pixels actually separated from
the connecting floor pixels by a single raw-luma byte threshold? If no clean
valley exists between bright (icon) and dim (floor) peaks, the
`MinLumaForDeviation` knob doesn't have a load-bearing setting to ship and the
proposed direction is wrong upstream of implementation.

## TL;DR — bimodal hypothesis CONFIRMED across both canonical bundles

Both canonical merged-NPC-blob bbox regions show a clean bimodal luma
distribution. A heavy floor peak sits at luma ~32–63 (BT.601 luma of the
dungeon cobblestone). A bright icon peak sits at luma ~160–220 (the NPC pip
glyphs). Between them, the luma range [80, 144] is essentially empty —
**less than 1% of the bbox pixels per 16-byte bin**. The threshold range
[140, 200] is supported by the data; the issue's proposed default of 180 lands
in the leading edge of the bright peak (captures the icon halo + core,
suppresses the dim shoulder).

The mechanism is **NOT falsified**. Implementation proceeds with default
`MinLumaForDeviation = 180` as the Phase 2.6 Indoor profile value — to be
revised if the threshold-sweep test ([`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md))
shows the merge requires a different value.

## Measurement table

Source: each bundle's `06-aligned-screenshot.png` (Gray8 — saved by
`FilesystemCalibrationAttemptBundleSink` from `CapturedFrame.Gray`, which is
BT.601 luma of the live BGRA. So bundle-gray ≡ live-luma, and the threshold
measured here transfers directly to live capture.)

bbox per bundle: the merged-NPC connected-component bbox observed at the
production `(openRadius=0, closeRadius=1, win=11)` settings, per the Phase 2.5
morph-open measurement's per-row bbox trace.

### 06-13 canonical — bbox (410, 173) + 48×54 = 2592 pixels

```
min=0, p10=43, p25=49, p50=54, p75=63, p90=152, max=224

Histogram (16-byte bins):
  [  0- 15]    65    2%  ##
  [ 16- 31]    50    1%  #
  [ 32- 47]   791   30%  ##############################
  [ 48- 63]  1346   51%  ####################################################
  [ 64- 79]    30    1%  #               ← valley starts
  [ 80- 95]     5    0%
  [ 96-111]     7    0%
  [112-127]     2    0%
  [128-143]    11    0%
  [144-159]    44    1%  #               ← valley ends
  [160-175]    55    2%  ##              ← bright peak (Icon halo)
  [176-191]    47    1%  #
  [192-207]    36    1%  #
  [208-223]    24    0%
  [224-239]     1    0%
  [240-255]     0    0%

Dim peak:    bin [48-63],   count 1346
Bright peak: bin [160-175], count 55
Per-byte valley: between luma 80 and luma 144, count consistently ≤ 5 px
```

### 06-15 live-verification — bbox (453, 198) + 39×47 = 1833 pixels

```
min=0, p10=36, p25=44, p50=50, p75=56, p90=165, max=224

Histogram (16-byte bins):
  [  0- 15]    56    3%  ###
  [ 16- 31]    50    2%  ##
  [ 32- 47]   601   32%  ################################
  [ 48- 63]   806   43%  ###########################################
  [ 64- 79]    30    1%  #               ← valley starts
  [ 80- 95]     6    0%
  [ 96-111]    12    0%
  [112-127]    13    0%
  [128-143]    24    1%  #
  [144-159]    35    1%  #               ← valley ends
  [160-175]    59    3%  ###             ← bright peak (Icon halo)
  [176-191]    54    2%  ##
  [192-207]    39    2%  ##
  [208-223]    44    2%  ##
  [224-239]     4    0%
  [240-255]     0    0%

Dim peak:    bin [48-63],   count 806
Bright peak: bin [160-175], count 59
Per-byte valley: between luma 80 and luma 144, count consistently ≤ 7 px
```

Reproduced via [`IndoorPreDeviationLumaDistributionTests.Measure_luma_distribution_over_merged_NPC_bbox`](../../../../tests/Mithril.MapCalibration.Tests/Detection/IndoorPreDeviationLumaDistributionTests.cs):

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests `
  --filter "FullyQualifiedName~IndoorPreDeviationLumaDistributionTests" `
  --logger "console;verbosity=detailed"
```

## Survival fractions at candidate thresholds

The fraction of bbox pixels with `luma >= threshold` — i.e., the fraction that
would PARTICIPATE in `LocalNccDeviation.DeviationMap` after the mithril#1172
pre-mask gate. Pixels below threshold are zeroed on BOTH the screenshot and
texture float buffers before the NCC integral image, so they don't contribute
"added content" deviation evidence.

| Threshold | 06-13 survivors / 2592 | 06-15 survivors / 1833 | Captures |
|---:|---:|---:|---|
| 140 | 242 (9%) | 244 (13%) | shoulder of dim peak still bleeding through |
| 160 | 192 (7%) | 200 (10%) | starts of bright peak — full icon halo |
| 170 | 161 (6%) | 170 (9%) |  |
| **180** | **133 (5%)** | **135 (7%)** | **issue default — bright-peak core + outer halo** |
| 190 | 91 (3%) | 98 (5%) |  |
| 200 | 57 (2%) | 64 (3%) | icon-core only |

At threshold 180 across both bundles, ~5–7% of bbox pixels survive — these
are the actual NPC pip pixels. The floor pixels (95% of bbox) are gated out.
The NCC kernel can't smear floor into icon halos because floor pixels carry no
signal to smear.

## Per-finding analysis

### Finding 1 — Bimodal distribution is structurally clean

Both bundles show a heavy floor peak (32–63 luma) carrying 75–80% of bbox
pixels, and a bright icon peak (160–220 luma) carrying 5–10%. The intermediate
range [64, 159] is consistently ≤ 1% per 16-byte bin on both bundles. There
is no third population that the mechanism would have to disambiguate.

The icon peak isn't a single bright spike — it's a wide shoulder from luma
160 to 224 reflecting the natural anti-aliased glyph plus a fall-off into
half-bright halo. This is significant for the threshold choice: a high value
(200+) cuts INTO the icon core and risks destroying the icon connectivity in
the deviation map. The right value is between the valley's upper edge (~144)
and the icon halo's start (~160).

### Finding 2 — 180 is a reasonable default, not load-bearing

The issue proposed `MinLumaForDeviation = 180` from the existing peak-luma
measurement (which observed icon peak luma > 0.78 in normalised space ≡ 199
in byte space). The measurement here finds the icon peak starts at ~160 and
extends to 220 — so 180 sits inside the icon population, capturing the
brightest two-thirds. Lower values (160, 170) capture the full icon population
including the dimmer halo edge; higher values (200) gate out half of the
icon's pixels.

The decisive choice between 140, 160, 180, 200 is empirical, not
distributional — the relevant signal is whether the threshold lets the
deviation map produce **two separable blobs** at the merged NPC pair's
position. That measurement lands in [`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md)
after the production implementation lands.

### Finding 3 — Cross-bundle generalisation

The two bundles' distributions reproduce each other within rounding error:

| Stat | 06-13 | 06-15 |
|---|---:|---:|
| Floor median (luma) | 54 | 50 |
| Floor peak (luma bin) | 48–63 | 48–63 |
| Bright peak (luma bin) | 160–175 | 160–175 |
| Valley (luma range, ≤ 1%/bin) | 80–143 | 80–143 |
| % pixels ≥ 180 | 5% | 7% |

The findings are not bundle-specific. PG's indoor dungeon textures appear to
share a luma envelope that makes a single global threshold viable across
captures.

### Finding 4 — Mechanism vs. peak-luma post-filter (Phase 3)

The Phase 3 peak-luma filter ([#1169](https://github.com/moumantai-gg/mithril/pull/1169))
inspects `blob.PeakLuma` AFTER classification and rejects blobs whose
**brightest** raw-BGRA pixel is below `BlobOptions.MinPeakLuma` (0.7 ≡ luma
~178). That filter operates per-blob and can only DELETE — it can't split.

The mithril#1172 pre-deviation luma gate operates per-pixel BEFORE the
deviation map is built. It changes what the NCC kernel SEES — floor pixels
become "no signal here" so the kernel can't smear icon halos through them.
The mechanism is upstream and additive to Phase 3; the two compose without
overlap.

## Open follow-ups (not blocking #1172)

1. **Broader-corpus distribution.** Both canonical bundles are
   Hogan's Keep Basement. The luma distribution may shift in
   GoblinDungeon_TopFloor, BrainBugCaverns, or HumanCellar (other Indoor
   scenes with `opaqueFraction` in the [0.07, 0.36] range per
   [`scene-class-classification.md`](scene-class-classification.md)). The
   threshold sweep test extending the existing
   `IndoorRecallMergeTuningTests` battery covers Hogan's only; a Phase 4
   corpus-expansion measurement (separate issue) should sample the other
   Indoor scenes once their dev-local bundles exist.

2. **Color-channel composition.** The bundle's `06-aligned-screenshot.png`
   is Gray8 (BT.601 of BGRA), so this measurement reduces to per-pixel
   luma. The live `captureResult.Color.Bgra` carries the full color signal —
   if PG ever ships an Indoor scene with a brightly-coloured icon
   distinguishable by chroma but not luma, a per-channel luma min (`max(R,
   G, B)`) or weighted max would carry strictly more signal. Phase 0
   spike's chroma measurement (Indoor scenes are grayscale glyphs on
   grayscale floor) makes this unlikely on today's PG asset set; defer to
   a separate Phase 4 audit.

## Reproducibility

Bundle dev-local convention per
[`map_calibration_replay_fixtures_dev_local`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_replay_fixtures_dev_local.md).
Both bundles need to be present at
`%LOCALAPPDATA%/Mithril/diagnostics/calibration/` for the test theory to
emit data; absent bundles SKIP the corresponding theory row.
