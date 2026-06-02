using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

/// <summary>
/// Pins the snapshot contract on <see cref="IMapPinState.Pins"/>. The crash that
/// motivated this came from a consumer enumerating the property while the Arda
/// ingest thread mutated the backing list; the fix is to return a snapshot, so a
/// captured collection must be unaffected by subsequent mutations. See
/// docs/planning/arda-state-snapshot-and-ui-dispatch/spec.md.
/// </summary>
public sealed class MapPinsSnapshotTests
{
    private static LogLineMetadata Meta()
        => new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void Pins_ReturnsSnapshot_NotLiveView()
    {
        var bus = new RecordingPublisher();
        var pins = new MapPins(bus);

        // Add one pin.
        pins.PinAddHandler.Handle(
            args: "1, 0, 1, (100.00, 0.00, 200.00), \"first\"".AsSpan(),
            verb: "ProcessMapPinAdd".AsSpan(),
            sourceLog: "Player.log",
            metadata: Meta());

        // Capture the collection reference *before* mutating again.
        var captured = pins.Pins;
        captured.Should().HaveCount(1);

        // Mutate the underlying state (the racing scenario).
        pins.PinAddHandler.Handle(
            args: "1, 0, 1, (300.00, 0.00, 400.00), \"second\"".AsSpan(),
            verb: "ProcessMapPinAdd".AsSpan(),
            sourceLog: "Player.log",
            metadata: Meta());

        // The previously-captured collection MUST NOT reflect the new pin.
        // If Pins returns _pins directly, captured.Count == 2 here and the test fails.
        captured.Should().HaveCount(1, "Pins is a snapshot at the moment of read");
        pins.Pins.Should().HaveCount(2, "a fresh read sees the up-to-date set");
    }

    private sealed class RecordingPublisher : IDomainEventPublisher
    {
        public void Publish<T>(T domainEvent) where T : struct { }
    }
}
