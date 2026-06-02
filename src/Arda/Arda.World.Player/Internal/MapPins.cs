using System.Globalization;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks the player's map pins via <c>ProcessMapPinAdd</c> / <c>ProcessMapPinRemove</c>.
/// Pins are per-area — the set is cleared on area transition.
/// <para>
/// Args format: <c>(A, shape, color, (x, y, z), "label")</c>
/// where A is an opaque leading int (invariant 1), y is always 0.00 and skipped.
/// </para>
/// <para>
/// Exposes <see cref="PinAddHandler"/> and <see cref="PinRemoveHandler"/> for verb
/// registration — the two verbs share the same args format, so a single
/// <see cref="IFrameHandler.Handle"/> method cannot differentiate them.
/// </para>
/// </summary>
internal sealed class MapPins : IMapPinState
{
    private readonly IDomainEventPublisher _bus;
    private readonly List<MapPinEntry> _pins = [];

    public IReadOnlyCollection<MapPinEntry> Pins => _pins.ToArray();
    internal IReadOnlyList<MapPinEntry> PinsList => _pins.ToArray();

    public MapPins(IDomainEventPublisher bus) => _bus = bus;

    internal IFrameHandler PinAddHandler => new AddVerb(this);
    internal IFrameHandler PinRemoveHandler => new RemoveVerb(this);

    internal void Reset() => _pins.Clear();

    private void OnAdd(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        if (!TryParsePin(args, verb, sourceLog, out var x, out var z, out var label, out var shape, out var color))
            return;

        _pins.Add(new MapPinEntry(x, z, label, shape, color));
        _bus.Publish(new MapPinAdded(x, z, label, shape, color, metadata));
    }

    private void OnRemove(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        if (!TryParsePin(args, verb, sourceLog, out var x, out var z, out var label, out _, out _))
            return;

        for (var i = _pins.Count - 1; i >= 0; i--)
        {
            var pin = _pins[i];
            if (Math.Abs(pin.X - x) < 0.01 && Math.Abs(pin.Z - z) < 0.01)
            {
                _pins.RemoveAt(i);
                break;
            }
        }

        _bus.Publish(new MapPinRemoved(x, z, label, metadata));
    }

    /// <summary>
    /// Parse <c>(A, shape, color, (x, y, z), "label")</c>.
    /// Manually finds the nested coordinate parens and splits around them.
    /// </summary>
    private static bool TryParsePin(
        ReadOnlySpan<char> args,
        ReadOnlySpan<char> verb,
        string sourceLog,
        out double x, out double z,
        out string label, out int shape, out int color)
    {
        x = 0; z = 0; label = ""; shape = 0; color = 0;

        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return false;

        // Find the nested '(' that starts the coordinate tuple.
        // Format so far: "1, 0, 0, (1425.06, 0.00, 2924.99), "South""
        var nestedOpen = inner.IndexOf('(');
        if (nestedOpen < 0)
            return false;

        // Everything before the '(' is "A, shape, color, "
        var prefix = inner[..nestedOpen].TrimEnd();
        if (prefix.Length > 0 && prefix[^1] == ',')
            prefix = prefix[..^1];

        // Parse A, shape, color from the prefix via comma split
        var prefixTok = new ArgTokenizer(prefix, verb, sourceLog);
        prefixTok.Skip(1); // A (opaque)

        var shapeSpan = prefixTok.NextTokenSpan();
        if (!int.TryParse(shapeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out shape))
            return false;

        var colorSpan = prefixTok.NextTokenSpan();
        if (!int.TryParse(colorSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out color))
            return false;

        // Extract coordinate tuple: find matching ')'
        var afterOpen = inner[(nestedOpen + 1)..];
        var nestedClose = afterOpen.IndexOf(')');
        if (nestedClose < 0)
            return false;

        var coords = afterOpen[..nestedClose];

        // Parse x, y, z from the coordinate triple
        var coordTok = new ArgTokenizer(coords, verb, sourceLog);
        var xSpan = coordTok.NextTokenSpan();
        if (!double.TryParse(xSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out x))
            return false;

        coordTok.Skip(1); // y (always 0.00)

        var zSpan = coordTok.NextTokenSpan();
        if (!double.TryParse(zSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            return false;

        // Everything after the coordinate close paren: ), "label")
        var suffix = afterOpen[(nestedClose + 1)..];
        // Strip leading ", " to get to the label
        var quoteIdx = suffix.IndexOf('"');
        if (quoteIdx >= 0)
        {
            var afterQuote = suffix[(quoteIdx + 1)..];
            var closeQuote = afterQuote.IndexOf('"');
            label = closeQuote >= 0
                ? afterQuote[..closeQuote].ToString()
                : afterQuote.ToString();
        }

        return true;
    }

    private sealed class AddVerb(MapPins owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => owner.OnAdd(args, verb, sourceLog, metadata);
    }

    private sealed class RemoveVerb(MapPins owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => owner.OnRemove(args, verb, sourceLog, metadata);
    }
}
