namespace Mithril.MapCalibration;

/// <summary>
/// Holds the current <see cref="MapViewFix"/> per area and the trigger
/// for refreshing it on user gestures (toggle validation, enable motherlode
/// overlay, enable survey overlay, manual re-detect hotkey). Replaces the
/// deleted manual zoom slider as the source-of-truth for live view state.
///
/// <para><b>Threading.</b> <see cref="RefreshAsync"/> runs the probe on a
/// background thread; <see cref="Changed"/> is raised on the UI thread.
/// Concurrent <see cref="RefreshAsync"/> calls for the same area are
/// deduped — the second caller awaits the in-flight probe.</para>
///
/// <para><b>Fail-soft.</b> When the probe returns null, the prior fix
/// stays in place (markers keep rendering from the last good measurement);
/// the UI separately surfaces the failure status. When no fix has ever
/// been measured for an area, <see cref="GetCurrent"/> returns null and
/// consumers refuse to render.</para>
/// </summary>
public interface ILiveMapViewService
{
    /// <summary>The most recently measured fix for the area, or null if no
    /// measurement has ever succeeded for it.</summary>
    MapViewFix? GetCurrent(string mapAssetKey);

    /// <summary>The status of the most recent probe attempt for the area.</summary>
    LiveMapViewStatus GetStatus(string mapAssetKey);

    /// <summary>Trigger a fresh probe for the area. Concurrent calls for
    /// the same area dedupe to one in-flight probe.</summary>
    Task RefreshAsync(string mapAssetKey, CancellationToken ct = default);

    /// <summary>Raised on the UI thread after <see cref="RefreshAsync"/>
    /// completes (success or failure).</summary>
    event Action<string>? Changed;
}

public enum LiveMapViewStatus
{
    NeverMeasured,
    Detecting,
    Detected,
    FailedNoBaseTexture,
    FailedNoCapture,
    FailedLowConfidence,
}
