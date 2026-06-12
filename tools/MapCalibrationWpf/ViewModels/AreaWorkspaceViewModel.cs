namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationWpf.Services;
using Mithril.Tools.MapCalibrationWpf.Views;

/// <summary>
/// Per-area state machine. On construction it kicks off
/// <see cref="AssetBootstrapService.EnsureSourcePngAsync"/> behind a modal
/// progress dialog; success populates <see cref="SourceMapImage"/>, failure
/// populates <see cref="LoadError"/>. The picker is instantiated synchronously
/// from the bundled landmarks/NPCs JSON (cheap; no IO except a JSON parse).
///
/// <para><see cref="Refs"/> is the user's committed reference clicks. The
/// solver wiring (Task 9) subscribes to <see cref="ObservableCollection{T}.CollectionChanged"/>
/// and re-solves on every change.</para>
/// </summary>
public sealed partial class AreaWorkspaceViewModel : ObservableObject
{
    private readonly PgInstallResolver _installResolver;
    private readonly WorkspaceCommitService _commitService;
    private AreaCalibration? _storedCalibration;

    public string Area { get; }

    [ObservableProperty]
    private BitmapImage? _sourceMapImage;

    [ObservableProperty]
    private string? _loadError;

    public LandmarkPickerViewModel Picker { get; }

    public ObservableCollection<RefViewModel> Refs { get; } = new();

    public ObservableCollection<ProjectionMarkerViewModel> Projections { get; } = new();

    [ObservableProperty]
    private AreaCalibration? _calibration;

    public SolverReadoutViewModel SolverReadout { get; } = new();

    /// <summary>
    /// True when the user has placed refs whose solved calibration differs
    /// from what's stored in <c>map-calibration-baseline.json</c> for this
    /// area. Used by both the commit-button CanExecute and the area-switch
    /// guard dialog in <see cref="MainViewModel"/>.
    /// </summary>
    public bool HasUncommittedRefs =>
        Refs.Count >= 2
        && Calibration is { } current
        && !ApproximatelyEqual(current, _storedCalibration);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit()
    {
        if (Calibration is not { } cal) return;
        _commitService.Commit(Area, cal);
        // LandmarkCalibrationSolver.Solve always emits Source=UserRefinement
        // (record default) but the service stamps Source=BundledBaseline
        // (+ Zoom=1.0 + SchemaVersion=1) into the JSON. Stamp the in-memory
        // copy to match what's now on disk — otherwise ApproximatelyEqual
        // would never report a match after commit and the button would
        // stay enabled forever (round-2 review blocker). Mirror exactly what
        // WorkspaceCommitService.Commit writes.
        Calibration = cal with
        {
            Source = CalibrationSource.BundledBaseline,
            SchemaVersion = 1,
        };
        SolverReadout.Calibration = Calibration;
        // Refresh the stored cache so HasUncommittedRefs flips to false (and
        // the button greys out) until the user changes a ref again.
        _storedCalibration = _commitService.ReadStored(Area);
        OnPropertyChanged(nameof(HasUncommittedRefs));
        CommitCommand.NotifyCanExecuteChanged();
    }

    private bool CanCommit() =>
        Calibration is not null && Refs.Count >= 2 && HasUncommittedRefs;

    /// <summary>
    /// ULP-tolerant comparison: two calibrations are "the same" if every
    /// numeric field matches within a tight epsilon AND the bool/enum fields
    /// match exactly. Threshold chosen to be tighter than any visible-pixel
    /// effect (Scale + Origin float-rounds round-trip via JSON well below
    /// 1e-9 on values in the px-per-unit / origin-pixel range we see).
    /// </summary>
    private static bool ApproximatelyEqual(AreaCalibration a, AreaCalibration? b)
    {
        if (b is null) return false;
        const double eps = 1e-9;
        return Math.Abs(a.Scale - b.Scale) < eps
            && Math.Abs(a.RotationRadians - b.RotationRadians) < eps
            && Math.Abs(a.OriginX - b.OriginX) < eps
            && Math.Abs(a.OriginY - b.OriginY) < eps
            && Math.Abs(a.ResidualPixels - b.ResidualPixels) < eps
            && a.ReferenceCount == b.ReferenceCount
            && a.MirrorNorth == b.MirrorNorth
            && a.Source == b.Source
            && a.SchemaVersion == b.SchemaVersion;
    }

