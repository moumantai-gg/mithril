# overlay-965-exclude-from-capture — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply `WDA_EXCLUDEFROMCAPTURE` to the three overlay windows at `SourceInitialized`, then decouple `CaptureService` from `IOverlayBlanker` and delete the blanker.

**Architecture:** One shared static helper in `Mithril.Shared.Wpf` calls `SetWindowDisplayAffinity`. The owning code path for each overlay window (`OverlayWindowService.CreateWindowOnDispatcher`, `CalibrationOverlayView` ctor, `InventoryOverlayView` ctor) calls the helper exactly once. After live-verify against PG, the `OverlayBlanker` + `IOverlayBlanker` + their DI registration get deleted, and `CaptureService`'s ctor + `CaptureMapAsync` lose the blank/restore wrapping.

**Tech Stack:** .NET 10 (`net10.0-windows`), WPF, P/Invoke (`user32.dll`), xUnit, FluentAssertions.

**Spec:** [spec.md](./spec.md). **Issue:** [#965](https://github.com/moumantai-gg/mithril/issues/965). **Live-verify owner:** [#938](https://github.com/moumantai-gg/mithril/issues/938).

---

## File map

**Create:**
- `src/Mithril.Shared.Wpf/WindowCaptureExclusion.cs` — static helper, single `SetWindowDisplayAffinity` PInvoke.
- `tests/Mithril.Shared.Tests/Wpf/WindowCaptureExclusionTests.cs` — smoke / fault-path tests on an STA thread. (Existing project; already references `Mithril.Shared.Wpf` and has `<UseWPF>true</UseWPF>`.)

**Modify:**
- `src/Mithril.Overlay/Internal/OverlayWindowService.cs` — one line in `CreateWindowOnDispatcher`.
- `src/Legolas.Module/Views/CalibrationOverlayView.xaml.cs` — one line in the parameterless ctor.
- `src/Legolas.Module/Views/InventoryOverlayView.xaml.cs` — one line in the parameterless ctor.
- `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` — remove the `IOverlayBlanker` registration + the corresponding constructor arg in the `CaptureService` factory.
- `src/Mithril.MapCalibration.Capture/CaptureService.cs` — drop the `_blanker` field, the ctor param, and the `await using` block.
- `tests/Mithril.MapCalibration.Capture.Tests/CaptureServiceTests.cs` — strip the `FakeBlanker` fixture + arguments.

**Delete (after live-verify gate, Task 6):**
- `src/Mithril.MapCalibration.Capture/OverlayBlanker.cs`
- `src/Mithril.MapCalibration.Capture/IOverlayBlanker.cs`

---

## Task 1: Add `WindowCaptureExclusion` helper to `Mithril.Shared.Wpf`

**Files:**
- Create: `src/Mithril.Shared.Wpf/WindowCaptureExclusion.cs`
- Create: `tests/Mithril.Shared.Tests/Wpf/WindowCaptureExclusionTests.cs`

- [ ] **Step 1.1: Write the failing tests**

Create `tests/Mithril.Shared.Tests/Wpf/WindowCaptureExclusionTests.cs`:

```csharp
using System.Threading;
using System.Windows;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

public sealed class WindowCaptureExclusionTests
{
    // STA-fact wrapper: WPF Window construction requires an STA thread; xUnit's
    // default Fact runs MTA. We spin a thread per test to scope the STA cost.
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }

    [Fact]
    public void ExcludeFromCapture_BeforeSourceInitialized_DoesNotThrow()
    {
        RunSta(() =>
        {
            var window = new Window();
            // HWND not yet created — helper must hook SourceInitialized.
            var act = () => WindowCaptureExclusion.ExcludeFromCapture(window);
            act.Should().NotThrow();
            window.Close();
        });
    }

    [Fact]
    public void ExcludeFromCapture_AfterHwndCreated_DoesNotThrow()
    {
        RunSta(() =>
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Width = 1,
                Height = 1,
                Left = -10_000, // off-screen
                Top = -10_000,
            };
            window.Show(); // forces SourceInitialized → HWND exists
            var act = () => WindowCaptureExclusion.ExcludeFromCapture(window);
            act.Should().NotThrow();
            window.Close();
        });
    }

    [Fact]
    public void ExcludeFromCapture_NullWindow_Throws()
    {
        Action act = () => WindowCaptureExclusion.ExcludeFromCapture(null!);
        act.Should().Throw<System.ArgumentNullException>();
    }
}
```

- [ ] **Step 1.2: Run the tests and verify they fail to compile**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~WindowCaptureExclusionTests" --no-restore`
Expected: compile error — `WindowCaptureExclusion` does not exist.

- [ ] **Step 1.3: Implement the helper**

Create `src/Mithril.Shared.Wpf/WindowCaptureExclusion.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Marks a WPF <see cref="Window"/> as excluded from screen captures (PrintScreen,
/// Snipping Tool, GDI <c>BitBlt</c> of the screen DC, Windows Graphics Capture)
/// while leaving it fully visible on the display. The pixels beneath the window
/// show through to whatever sits below in any capture surface — not a black
/// rectangle (that was the older <c>WDA_MONITOR</c>).
///
/// <para>Requires Windows 10 2004+ (build 19041). Mithril targets Win11.</para>
/// </summary>
public static class WindowCaptureExclusion
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>
    /// Apply <c>WDA_EXCLUDEFROMCAPTURE</c> to <paramref name="window"/>. Safe to
    /// call before the HWND exists — the helper hooks <see cref="Window.SourceInitialized"/>
    /// once and applies the affinity from that handler.
    /// </summary>
    /// <param name="window">The WPF window to exclude from screen captures.</param>
    /// <param name="logger">Optional. When supplied, a single <c>Warning</c> is
    /// logged on PInvoke failure (Win32 error + HWND). Callers without a logger
    /// get silent fail-soft behavior; the window remains usable.</param>
    public static void ExcludeFromCapture(Window window, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Apply(hwnd, logger);
            return;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            window.SourceInitialized -= handler;
            var h = new WindowInteropHelper(window).Handle;
            if (h != IntPtr.Zero) Apply(h, logger);
        };
        window.SourceInitialized += handler;
    }

    private static void Apply(IntPtr hwnd, ILogger? logger)
    {
        if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) return;
        var err = Marshal.GetLastWin32Error();
        logger?.LogWarning(
            "SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed for HWND {Hwnd}: Win32 error {Error}.",
            hwnd, err);
    }
}
```

- [ ] **Step 1.4: Run the tests and verify they pass**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~WindowCaptureExclusionTests"`
Expected: 3 passed.

- [ ] **Step 1.5: Commit**

```bash
git add src/Mithril.Shared.Wpf/WindowCaptureExclusion.cs tests/Mithril.Shared.Tests/Wpf/WindowCaptureExclusionTests.cs
git commit -m "feat(shared.wpf): add WindowCaptureExclusion helper (#965)"
```

---

## Task 2: Wire the shared overlay window

**Files:**
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs:262-272` (the `CreateWindowOnDispatcher` method)

- [ ] **Step 2.1: Add the call**

In `CreateWindowOnDispatcher`, immediately after `_window = new OverlayWindow();` and before `_window.DataContext = this;`, add:

```csharp
Mithril.Shared.Wpf.WindowCaptureExclusion.ExcludeFromCapture(
    _window, _loggerFactory?.CreateLogger("Mithril.Overlay.Capture"));
```

After the edit, the method should read (newly inserted line marked with `// #965`):

```csharp
private void CreateWindowOnDispatcher()
{
    if (_window is not null) return;
    using var act = MithrilActivitySources.Overlay.StartActivity("window.create");
    _window = new OverlayWindow();
    Mithril.Shared.Wpf.WindowCaptureExclusion.ExcludeFromCapture( // #965
        _window, _loggerFactory?.CreateLogger("Mithril.Overlay.Capture"));
    _window.DataContext = this;
    _window.OverlaySurface.Render += OnSurfaceRender;
    _window.OverlaySurface.Logger = _loggerFactory?.CreateLogger("Mithril.Overlay.Surface");
    _window.Closed += OnWindowClosed;
    _logger?.LogInformation("OverlayWindow created (not shown — consumer must Show() to surface it).");
}
```

`Mithril.Overlay` already references `Mithril.Shared` (and transitively pulls `Mithril.Shared.Wpf`'s namespace via its existing ProjectReference set — verify a project ref to `Mithril.Shared.Wpf` exists; if not, add one). Run `grep -n "Mithril.Shared.Wpf" src/Mithril.Overlay/Mithril.Overlay.csproj` — if the line is missing, add it under the `<ItemGroup>` with the other ProjectReferences:

```xml
<ProjectReference Include="..\Mithril.Shared.Wpf\Mithril.Shared.Wpf.csproj" />
```

- [ ] **Step 2.2: Build the overlay project**

Run: `dotnet build src/Mithril.Overlay/Mithril.Overlay.csproj`
Expected: build succeeded, 0 errors.

- [ ] **Step 2.3: Run overlay tests**

Run: `dotnet test tests/Mithril.Overlay.Tests`
Expected: all pass — no behavioral test covers the affinity directly, so this is just a "no regression" check.

- [ ] **Step 2.4: Commit**

```bash
git add src/Mithril.Overlay/Internal/OverlayWindowService.cs src/Mithril.Overlay/Mithril.Overlay.csproj
git commit -m "feat(overlay): exclude shared overlay from screen capture (#965)"
```

---

## Task 3: Wire `CalibrationOverlayView`

**Files:**
- Modify: `src/Legolas.Module/Views/CalibrationOverlayView.xaml.cs:22-25` (the parameterless ctor)

- [ ] **Step 3.1: Add the call**

In the parameterless `CalibrationOverlayView()` ctor, after `InitializeComponent();`, add the affinity call. `Legolas.Module.csproj` already references `Mithril.Shared.Wpf` (verified) — no csproj change.

After the edit, the parameterless ctor should read:

```csharp
public CalibrationOverlayView()
{
    InitializeComponent();
    Mithril.Shared.Wpf.WindowCaptureExclusion.ExcludeFromCapture(this); // #965
}
```

Rationale for the parameterless ctor (not the DI overload): every overload chains through this one (`: this()`), so the affinity is applied exactly once regardless of which constructor a caller picks. No logger is passed — there's no logger in scope here and the helper's silent fail-soft is the correct shape for a UI ctor.

- [ ] **Step 3.2: Build Legolas**

Run: `dotnet build src/Legolas.Module/Legolas.Module.csproj`
Expected: build succeeded, 0 errors.

- [ ] **Step 3.3: Run Legolas tests**

Run: `dotnet test tests/Legolas.Tests`
Expected: all pass — no behavioral test covers the affinity; this is a "no regression" check.

- [ ] **Step 3.4: Commit**

```bash
git add src/Legolas.Module/Views/CalibrationOverlayView.xaml.cs
git commit -m "feat(legolas): exclude calibration overlay from screen capture (#965)"
```

---

## Task 4: Wire `InventoryOverlayView`

**Files:**
- Modify: `src/Legolas.Module/Views/InventoryOverlayView.xaml.cs:11-14` (the parameterless ctor)

- [ ] **Step 4.1: Add the call**

In the parameterless `InventoryOverlayView()` ctor, after `InitializeComponent();`, add the affinity call. After the edit:

```csharp
public InventoryOverlayView()
{
    InitializeComponent();
    Mithril.Shared.Wpf.WindowCaptureExclusion.ExcludeFromCapture(this); // #965
}
```

Same parameterless-ctor rationale as Task 3.

- [ ] **Step 4.2: Build Legolas**

Run: `dotnet build src/Legolas.Module/Legolas.Module.csproj`
Expected: build succeeded, 0 errors.

- [ ] **Step 4.3: Commit**

```bash
git add src/Legolas.Module/Views/InventoryOverlayView.xaml.cs
git commit -m "feat(legolas): exclude inventory overlay from screen capture (#965)"
```

---

## Task 5: Live-verify gate (manual, owned under #938)

**No code in this task.** This is a hard stop before the deletions in Task 6.

Pull the latest build of the four prior commits (Tasks 1–4) onto a Win11 machine with PG installed.

- [ ] **Step 5.1: Build a runnable shell against the verify branch**

Run: `dotnet build Mithril.slnx -c Debug`
Expected: succeeds.

- [ ] **Step 5.2: Launch Mithril + PG**

Run: `dotnet run --project src/Mithril.Shell`
(Or use the `mithril` skill: `scripts/start.ps1`.)
Launch PG and sign in to any zone with a calibrated map.

- [ ] **Step 5.3: Verify case 1 — map overlay visible during capture**

1. Open the Legolas map overlay (so it's visible over the game viewport).
2. Trigger **Capture & Calibrate** (whatever hotkey or button binding the user has set).
3. Locate the persisted attempt bundle on disk (default root: `Diagnostics.CalibrationBundleDirectories.DefaultRoot`).
4. Open the captured frame image. **The map overlay's chrome MUST NOT appear in it.**

- [ ] **Step 5.4: Verify case 2 — calibration overlay visible during capture**

1. Open `CalibrationOverlayView` (start a manual calibration session).
2. Trigger a capture under it.
3. Inspect the bundle frame. **The calibration overlay chrome MUST NOT appear in it.**

- [ ] **Step 5.5: Verify case 3 — inventory overlay visible during capture**

1. Open `InventoryOverlayView`.
2. Trigger a capture under it.
3. Inspect the bundle frame. **The inventory overlay chrome MUST NOT appear in it.**

- [ ] **Step 5.6: Decision gate**

If all three cases pass: proceed to Task 6.

If **any** case shows overlay chrome in the captured frame: stop. The affinity is not being honored on this layered/`AllowsTransparency` `D3DImage` window under GDI `GetDC(NULL)` + `BitBlt`. Do **not** proceed to delete `OverlayBlanker`. Open a follow-up under #938 / #965 documenting the failure mode (Windows version, GPU/driver if relevant, which window failed) and revisit.

**No commit in this task — verification only.**

---

## Task 6: Delete `OverlayBlanker` + simplify `CaptureService`

**Gated on Task 5 passing.**

**Files:**
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/CaptureServiceTests.cs` (drop `FakeBlanker`, update ctor calls).
- Modify: `src/Mithril.MapCalibration.Capture/CaptureService.cs` (drop blanker param + `await using` block).
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` (drop the `IOverlayBlanker` registration; drop the blanker arg from the `CaptureService` factory).
- Delete: `src/Mithril.MapCalibration.Capture/OverlayBlanker.cs`
- Delete: `src/Mithril.MapCalibration.Capture/IOverlayBlanker.cs`

- [ ] **Step 6.1: Rewrite `CaptureServiceTests` to the new ctor shape**

Replace the entire contents of `tests/Mithril.MapCalibration.Capture.Tests/CaptureServiceTests.cs` with:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class CaptureServiceTests
{
    [Fact]
    public async Task Returns_null_gray_when_capture_fails()
    {
        var svc = new CaptureService(new FailingCapture(), new CaptureValidation(), null);
        var result = await svc.CaptureMapAsync(new CaptureRect(0, 0, 8, 8), default);
        result.Gray.Should().BeNull();
        result.Color.Should().BeNull();
    }

    [Fact]
    public async Task Returns_gray_for_a_valid_capture()
    {
        var px = new byte[8 * 8 * 4]; Array.Fill(px, (byte)180);
        var svc = new CaptureService(new FakeCapture(new CapturedFrame(8, 8, px)),
            new CaptureValidation(), null);
        var result = await svc.CaptureMapAsync(new CaptureRect(0, 0, 8, 8), default);
        result.Gray.Should().NotBeNull();
        result.Gray!.Width.Should().Be(8);
        result.Color.Should().NotBeNull("color frame should be returned alongside gray");
    }

    [Fact]
    public async Task Rejects_a_black_capture() // spec §11 "captured our own overlay / occlusion"
    {
        var svc = new CaptureService(new FakeCapture(new CapturedFrame(8, 8, new byte[8 * 8 * 4])),
            new CaptureValidation(), null);
        var result = await svc.CaptureMapAsync(new CaptureRect(0, 0, 8, 8), default);
        result.Gray.Should().BeNull();
        result.Color.Should().BeNull();
    }

    private sealed class FakeCapture(CapturedFrame frame) : IScreenCapture
    {
        public CapturedFrame? Capture(CaptureRect rect) => frame;
    }

    private sealed class FailingCapture : IScreenCapture
    {
        public CapturedFrame? Capture(CaptureRect rect) => null;
    }
}
```

What changed from the old test: `FakeBlanker` deleted; the "restores overlay on failure" assertion is gone (the property it tested no longer exists); ctor calls now take two collaborators instead of three. The black-capture test is preserved verbatim because `CaptureValidation` still rejects all-black frames at the validation step — that behavior is independent of the affinity rework.

- [ ] **Step 6.2: Run the tests and verify they fail to compile**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CaptureServiceTests" --no-restore`
Expected: compile error — `CaptureService` ctor still requires an `IOverlayBlanker`.

- [ ] **Step 6.3: Simplify `CaptureService`**

Replace the entire contents of `src/Mithril.MapCalibration.Capture/CaptureService.cs` with:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Captures the framed bbox via <see cref="IScreenCapture"/> and validates the
/// result before handing a clean <see cref="CaptureMapResult"/> to the solve
/// engine. The overlay windows declare themselves invisible to capture at
/// construction (Mithril.Shared.Wpf.WindowCaptureExclusion, #965) so capture has
/// no overlay coupling.
/// </summary>
public sealed class CaptureService : ICaptureService
{
    private readonly IScreenCapture _capture;
    private readonly CaptureValidation _validation;
    private readonly ILogger? _logger;

    public CaptureService(
        IScreenCapture capture,
        CaptureValidation validation,
        ILogger? logger)
    {
        _capture = capture;
        _validation = validation;
        _logger = logger;
    }

    public Task<CaptureMapResult> CaptureMapAsync(CaptureRect bbox, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        CapturedFrame? frame = _capture.Capture(bbox);
        if (frame is null)
        {
            _logger?.LogWarning("Map capture produced no frame for bbox {Width}x{Height} at ({X},{Y})",
                bbox.Width, bbox.Height, bbox.X, bbox.Y);
            return Task.FromResult(new CaptureMapResult(null, null));
        }

        if (!_validation.Validate(frame, bbox, out var reason))
        {
            _logger?.LogWarning("Map capture rejected: {Reason}", reason);
            return Task.FromResult(new CaptureMapResult(null, null));
        }

        return Task.FromResult(new CaptureMapResult(frame, frame.ToGray()));
    }
}
```

Note: `CaptureMapAsync` was `async` only because of the `await using` on the blanker. With the blanker gone, the body is fully synchronous; return `Task.FromResult(...)` and drop `async`. The `ICaptureService.CaptureMapAsync` signature still returns `Task<CaptureMapResult>` so callers are unaffected.

- [ ] **Step 6.4: Strip the DI registration**

In `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`, find the block (around the comment `// Overlay blanking + the capture orchestration over it.`):

```csharp
// Overlay blanking + the capture orchestration over it.
services.AddSingleton<IOverlayBlanker, OverlayBlanker>();
services.AddSingleton<ICaptureService>(sp => new CaptureService(
    sp.GetRequiredService<IScreenCapture>(),
    sp.GetRequiredService<IOverlayBlanker>(),
    sp.GetRequiredService<CaptureValidation>(),
    sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Service")));
```

Replace with:

```csharp
// Capture orchestration. Overlay windows are excluded from screen capture at
// their own construction (Mithril.Shared.Wpf.WindowCaptureExclusion, #965), so
// CaptureService has no overlay dependency.
services.AddSingleton<ICaptureService>(sp => new CaptureService(
    sp.GetRequiredService<IScreenCapture>(),
    sp.GetRequiredService<CaptureValidation>(),
    sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Service")));
```

- [ ] **Step 6.5: Delete the blanker files**

Run:

```bash
git rm src/Mithril.MapCalibration.Capture/OverlayBlanker.cs
git rm src/Mithril.MapCalibration.Capture/IOverlayBlanker.cs
```

- [ ] **Step 6.6: Build and verify the deletes don't leave dangling references**

Run: `dotnet build Mithril.slnx`
Expected: build succeeded, 0 errors. If a compile error names `IOverlayBlanker` or `OverlayBlanker`, grep the symbol (`grep -rn "OverlayBlanker" src/ tests/`) and remove the offending reference — there should be none left by design.

- [ ] **Step 6.7: Run capture tests + the DI test**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests`
Expected: all green. `CaptureDependencyInjectionTests` should resolve `ICaptureService` against the new factory unchanged (it didn't mention `IOverlayBlanker` — verified at plan time).

- [ ] **Step 6.8: Run the full test suite as a sweep**

Run: `dotnet test Mithril.slnx`
Expected: all green.

- [ ] **Step 6.9: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/CaptureService.cs \
        src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs \
        tests/Mithril.MapCalibration.Capture.Tests/CaptureServiceTests.cs
git commit -m "refactor(map-calibration): delete OverlayBlanker; CaptureService is overlay-free (#965)"
```

(The `git rm` from Step 6.5 already staged the deletions, so they go into this commit.)

---

## Task 7: Flip planning-index status to `shipped` after PR merge

**Files:**
- Modify: `docs/planning/INDEX.md` (the row added during brainstorming)

- [ ] **Step 7.1: Open a PR**

```bash
gh pr create --title "Decouple capture from overlay window (#965)" --body-file - <<'EOF'
## Summary
- Apply `WDA_EXCLUDEFROMCAPTURE` to the shared map overlay + Legolas calibration/inventory overlays at `SourceInitialized` via a new `Mithril.Shared.Wpf.WindowCaptureExclusion` helper.
- Delete `OverlayBlanker` / `IOverlayBlanker`; `CaptureService` no longer depends on the overlay.

Closes #965.

## Test plan
- [x] `WindowCaptureExclusionTests` (3 smoke / fault-path tests)
- [x] `CaptureServiceTests` rewritten to two-collaborator ctor
- [x] Live-verified per spec §"Verification owed" (map / calibration / inventory overlays absent from BitBlt'd frame on Win11)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

- [ ] **Step 7.2: After merge, flip the INDEX row**

In `docs/planning/INDEX.md`, change the row added during brainstorming from:

```
| [overlay-965-exclude-from-capture](overlay-965-exclude-from-capture/) | active | [#965](https://github.com/moumantai-gg/mithril/issues/965) | Decouple screen capture from overlay window — apply `WDA_EXCLUDEFROMCAPTURE` to the three overlay windows at `SourceInitialized`; delete `OverlayBlanker` + capture-side blank/restore |
```

to (replace `active` with `shipped` and append the PR link to the issue cell):

```
| [overlay-965-exclude-from-capture](overlay-965-exclude-from-capture/) | shipped | [#965](https://github.com/moumantai-gg/mithril/issues/965) · [#<PR>](https://github.com/moumantai-gg/mithril/pull/<PR>) | Decouple screen capture from overlay window — apply `WDA_EXCLUDEFROMCAPTURE` to the three overlay windows at `SourceInitialized`; delete `OverlayBlanker` + capture-side blank/restore |
```

- [ ] **Step 7.3: Commit the status flip**

```bash
git add docs/planning/INDEX.md
git commit -m "docs(planning): flip overlay-965-exclude-from-capture to shipped"
```

---

## Plan self-review

- **Spec coverage.** New helper → Task 1. Three wirings → Tasks 2/3/4. Live-verify gate → Task 5. Blanker deletion + DI cleanup + CaptureService simplification + test fixup → Task 6. Index status flip → Task 7. No spec section uncovered.
- **Placeholder scan.** `<PR>` placeholder in Task 7 is filled in by the engineer after `gh pr create` returns the PR number — that's a runtime value, not a TBD. No other placeholders.
- **Type consistency.** Helper name `WindowCaptureExclusion`, method name `ExcludeFromCapture(Window, ILogger?)` — same across the helper file, all three call sites, and the test file. `CaptureService` ctor goes from 4 → 3 params; DI factory and tests both match. `OverlayBlanker` / `IOverlayBlanker` deletions consistent.
