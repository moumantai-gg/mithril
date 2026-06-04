# overlay-965-exclude-from-capture — spec

Issue: [#965](https://github.com/moumantai-gg/mithril/issues/965). Related: [#914](https://github.com/moumantai-gg/mithril/issues/914) (capture/solve engine), [#938](https://github.com/moumantai-gg/mithril/issues/938) (live manual-verify), [#941](https://github.com/moumantai-gg/mithril/issues/941) (overlay status surface).

## Problem

`CaptureService.CaptureMapAsync` wraps its `BitBlt` grab in `OverlayBlanker.BlankAsync()`, which:

1. Reads `IOverlayWindow.Window` — that getter calls `OverlayWindowService.EnsureWindow` and **creates** the shared overlay window + its D3D surface even when the overlay was never opened.
2. `window.Hide()` — no-op if it was never shown.
3. On dispose, `window.Show()` — **unconditionally**, so the overlay appears even if the user never toggled it on.

So pressing **Capture & Calibrate** materializes the overlay window from a never-opened state, purely to keep Mithril's own chrome out of the BitBlt. The "restore" step doesn't restore prior state; it always `Show()`s.

The capture itself is window-independent: `BitBltScreenCapture` uses `GetDC(NULL)` (whole-desktop DC) over an absolute desktop-pixel rect (the [#947](https://github.com/moumantai-gg/mithril/issues/947) persisted bbox). The overlay is touched only to keep our own topmost chrome out of the shot. The coupling is incidental complexity and leaks as a user-visible bug (overlay appears on calibrate), `D3DImage` flicker risk, and avoidable DWM hide/show churn on the capture path.

## Goal

Decouple capture orchestration from overlay-window lifecycle. The overlay declares itself invisible to capture at window-creation time; capture stops touching the overlay entirely.

## Non-goals

- Replacing `BitBlt` with WGC. Affinity removes the only motivator the [`OverlayBlanker` docstring](../../../src/Mithril.MapCalibration.Capture/OverlayBlanker.cs) cited (D3DImage hide/show flicker), but WGC is a separate cost/benefit conversation.
- Adding the affinity flag to the Mithril.Shell main window, Gandalf alarm toasts, or any other Mithril window the user positions deliberately. Affinity is intended for windows definitionally meant to sit *over the game viewport* — not for chrome the user can move and would expect in their own screenshots.

## Mechanism — `WDA_EXCLUDEFROMCAPTURE`

`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` removes the window from screen captures (Print Screen, Snipping Tool, GDI `BitBlt` of the screen DC, Windows Graphics Capture) while leaving it fully visible on the display. The captured region of pixels underneath the excluded window shows through to whatever is below (the game map) — not a black rectangle (that was the older `WDA_MONITOR`).

OS requirement: Windows 10 2004+ (build 19041). Mithril is Win11-only — fine.

## Scope — three windows tagged

`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` is applied at `SourceInitialized` to:

1. **`IOverlayWindow.Window`** (`Mithril.Overlay.Internal.OverlayWindow`) — the shared D2D map overlay.
2. **`Legolas.Views.CalibrationOverlayView`** — topmost transparent click-capture window for the landmark-pair calibration UI.
3. **`Legolas.Views.InventoryOverlayView`** — topmost transparent overlay over the game viewport.

## Design

### New: `Mithril.Shared.Wpf/WindowCaptureExclusion.cs`

One public static, called once per qualifying window from its constructor:

```csharp
public static class WindowCaptureExclusion
{
    public static void ExcludeFromCapture(Window window) { ... }
}
```

Internals:

- Single `[DllImport("user32.dll", SetLastError = true)]` for `SetWindowDisplayAffinity(IntPtr hwnd, uint affinity)`.
- Constant `WDA_EXCLUDEFROMCAPTURE = 0x11`.
- If the window's HWND already exists (`new WindowInteropHelper(window).Handle != IntPtr.Zero`), call directly. Otherwise hook `SourceInitialized` once and call from the handler.
- Signature: `ExcludeFromCapture(Window window, ILogger? logger = null)`. On `false` return, capture `Marshal.GetLastWin32Error()` and emit a single `Warning` via `logger` with the HWND + error code (if supplied; otherwise silent — caller can opt in). Do not throw; the affinity is a hint, the window remains usable.
  - Call sites pass the local `ILoggerFactory.CreateLogger("Mithril.Overlay")` / `"Legolas"` they already have on hand. Three call sites, three already-resolved loggers — no new DI plumbing required for the helper.

Why not CsWin32 here: `Mithril.Shared.Wpf` doesn't currently consume CsWin32, and one DllImport doesn't justify wiring it. If CsWin32 lands in this project later, the static can be refactored without a contract change.

### Wiring

- **`OverlayWindowService.CreateWindowOnDispatcher`** — call `WindowCaptureExclusion.ExcludeFromCapture(_window)` immediately after `_window = new OverlayWindow()`, before any `Show()` is possible (consumers `Show()` later). Owning service makes the call so `OverlayWindow.xaml.cs` stays mostly empty.
- **`CalibrationOverlayView` ctor** — append `WindowCaptureExclusion.ExcludeFromCapture(this);` after `InitializeComponent()`.
- **`InventoryOverlayView` ctor** — same.

### Deletions

- `src/Mithril.MapCalibration.Capture/OverlayBlanker.cs`
- `src/Mithril.MapCalibration.Capture/IOverlayBlanker.cs`
- The `services.AddSingleton<IOverlayBlanker, OverlayBlanker>()` (or equivalent) line in `CaptureServiceCollectionExtensions`.
- The `IOverlayBlanker blanker` constructor parameter on `CaptureService` and the `await using (await _blanker.BlankAsync()...)` block in `CaptureMapAsync`. The method becomes a straight `_capture.Capture(bbox)` + validate.
- The `ProjectReference` from `Mithril.MapCalibration.Capture.csproj` to `Mithril.Overlay.csproj` stays. `IOverlayWindow` still has other live consumers in Capture (`AutoCalibrationTrigger`, `ManualCalibrationCoordinator`, `MapBboxDrawController`); only the `IOverlayBlanker` registration is removed.

### Data flow

Unchanged on the capture side except that the wrapping disappears:

```
CaptureCalibrateCommand
 → TryCalibrateCurrentAreaAsync
 → CaptureService.CaptureMapAsync(bbox)
 → BitBltScreenCapture.Capture(bbox)
 → CaptureValidation.Validate(frame, bbox)
 → CaptureMapResult
```

No dispatcher round-trips, no `Hide`/`Show`, no `EnsureWindow` side effects, no `D3DImage` hide/show flicker risk on the capture path.

## Error handling

`SetWindowDisplayAffinity` failure is rare (unsupported Windows, paranoid security policy). On a `false` return:

- Log `Warning` once per window with the Win32 error code and HWND.
- Do not throw — the affinity is a best-effort hint; the window stays usable.

If the affinity isn't honored at all (extremely unlikely on Win11), the captured frame contains overlay chrome. The calibration solver will either fail to converge or accept a degraded match, which `CaptureValidation` may flag downstream. That's user-visible but not crash-shape. The risk is gated by the live-verify checklist below.

## Verification owed (gate on cutover)

**Manual / live-verify (owned under [#938](https://github.com/moumantai-gg/mithril/issues/938)).** Before deleting `OverlayBlanker`, with PG running:

1. Open the map overlay → trigger Capture & Calibrate → inspect the persisted capture frame on disk → confirm the overlay chrome is absent from the BitBlt.
2. Open the calibration-landmark overlay → trigger a capture under it → confirm absent.
3. Open the inventory overlay → trigger a capture under it → confirm absent.

If any of (1)–(3) shows overlay chrome in the capture, the cutover stops and we investigate why GDI `GetDC(NULL)` + `BitBlt` isn't honoring affinity on a layered/`AllowsTransparency` `D3DImage` window before proceeding with the deletes.

## Tests

### Unit — `Mithril.Shared.Wpf.Tests` (new file)

- `WindowCaptureExclusion_ExcludeFromCapture_DoesNotThrowBeforeSourceInitialized` — instantiate a `new Window()` on an STA thread, call `ExcludeFromCapture` before showing, assert no throw. Then trigger `SourceInitialized` (via `Show()`/close) and assert still no throw.
- `WindowCaptureExclusion_ExcludeFromCapture_DoesNotThrowOnHwndAlreadyCreated` — show the window first, then call `ExcludeFromCapture`, assert no throw.

These two are smoke / fault-path coverage — the real behavior is observable only on a live desktop with PG, which the manual-verify covers.

### Unit — `Mithril.MapCalibration.Capture.Tests`

- `CaptureServiceTests` — remove the `IOverlayBlanker` fake from fixtures; update `CaptureMapAsync` tests to no longer assert blank/restore ordering. Add: `CaptureMapAsync_DoesNotTouchOverlayCollaborators` (a test seam asserting only `IScreenCapture` + `CaptureValidation` interactions, via a strict mock or by leaving `CaptureService`'s ctor with just those two deps).
- `CaptureDependencyInjectionTests` — remove the assertion that `IOverlayBlanker` resolves; add the negative assertion that it is no longer registered.

### Tests untouched but worth re-running

- `ManualCalibrationCoordinatorTests` — the coordinator drives `CaptureService` indirectly; ensure no fixtures still ask for an `IOverlayBlanker`.

## Open questions

None — the issue body + ratified scope comment lock the macro decisions. The one design axis (Win32 helper location) was settled to `Mithril.Shared.Wpf`.

## Status

`active` — pending implementation. Index row added to `docs/planning/INDEX.md`.