    partial void OnCalibrationChanged(AreaCalibration? value)
    {
        OnPropertyChanged(nameof(HasUncommittedRefs));
        CommitCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Click handler hook from <see cref="Views.SourceMapCanvas"/>. Materialises
    /// a <see cref="RefViewModel"/> from the picker's <see cref="LandmarkPickerViewModel.Selected"/>
    /// + the click pixel, then clears the picker selection. No-op when nothing
    /// is selected (so stray clicks don't pollute the ref table).
    /// </summary>
    public void PlaceRefAt(double x, double y)
    {
        if (Picker.Selected is not LandmarkPickerItem sel) return;
        var refVm = new RefViewModel(sel.Name, sel.Kind, sel.World, new TexturePixel(x, y));
        Refs.Add(refVm);
        Picker.Selected = null;
        // Solver re-runs via the OnRefsChanged hook below.
    }

    private void ReSolve()
    {
        if (Refs.Count < 2)
        {
            Calibration = null;
            SolverReadout.Calibration = null;
            foreach (var r in Refs) r.ResidualPx = null;
            Projections.Clear();
            return;
        }

        var solverRefs = Refs.Select(r =>
            new LandmarkCalibrationSolver.Reference(r.World.X, r.World.Z, r.TexturePixel.X, r.TexturePixel.Y))
            .ToList();
        var cal = LandmarkCalibrationSolver.Solve(solverRefs);
        Calibration = cal;
        SolverReadout.Calibration = cal;

        if (cal is null)
        {
            foreach (var r in Refs) r.ResidualPx = null;
            Projections.Clear();
            return;
        }

        // #1076 Phase 7.5: the WPF tool displays texture-frame calibration data;
        // project through a texture-frame view of the solved AreaCalibration so
        // residuals + projection markers stay frame-explicit.
        var calTex = AsTexture(cal);
        foreach (var r in Refs)
        {
            var projected = calTex.ToTexture(r.World);
            var dx = projected.X - r.TexturePixel.X;
            var dy = projected.Y - r.TexturePixel.Y;
            r.ResidualPx = Math.Sqrt(dx * dx + dy * dy);
        }

        RefreshProjections(cal);
    }

    private void RefreshProjections(AreaCalibration cal)
    {
        Projections.Clear();
        var calTex = AsTexture(cal);
        foreach (var item in Picker.AllItems)
        {
            var px = calTex.ToTexture(item.World);
            var refMatch = RefForLandmark(item);
            Projections.Add(new ProjectionMarkerViewModel(item.Name, px, refMatch?.ResidualPx));
        }
    }

    // #1076 Phase 7.5: field-identity re-tag of an AreaCalibration to its
    // texture-frame view. The WPF tool's overlay markers sit in texture space
    // (drawn over the loaded base-texture image), so projection through a
    // frame-typed struct keeps the pixel comparison explicit.
    private static WorldToTextureCalibration AsTexture(AreaCalibration cal) =>
        new(cal.OriginX, cal.OriginY, cal.Scale, cal.RotationRadians, cal.MirrorNorth);

    private RefViewModel? RefForLandmark(LandmarkPickerItem item) =>
        Refs.FirstOrDefault(r =>
            Math.Abs(r.World.X - item.World.X) < 1e-6
            && Math.Abs(r.World.Z - item.World.Z) < 1e-6);

    public AreaWorkspaceViewModel(string area, PgInstallResolver installResolver)
    {
        Area = area;
        _installResolver = installResolver;
        _commitService = new WorkspaceCommitService();
        Picker = new LandmarkPickerViewModel(
            area,
            RepoPaths.LandmarksJsonPath(),
            RepoPaths.NpcsJsonPath());
        Refs.CollectionChanged += OnRefsChanged;
        _storedCalibration = _commitService.ReadStored(area);
        // If a stored anchor exists, render its projections immediately so the
        // user sees the existing baseline overlay before they place any refs.
        if (_storedCalibration is { } stored)
        {
            Calibration = stored;
            SolverReadout.Calibration = stored;
            RefreshProjections(stored);
        }
        // Fire-and-forget: the dialog runs on the UI thread; the bg work
        // updates UI properties when complete. Caller (MainViewModel) doesn't
        // await — area-switch responsiveness wins over linearised completion.
        // The async state machine captures pre-first-await throws into the
        // returned Task, so an outer try/catch here would be dead code —
        // LoadAsync's own try/catch funnels every failure path into LoadError.
        _ = LoadAsync();
    }

    private void OnRefsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasUncommittedRefs));
        ReSolve();
    }

    private async Task LoadAsync()
    {
        ExtractionProgressDialog? dialog = null;
        try
        {
            var bootstrap = new AssetBootstrapService(_installResolver);

            dialog = new ExtractionProgressDialog($"Loading map for {Area}…")
            {
                Owner = Application.Current?.MainWindow,
            };
            dialog.Show();

            var progress = new Progress<string>(msg => dialog?.UpdateStatus(msg));
            var pngPath = await bootstrap.EnsureSourcePngAsync(Area, progress).ConfigureAwait(true);

            // CacheOption.OnLoad so the PNG file isn't held locked across
            // re-extracts; Freeze so the bound Image can render cross-thread.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(pngPath);
            bmp.EndInit();
            bmp.Freeze();
            SourceMapImage = bmp;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
        finally
        {
            try { dialog?.Close(); } catch { /* dialog may have been closed already */ }
        }
    }
}
