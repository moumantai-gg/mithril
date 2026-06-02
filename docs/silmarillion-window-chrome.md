# Detail / popup window chrome — the open #404 conformance question

> **Status:** open design question. The DRY half (centralising the duplicated
> chrome) is **done** (this doc's "What shipped"). The conformance half
> (should the frame obey the visual grammar, and how) is **not decided** — it
> needs a design pass / gate, not free-handing. See the tracking issue for the
> task side; this doc holds the *why* and the options.

## The observation

The #404 visual-grammar program migrated every Silmarillion detail **view
body** to the ratified grammar primitives (`FactTitleStyle`, `FactTable`,
`Link`, `SetRef`, `FactFooter`, the `--accent` / `--fg-*` token vocabulary).
The **window that frames those views was never touched** — and it is identical
across all 11 such windows:

- `src/Mithril.Shared.Wpf/ProvenancePopupWindow.xaml` (the set/member-list popup)
- the 10 `src/Silmarillion.Module/Views/*DetailWindow.xaml`
  (Ability, Area, Effect, Lorebook, Npc, PlayerTitle, Quest, Recipe,
  StorageVault, Treasure)

Each hand-rolled the same dark chrome: frame `#FF1A1A1A` / border `#33FFFFFF`,
title bar `#FF202020`, **title as a bespoke `AppHeaderFontFamily` `#FFD4A847`
gold `TextBlock` — not `FactTitleStyle`**, subtitle / section headers
`#88FFFFFF`.

## Why this is *not* a #404 regression

This was investigated before acting:

1. **The grammar deliberately scopes itself out of the frame.**
   `docs/silmarillion-visual-grammar.md` § "What this doc does NOT do" bounds
   the spec to the five *content* tiers; line 63 is explicit — *"Structure is
   a frame, not a finding."* There is **no ratified spec** for window/popup
   chrome anywhere.
2. **Chrome was never in any #404 phase's scope.** The Phase-5 fan-out plan's
   out-of-scope list is *"coverage #407/#408, the #214 Effects-stub"*. Window
   chrome is not named as in-scope, deferred, *or* excluded — it was simply
   outside the program's entire remit. The migration swept *view-body call
   sites*; the frame was never claimed.
3. **No shared chrome style was ever intended.** `Resources.xaml` had
   `FactTitleStyle` / `FactBodyStyle` (content) but no window-chrome style. The
   11× duplication **predates #404 entirely**. The grammar made the contrast
   *visible*; it did not create it.

So "make the popup obey the visual language" has **no authoritative target to
conform to** — deciding one is new design, not mechanical migration.

## The real tension this exposes (the G-b problem)

The grammar's central rule **G-b** ("gold reads as *title* — serif, large, no
glyph — or *link*; two unambiguous patterns; one gold title per pane") is
obeyed inside the view body but **violated by the frame around it**: open any
entity in a popup and the gold title renders **twice, in two different
languages** — the old bespoke `AppHeaderFontFamily` gold in the title bar, and
the new `FactTitleStyle` Cambria-serif gold in the body beneath it. The
grammar has **no ratified answer** for the popped-into-a-window case.
`ProvenancePopupWindow`'s gold title + `#88FFFFFF` "N total" subtitle +
`#88FFFFFF` section headers are the same violation for set display.

## What shipped (the DRY half — done)

The pre-existing identical pigment/typography was extracted to four keyed
styles in `src/Mithril.Shared.Wpf/Resources.xaml`, and all 11 windows now
reference them:

| Key | Was (inline, ×11) |
|---|---|
| `MithrilChromeWindowFrameStyle` | `#FF1A1A1A` / `#33FFFFFF` / 1 / radius 6 |
| `MithrilChromeWindowTitleBarStyle` | `#FF202020` / radius 6,6,0,0 / pad 14,10 |
| `MithrilChromeWindowCloseButtonStyle` | 24×24 transparent hand "Close" button |
| `MithrilChromeWindowTitleTextStyle` | `AppHeaderFontFamily` / XLarge / `#FFD4A847` |

**The look is preserved byte-for-byte** — this is a pure refactor. Its only
purpose is to make the conformance decision below a **single-file edit**.

Intentionally left inline (out of scope for the DRY pass):

- **Window-level behavioural attrs** (`WindowStyle=None`,
  `AllowsTransparency`, `SizeToContent`, etc.) — not pigment, not the
  grammar's concern, and a Window-self `Style` via the window's own merged
  dictionary hits the classic *StaticResource-parses-before-`Window.Resources`*
  ordering pitfall. Untouched on every window.
- **Per-window `MinWidth` / `MaxWidth` / `MaxHeight`** — 4 distinct size bins
  (360/620, 320/520, 420/680/h760, Treasure 420/760, popup 360/560/h640).
- **Residual `#88FFFFFF`** — the close-`X` glyph (all 11) and the
  `ProvenancePopupWindow` subtitle + section headers. These are child-content
  pigment; folding them in belongs to the conformance decision, not the
  no-op DRY pass. Inventoried here so they are not lost.

## The open decision (the conformance half — not decided)

| Option | Sketch | Cost / risk |
|---|---|---|
| **A — Frame is deliberate non-grammar chrome** | Ratify that the dark window shell is *intentionally* outside the grammar (it is OS-window chrome, not a content finding). Title bar keeps a distinct treatment; resolve the double-title by **suppressing the body `FactTitleStyle` when hosted in a window** (the title bar is the pane's one title). | Lowest churn. Needs a body/window "am I popped?" signal. Settles G-b by declaring the frame exempt. |
| **B — Conform the frame to the grammar** | Title bar adopts `FactTitleStyle` + token vocabulary (`--bg-surface` / `--border-*` / `--fg-tertiary` for the subtitle); the body title is then the duplicate and is suppressed instead. Pull `#88FFFFFF` into tokens too. | Highest fidelity. Now a 1-file edit thanks to the shipped DRY. Still needs the double-title resolution + a gate (it is a visual change to 11 surfaces). |
| **C — Hybrid** | Frame keeps its shell pigment (A) but the *title text* becomes `FactTitleStyle` so there is one gold-title language system-wide; subtitle/headers move to tokens. | Middle. Smallest visible change that kills the two-gold-languages problem without re-pigmenting the shell. |

All three require resolving **where the single per-pane gold title lives when
a detail is shown in a window** — that is the actual G-b gap, and it is a
design call for the #404 owners / Claude Design, not an implementation
default.

## Pointers

- `docs/silmarillion-visual-grammar.md` — G-b, § "What this doc does NOT do".
- `docs/planning/silmarillion-404-visual-grammar/phase5-fanout.md` — scope list
  proving chrome was never in remit.
- `src/Mithril.Shared.Wpf/Resources.xaml` — the four `MithrilChromeWindow*`
  keys (the single swap point for options B/C).
