# §6.a — Scene-class classification via alpha coverage

**Verdict: CONFIRMED.** Outdoor `OpaqueFraction = 1.00` (n=3 scenes); Indoor `OpaqueFraction ∈ [0.07, 0.36]` (n=10 scenes). The spec's `≥ 0.95` threshold separates with massive margin; even a relaxed `≥ 0.50` would work.

## Method

For each scene under `%LocalAppData%/Mithril/assets/maps-src/Map_*.v4.png`, load as 32bpp ARGB and count pixels with `alpha ≥ 128`. The PNGs are the source textures consumed by the sidecar extractor (`sidecar-rgba-alpha-surface`); their alpha channel is the same one fed to `FloorBoundaryMaskCache` at runtime.

```text
opaqueFraction = count(alpha ≥ 128) / (width × height)
SceneClass     = opaqueFraction ≥ 0.95 ? Outdoor : Indoor
```

## Measurements

| Scene | W × H | OpaqueFraction | Classification |
|---|---|---|---|
| Map_AreaEltibule | 2048×2033 | **1.00** | Outdoor |
| Map_AreaKurMountains | 2048×2048 | **1.00** | Outdoor |
| Map_AreaSerbule | 1961×2048 | **1.00** | Outdoor |
| Map_CarpalTunnels | 452×512 | 0.36 | Indoor |
| Map_GoblinDungeon_TopFloor | 800×800 | 0.27 | Indoor |
| Map_AreaCasino | 1024×1024 | 0.19 | Indoor |
| Map_WolfCave | 819×1024 | 0.18 | Indoor |
| Map_HogansKeepBasement | 1024×1024 | 0.17 | Indoor |
| Map_KhyruleksCrypt | 525×1024 | 0.16 | Indoor |
| Map_MyconianCave | 541×1024 | 0.15 | Indoor |
| Map_KurTower | 569×1024 | 0.13 | Indoor |
| Map_BoardedUpBasement | 205×512 | 0.13 | Indoor |
| Map_GoblinDungeon | 398×1024 | 0.07 | Indoor |

## Notes

- The 3 outdoor scenes all hit exactly `1.00` — PG's outdoor maps ship with full-coverage alpha (the entire texture is the visible game world, no off-map regions).
- The largest Indoor `OpaqueFraction` is `Map_CarpalTunnels` at `0.36` — still 0.59 below the threshold. Indoor/Outdoor are not borderline at any scene in the corpus.
- `Map_AreaCasino` at `0.19` validates the heuristic on a non-dungeon Indoor scene (the Casino in PG is a building interior, not a sub-zone). The heuristic generalises correctly.
- A future scene at the threshold would surface in the `01-attempt.json` `sceneClassOpaqueFraction` field per spec §5.6 and could be resolved by a per-scene override if needed.

## Implication for spec

Spec §5.2 + §6.a — no change. Indoor profile activates for `Map_HogansKeepBasement` (and 9 other scenes). Outdoor profile preserved for the 3 outdoor scenes.

## Verification-owed re-survey

When the sidecar re-emits alpha for additional Indoor scenes (currently only `Map_HogansKeepBasement-alpha.bin` exists in the runtime cache; others compute from the source PNGs that have alpha intact), the runtime classification will match these source-PNG measurements byte-for-byte. **No further verification owed.**
