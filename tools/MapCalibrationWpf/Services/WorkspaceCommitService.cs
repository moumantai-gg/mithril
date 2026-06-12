namespace Mithril.Tools.MapCalibrationWpf.Services;

using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Stamps the solved <see cref="AreaCalibration"/> with the BundledBaseline
/// metadata and writes it to <c>src/Mithril.MapCalibration/BundledData/
/// map-calibration-baseline.json</c> via <see cref="BaselineFile.UpsertAnchor"/>.
///
/// <para>The same path the CLI writes to (resolved via <see cref="RepoPaths"/>)
/// so the WPF tool's commit lands exactly where the existing baseline test
/// suite reads from.</para>
/// </summary>
public sealed class WorkspaceCommitService
{
    public void Commit(string area, AreaCalibration calibration)
    {
        var stamped = calibration with
        {
            Source = CalibrationSource.BundledBaseline,
            SchemaVersion = 1,
        };
        BaselineFile.UpsertAnchor(RepoPaths.BaselineJsonPath(), area, stamped);
    }

    /// <summary>
    /// Reads the currently-stored anchor for <paramref name="area"/>. Returns
    /// null when no anchor exists yet — the workspace treats that as "dirty"
    /// (commit-enabled).
    /// </summary>
    public AreaCalibration? ReadStored(string area)
    {
        try
        {
            return BaselineFile.TryReadAnchor(RepoPaths.BaselineJsonPath(), area);
        }
        catch (UserFacingException)
        {
            // Either the baseline path doesn't resolve (running outside the
            // repo — RepoPaths throws), or the JSON is malformed / has a
            // required field missing for this area (TryReadAnchor throws).
            // Either way pretend "no stored anchor" so the workspace treats
            // the in-progress calibration as dirty and the commit button
            // stays available — the user can see the underlying issue when
            // they try to commit (UpsertAnchor will re-surface the error).
            return null;
        }
    }
}
