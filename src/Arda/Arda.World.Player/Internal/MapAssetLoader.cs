using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Mithril.MapCalibration;

namespace Arda.World.Player.Internal;

/// <summary>
/// Parses the unbracketed Player.log "Downloading Map [GUID] GUID GUID for
/// area &lt;FriendlyAreaName&gt; runtime key GUID[&lt;AssetName&gt;]" line (synthetic
/// verb <see cref="Verbs.DownloadingMap"/>) into per-scene <see cref="MapSceneRef"/>
/// state. The asset name in the runtime-key bracket is the literal Unity
/// Texture2D name (including the <c>Map_</c> prefix) and is the calibration key
/// downstream consumers use.
/// </summary>
/// <remarks>
/// <para>Builds the composite by combining the parsed friendly-name + asset-key
/// with the parent area key supplied by <see cref="IAreaState.CurrentArea"/>.
/// If no <c>Initializing area!</c> has fired yet, the parent area key is
/// <see cref="string.Empty"/>; consumers treat empty as "unknown parent" and the
/// resolution helper (see <c>SceneResolution.ResolveCurrentScene</c>) returns
/// the strict-gate <c>null</c> for that branch.</para>
///
/// <para>Malformed lines (missing <c>for area </c>, missing the runtime-key
/// bracket, empty args) are silently skipped — no state mutation, no event
/// published. The dispatch table doesn't inspect return values; safe-degrade
/// is the established Arda parser pattern.</para>
///
/// <para>Idempotent: a re-parse of the same line is a no-op event (state
/// changes once; subsequent identical parses don't fire <see cref="MapAssetChanged"/>).</para>
///
/// <para>Sub-zone-only transitions inside the same parent area (e.g. Hogan's
/// Basement → Goblin Dungeon both inside <c>AreaCave1</c>) update the composite
/// via a <c>with</c>-expression, preserving the previously-observed parent area
/// key on the new <see cref="MapSceneRef"/>.</para>
/// </remarks>
internal sealed class MapAssetLoader : IFrameHandler
{
    private readonly IDomainEventPublisher _bus;
    private readonly IAreaState _areaState;

    public MapAssetLoader(IDomainEventPublisher bus, IAreaState areaState)
    {
        _bus = bus;
        _areaState = areaState;
    }

    public MapSceneRef? CurrentMapScene { get; private set; }
    public DateTimeOffset? MapSceneMeasuredAt { get; private set; }

    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        const string ForArea = "for area ";
        const string RuntimeKey = " runtime key ";

        var forAreaIdx = args.IndexOf(ForArea);
        if (forAreaIdx < 0) return;

        var friendlyStart = forAreaIdx + ForArea.Length;
        var runtimeKeyRelIdx = args[friendlyStart..].IndexOf(RuntimeKey);
        if (runtimeKeyRelIdx < 0) return;

        var friendlyName = args.Slice(friendlyStart, runtimeKeyRelIdx).ToString();

        // The asset-name bracket MUST come after the " runtime key " marker; the
        // earlier [GUID] at args-head is also a bracket pair, so guarding against
        // it ensures malformed lines (missing close bracket on the runtime-key
        // bracket) don't silently fall back to the head-GUID bracket.
        var runtimeKeyAbsIdx = friendlyStart + runtimeKeyRelIdx + RuntimeKey.Length;
        var lastOpen = args.LastIndexOf('[');
        var lastClose = args.LastIndexOf(']');
        if (lastOpen < runtimeKeyAbsIdx) return;
        if (lastClose <= lastOpen + 1) return;

        var mapAsset = args.Slice(lastOpen + 1, lastClose - lastOpen - 1).ToString();

        // Build the composite. Parent area key comes from IAreaState (set by the
        // most-recent Initializing area! line). Empty string when no area has
        // been observed yet — consumers treat empty as strict-gate.
        var parentAreaKey = _areaState.CurrentArea ?? string.Empty;
        var previous = CurrentMapScene;
        var next = previous is { } existing && existing.ParentAreaKey == parentAreaKey
            ? existing with { SceneFriendlyName = friendlyName, MapAssetKey = mapAsset }
            : new MapSceneRef(parentAreaKey, friendlyName, mapAsset);

        // Idempotent: only mutate + publish on actual change.
        if (previous is { } p && p == next) return;

        CurrentMapScene = next;
        MapSceneMeasuredAt = metadata.Timestamp ?? metadata.ReadOn;
        _bus.Publish(new MapAssetChanged(previous, next, metadata));
    }
}
