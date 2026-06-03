using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Parses the unbracketed Player.log "Downloading Map [GUID] GUID GUID for
/// area &lt;FriendlyAreaName&gt; runtime key GUID[&lt;AssetName&gt;]" line (synthetic
/// verb <see cref="Verbs.DownloadingMap"/>) into per-scene map state. The
/// asset name in the runtime-key bracket is the literal Unity Texture2D
/// name (including the <c>Map_</c> prefix) and is the calibration key
/// downstream consumers use.
/// </summary>
/// <remarks>
/// <para>Malformed lines (missing <c>for area </c>, missing the runtime-key
/// bracket, empty args) are silently skipped — no state mutation, no event
/// published. The dispatch table doesn't inspect return values; safe-degrade
/// is the established Arda parser pattern.</para>
///
/// <para>Idempotent: a re-parse of the same line is a no-op event (state
/// changes once; subsequent identical parses don't fire <see cref="MapAssetChanged"/>).</para>
/// </remarks>
internal sealed class MapAssetLoader : IFrameHandler
{
    private readonly IDomainEventPublisher _bus;

    public MapAssetLoader(IDomainEventPublisher bus)
    {
        _bus = bus;
    }

    public string? CurrentMapAsset { get; private set; }
    public string? CurrentSceneFriendlyName { get; private set; }
    public DateTimeOffset? MapAssetMeasuredAt { get; private set; }

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

        // Idempotent: only mutate + publish on actual change.
        if (string.Equals(mapAsset, CurrentMapAsset, StringComparison.Ordinal)
            && string.Equals(friendlyName, CurrentSceneFriendlyName, StringComparison.Ordinal))
        {
            return;
        }

        var previous = CurrentMapAsset;
        CurrentMapAsset = mapAsset;
        CurrentSceneFriendlyName = friendlyName;
        MapAssetMeasuredAt = metadata.Timestamp ?? metadata.ReadOn;
        _bus.Publish(new MapAssetChanged(previous, CurrentMapAsset, CurrentSceneFriendlyName, metadata));
    }
}
