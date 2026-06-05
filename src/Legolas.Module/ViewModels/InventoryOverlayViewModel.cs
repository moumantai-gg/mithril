using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;

namespace Legolas.ViewModels;

public sealed partial class InventoryOverlayViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly MotherlodeViewModel? _motherlode;

    public InventoryOverlayViewModel(InventoryGridSettings grid, SessionState session,
        MotherlodeViewModel? motherlode = null)
    {
        Grid = grid;
        _session = session;
        _motherlode = motherlode;
        Slots = CollectionViewSource.GetDefaultView(_session.Surveys);
        Slots.Filter = item => item is SurveyItemViewModel s && !s.Collected;

        // ICollectionView doesn't auto-refresh when an item's filtered property
        // changes — we have to nudge it whenever Collected flips.
        _session.Surveys.CollectionChanged += OnSurveysChanged;
        foreach (var s in _session.Surveys)
            s.PropertyChanged += OnItemChanged;

        // #488: in Motherlode mode the overlay guides the multi-map read-order
        // contract — it lists the tracked motherlode maps (name + 1-based read
        // ordinal) so the player lays them in inventory in that order and reads
        // top-to-bottom at every spot. Sourced from the Motherlode coordinator
        // (via MotherlodeViewModel), kept entirely separate from Survey state.
        if (_motherlode is not null)
            _motherlode.Slots.CollectionChanged += (_, _) => RebuildMotherlodeSlots();
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionState.Mode))
            {
                RebuildMotherlodeSlots();
                OnPropertyChanged(nameof(ActiveSlots));
            }
        };
        RebuildMotherlodeSlots();
    }

    /// <summary>Motherlode read-order guidance items (uncollected tracked maps,
    /// in stable ordinal order). Reuses <see cref="SurveyItemViewModel"/> so the
    /// existing inventory tile template renders them unchanged: Name = map
    /// display name, GridIndex = 1-based read ordinal, IsActiveTarget = the
    /// next map to read.</summary>
    public ObservableCollection<SurveyItemViewModel> MotherlodeSlots { get; } = new();

    /// <summary>The collection the overlay binds to — Survey pins or, in
    /// Motherlode mode, the read-order map list.</summary>
    public IEnumerable ActiveSlots =>
        _session.Mode == SessionMode.Motherlode ? MotherlodeSlots : Slots;

    private void RebuildMotherlodeSlots()
    {
        MotherlodeSlots.Clear();
        if (_motherlode is null || _session.Mode != SessionMode.Motherlode) return;

        var ordinal = 0;
        foreach (var m in _motherlode.Slots)
        {
            if (m.Collected) continue;                       // mirrors the Survey "uncollected only" filter
            var name = string.IsNullOrWhiteSpace(m.MapName) ? $"Map {ordinal + 1}" : m.MapName!;
            var survey = Survey.Create(name, MetreOffset.Zero, ordinal) with { Collected = false };
            MotherlodeSlots.Add(new SurveyItemViewModel(survey) { IsActiveTarget = m.IsNextUp });
            ordinal++;
        }
    }

    private void OnSurveysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (SurveyItemViewModel s in e.NewItems)
                s.PropertyChanged += OnItemChanged;
        if (e.OldItems is not null)
            foreach (SurveyItemViewModel s in e.OldItems)
                s.PropertyChanged -= OnItemChanged;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SurveyItemViewModel.Collected))
            Slots.Refresh();
    }

    public InventoryGridSettings Grid { get; }

    public ICollectionView Slots { get; }

    [ObservableProperty]
    private bool _isVisible = true;

    public SessionState Session => _session;

    private readonly Random _rng = new(Seed: 1);
    private static readonly string[] _sampleNames =
    {
        "Sapphire", "Ruby", "Emerald", "Diamond", "Topaz",
        "Amethyst", "Pearl", "Opal", "Jade", "Garnet"
    };

    [RelayCommand]
    private void AddDebugSlot()
    {
        var index = _session.Surveys.Count;
        var name = _sampleNames[index % _sampleNames.Length];
        var bearing = _rng.NextDouble() * 2 * Math.PI;
        var distance = 30 + _rng.NextDouble() * 120;
        var offset = new MetreOffset(Math.Sin(bearing) * distance, Math.Cos(bearing) * distance);

        // 3 px/m default scale until calibrated
        var pixel = new OverlayPixel(
            _session.PlayerPosition.X + offset.East * 3,
            _session.PlayerPosition.Y - offset.North * 3);

        var survey = Survey.Create(name, offset, gridIndex: index) with { PixelPos = pixel };
        _session.Surveys.Add(new SurveyItemViewModel(survey));
    }

    [RelayCommand]
    private void ClearSlots() => _session.ClearSurveys();
}
