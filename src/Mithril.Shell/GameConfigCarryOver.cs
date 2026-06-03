using System.IO;
using System.Text.Json;

namespace Mithril.Shell;

/// <summary>
/// One-time composition-root carry-over (#919) for the two settings relocated
/// from <c>LegolasSettings</c> (a module) to the shared <see cref="ShellSettings"/>
/// store: <see cref="ShellSettings.GameProcessName"/> and
/// <see cref="ShellSettings.CalibrationGoodResidualPx"/>.
///
/// <para>Because the values move <em>between files</em> (Legolas's
/// <c>legolas.json</c> → the shell's <c>shell.json</c>), a per-file
/// <c>IVersionedState.Migrate</c> cannot reach across them. Instead we read the
/// pre-migration values straight out of the raw <c>legolas.json</c> on disk
/// (the keys persist there for upgrading users even though
/// <c>LegolasSettings</c> no longer declares the fields) and copy each one into
/// the shell store <strong>only when the shell value is still its factory
/// default</strong>. That makes the carry-over idempotent: once the user (or a
/// prior run) has set the shared value, we never clobber it, and on a fresh
/// install with no legolas.json it is a no-op.</para>
/// </summary>
public static class GameConfigCarryOver
{
    // Factory defaults — must mirror ShellSettings / GameConfig. A shell value
    // still equal to its default is the signal that nothing has been carried or
    // edited yet, so the legacy legolas value (if any) may flow in.
    internal const string DefaultGameProcessName = "ProjectGorgon";
    internal const double DefaultCalibrationGoodResidualPx = 12.0;

    /// <summary>
    /// Mutates <paramref name="shell"/> in place. Safe to call on every startup;
    /// only the first run (shell value still default + non-default legolas value)
    /// changes anything.
    /// </summary>
    /// <param name="legolasSettingsPath">Path to the module's <c>legolas.json</c>.</param>
    /// <param name="shell">The loaded shared shell settings.</param>
    /// <returns><c>true</c> if any value was carried over (so the caller can persist).</returns>
    public static bool Apply(string legolasSettingsPath, ShellSettings shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (string.IsNullOrEmpty(legolasSettingsPath) || !File.Exists(legolasSettingsPath))
            return false;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legolasSettingsPath));
            // Clone so the value survives the using-block dispose.
            root = doc.RootElement.Clone();
        }
        catch
        {
            // Corrupt / unreadable legolas.json — nothing to carry. The module's
            // own loader will surface any real problem.
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object) return false;

        var carried = false;

        // GameProcessName — carry only when shell is still the factory default
        // and legolas holds a non-empty, non-default value.
        if (string.Equals(shell.GameProcessName, DefaultGameProcessName, StringComparison.Ordinal)
            && root.TryGetProperty("gameProcessName", out var procEl)
            && procEl.ValueKind == JsonValueKind.String)
        {
            var legacy = procEl.GetString()?.Trim();
            if (!string.IsNullOrEmpty(legacy)
                && !string.Equals(legacy, DefaultGameProcessName, StringComparison.Ordinal))
            {
                shell.GameProcessName = legacy;
                carried = true;
            }
        }

        // CalibrationGoodResidualPx — carry only when shell is still the factory
        // default and legolas holds a positive, non-default value.
        if (Math.Abs(shell.CalibrationGoodResidualPx - DefaultCalibrationGoodResidualPx) < 1e-9
            && root.TryGetProperty("calibrationGoodResidualPx", out var resEl)
            && resEl.ValueKind == JsonValueKind.Number
            && resEl.TryGetDouble(out var legacyResidual)
            && legacyResidual > 0
            && Math.Abs(legacyResidual - DefaultCalibrationGoodResidualPx) > 1e-9)
        {
            shell.CalibrationGoodResidualPx = legacyResidual;
            carried = true;
        }

        return carried;
    }
}
