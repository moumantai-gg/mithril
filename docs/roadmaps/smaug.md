# Smaug · Vendor Prices — roadmap

> **Active backlog:** [Mithril Roadmap Project — `Module: Smaug`](https://github.com/users/arthur-conde/projects/3/views/1?filterQuery=module%3A%22Smaug%22).

What shipped in v1 and the rationale for what was deliberately deferred. Per-item *task tracking* lives in Issues; this doc keeps the *why* and the design-decision narrative.

## Context

Smaug is the hoarding dragon that helps you min-max value-to-council at NPC vendors. v1 was scoped around a single insight from analyzing live Player.log behavior:

- **Sells are fully observable.** `ProcessVendorAddItem(price, Item(instanceId), bool)` logs the exact price the vendor paid, and `ProcessVendorScreen` logs the favor tier in effect. We can calibrate sell prices purely from passive log observation.
- **Buys are not observable with price.** Buying a Bottle of Water for 100c produced only a plain `ProcessAddItem(BottleOfWater, -1, True)` — indistinguishable from any other inventory addition. The CDN item `Value: 11` was off by ~9× from what we paid, so the community wiki's "2-3× markup" heuristic is unreliable.
- **sources_items.json tells us *who* sells an item, but not what they charge.** Useful for the Catalog tab, not for buy-price estimation.

v1 is therefore log-driven sell-price calibration + a passive catalog. Future versions can close the buy-price gap with manual entry or empirical crawling.

## What shipped in v1

- Log parser for `ProcessVendorScreen`, `ProcessVendorAddItem`, `ProcessVendorUpdateAvailableGold`, `ProcessStartInteraction`, Civic Pride, and player login.
- Ingestion service attributes each sell to (NpcKey, FavorTier, CivicPrideLevel) and records observations.
- Two rate tables: absolute price (fixed-Value items) keyed by `NpcKey|InternalName|FavorTier|CivicPrideBucket`, Value-ratio (variable-Value items like augments/gear) keyed by `NpcKey|KeywordBucket|FavorTier|CivicPrideBucket`.
- Three tabs: **Vendor Catalog** (sources_items.json × npcs.json), **Sell Prices** (learned rates), **Calibration** (recent observations).
- Community pipeline: `VendorRatesPayload` schema v1, merger with PreferLocal / Blend / PreferCommunity modes, fetch from `mithril-calibration` GitHub repo.
- Settings pane with calibration source toggle, persisted to `%LocalAppData%/Mithril/Smaug/settings.json`.
- 15 unit tests covering parser, rate aggregation, Civic Pride bucketing, tier ordering, and community merger.

---

## Out of scope for v1

### 1. Buy-price estimation / tracking

**Why deferred:** The Player.log contains no price-tagged buy event. Log parsing alone cannot produce a ground-truth signal. Estimating from `Value × fixed multiplier` fails — the Bottle of Water observation showed a ~9× markup, not 2-3× as the wiki suggests, and the multiplier almost certainly varies per vendor / per category.

**Likely approach for v2:**
- Manual entry UI: when a user knows a buy price, let them input it against (vendor, item). Persist as a separate dictionary (`buyPriceObservations`) with its own schema-versioned community payload.
- Empirical gold-delta crawl: track player-owned gold across `ProcessAddItem` events that follow a `ProcessVendorScreen` but no corresponding `ProcessVendorAddItem`. Requires finding the "current gold" log signal — we haven't surveyed for one yet. If it exists (e.g., inside `ProcessUpdateAttributes` under `Currency`), automated buy tracking becomes feasible.
- Community-authoritative fallback: let users with verified prices submit `BuyPriceEntry { vendor, item, price, verifiedAt }` contributions to the mithril-calibration repo; aggregation picks the mode.

### 2. Vendor gold-pool tracking

**Why deferred:** `ProcessVendorUpdateAvailableGold(remaining, reset, cap)` is parsed and emitted as an event, but we don't persist or display it. It would power a "which vendors still have budget to buy this week?" feature.

**Likely approach:** A `VendorGoldState` service keyed by `NpcKey`, persisted per character. Show a "budget remaining" column on Sell Prices filtered by cap family (Armor/Weapon/Food/…) so the player can pick the vendor with the most headroom.

### 3. Barter / Consignment / Training service tracking

**Why deferred:** `npcs.json` exposes `Services` for all five types (Store, Barter, Consignment, Training, Stables, etc.) but the game log emits type-specific events only for Store. Parsers for `ProcessBarter*`, `ProcessTrainingScreen`, etc. weren't surveyed.

**Likely approach:** Per-service sub-parsers emitting sibling event types; tab for each in the same catalog shape. Barter has the most player-visible variance so it's the next-most-valuable after Store.

### 4. Recipe-input source annotations

**Why deferred:** `sources_items.json` contains `Recipe`, `HangOut`, `NpcGift`, `Quest`, `Barter`, `Monster`, `Angling` source types alongside `Vendor`. The Vendor Catalog tab filters to `Vendor` only. Broader coverage would let the Catalog answer "how do I obtain this item at all?"

**Likely approach:** Multi-source catalog view with a Type column and filter chips; reuse existing `ItemSource.Context` to surface the specific recipe/quest/monster name.

### 5. Bundled sources_items.json

**Why deferred:** The CDN refresh pulls it on first launch, so v1 ships functional-but-empty until the first successful refresh. For a fully-offline first-run experience, bundle a copy under `src/Mithril.Shared/Reference/BundledData/sources_items.json` (~2-3 MB).

**Action:** Download once from `https://cdn.projectgorgon.com/v{version}/data/sources_items.json`, save to the bundled dir, commit. The existing load path picks it up.

### 6. Sell-price estimator in the UI

**Why deferred:** `PriceCalibrationService.EstimateSellPrice(npcKey, item, favor, civicPride)` is implemented and tested via the bucket fallback hierarchy, but no UI consumes it yet. The Sell Prices tab shows raw rate rows, not "for item X at vendor Y you'd get ~Zc".

**Likely approach:** Add a fourth tab **"Sell Planner"** with an item picker + current-favor dropdown + Civic Pride input that renders a sorted-by-price list of vendors who will buy it, grayed out for vendors below the player's actual favor. Uses the existing estimator.

### 7. Civic Pride from character export

**Why deferred:** Civic Pride is parsed from the `ProcessLoadSkills` line at session start, but only while the ingestion service is running. If the player activates Smaug lazily (mid-session, after ProcessLoadSkills has already scrolled past), the level stays at 0 until the next login.

**Likely approach:** Read Civic Pride from `Character_*.json` exports via `ICharacterDataService` as a fallback. The Arwen pattern for favor tier does this already — mirror it.

### 8. Cap-increases enforcement

**Why deferred:** We parse NPC `Services[].CapIncreases` into `NpcStoreCapIncrease` records with `FavorTier`, `MaxGold`, and `Keywords`, but only surface `MinFavorTier` in the Catalog tab. The cap data could drive "this vendor won't buy this item at your current favor" warnings.

**Likely approach:** Extend the catalog row with a computed `Accepts` boolean = `item.Value ≤ maxGold(tier × keywords match)` given the player's current favor. Requires joining with the Arwen favor state service.

---

## History

- **2026-04-30** — backlog migrated from inline checklist into [issues #33–#41](https://github.com/moumantai-gg/mithril/issues?q=is%3Aissue+label%3Amodule%3Asmaug) as part of the docs-wiki-projects three-tier reorganization. v1.1 candidates (sources_items bundle, Civic Pride from export) and the Sell Planner tab tagged for prioritisation in the `Mithril Roadmap` Project.
