using Arda.Abstractions.Logs;
using Arda.Ingest.Clock;

namespace Arda.Ingest.Classification;

/// <summary>
/// L1 classification gate: inspects each line span from the tailer and decides
/// whether it should enter the pipeline as a <see cref="LogLine"/> or be
/// discarded as engine noise.
/// <para>
/// Classification is permissive and operates entirely on spans — zero string
/// allocation for discarded lines:
/// <list type="bullet">
///   <item><b>Timestamped lines.</b> The clock successfully parses a prefix
///   → the line is promoted to a <see cref="LogLine"/> with the prefix stripped.</item>
///   <item><b>Known system patterns.</b> Lines matching a small allowlist of
///   non-timestamped patterns (e.g., server connection identity) are promoted
///   with a null timestamp.</item>
///   <item><b>Everything else.</b> Discarded. No allocation, no pipeline entry.</item>
/// </list>
/// </para>
/// <para>
/// The allowlist of system patterns can expand as the log grammar is catalogued.
/// See <c>docs/design/arda/log-source.md</c> for the design rationale.
/// </para>
/// </summary>
internal sealed class LineClassifier
{
    private readonly ILogSourceClock _clock;

    public LineClassifier(ILogSourceClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The clock this classifier delegates timestamp parsing to.</summary>
    internal ILogSourceClock Clock => _clock;

    /// <summary>
    /// Classify a single line span. Returns a <see cref="ClassifiedLine"/>
    /// if the line should enter the pipeline, or <c>null</c> if it should
    /// be discarded.
    /// </summary>
    public ClassifiedLine? Classify(ReadOnlySpan<char> line)
    {
        var result = _clock.TryParse(line);

        if (result.HasTimestamp)
        {
            var strippedText = line[result.ConsumedLength..].ToString();

            return new ClassifiedLine(
                Log: strippedText,
                Timestamp: result.Timestamp,
                Raw: null);
        }

        // Known system patterns that lack a timestamp but carry useful data.
        if (IsKnownSystemPattern(line))
        {
            return new ClassifiedLine(
                Log: line.ToString(),
                Timestamp: null,
                Raw: null);
        }

        return null;
    }

    /// <summary>
    /// Checks whether a non-timestamped line matches a known system pattern
    /// that should be promoted into the pipeline. Currently recognizes:
    /// <list type="bullet">
    ///   <item><c>Connecting to {host} port {port}</c> — server identity.</item>
    ///   <item><c>EVENT(Ok): connected, url={host}, port={port}</c> — connection confirmation.</item>
    ///   <item><c>Downloading Map [{guid}] GUID {guid} for area {name} runtime key {guid}[{Map_*}]</c> — per-scene asset load.</item>
    /// </list>
    /// This allowlist expands as more patterns are catalogued from log samples.
    /// </summary>
    private static bool IsKnownSystemPattern(ReadOnlySpan<char> line)
    {
        if (line.StartsWith("Connecting to ".AsSpan(), StringComparison.Ordinal))
            return true;

        if (line.StartsWith("EVENT(Ok): connected".AsSpan(), StringComparison.Ordinal))
            return true;

        if (line.StartsWith("Downloading Map ".AsSpan(), StringComparison.Ordinal))
            return true;

        return false;
    }
}

/// <summary>
/// A line that passed classification and is ready to be wrapped in
/// <see cref="LogLine"/> with full metadata by the coordinator.
/// </summary>
internal readonly record struct ClassifiedLine(
    /// <summary>The stripped game-event text (timestamp prefix removed).</summary>
    string Log,
    /// <summary>The parsed timestamp, or null for system-pattern lines.</summary>
    DateTimeOffset? Timestamp,
    /// <summary>The original unstripped line, if retained for diagnostics.</summary>
    string? Raw);
