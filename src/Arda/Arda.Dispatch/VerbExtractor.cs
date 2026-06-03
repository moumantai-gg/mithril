namespace Arda.Dispatch;

/// <summary>
/// Result of <see cref="VerbExtractor.Parse"/>: the verb span and args span
/// extracted from a single log line in one pass.
/// </summary>
internal readonly ref struct ParsedVerb
{
    public readonly ReadOnlySpan<char> Verb;
    public readonly ReadOnlySpan<char> Args;

    public ParsedVerb(ReadOnlySpan<char> verb, ReadOnlySpan<char> args)
    {
        Verb = verb;
        Args = args;
    }

    public bool IsEmpty => Verb.IsEmpty;
}

/// <summary>
/// Extracts the verb and args from a log line's content (timestamp prefix already
/// stripped) in a single pass. Returns spans into the source — zero allocation.
/// Handles both Player.log grammar and Chat log grammar.
/// </summary>
internal static class VerbExtractor
{
    private const string LocalPlayerPrefix = "LocalPlayer: ";
    private const string StatusInventorySuffix = "added to inventory.";
    private const string ChatLoginBannerPrefix = "****";
    private const string StatusPrefix = "[Status] ";

    /// <summary>
    /// Parse a log line into its verb and args in a single pass. Both returned
    /// spans are slices of the input — no string allocation occurs.
    /// </summary>
    public static ParsedVerb Parse(ReadOnlySpan<char> log)
    {
        // Player.log: "LocalPlayer: VerbName(args...)"
        if (log.StartsWith(LocalPlayerPrefix))
        {
            var afterPrefix = log[LocalPlayerPrefix.Length..];
            var parenIdx = afterPrefix.IndexOf('(');
            if (parenIdx > 0)
                return new ParsedVerb(afterPrefix[..parenIdx], afterPrefix[parenIdx..]);
            return new ParsedVerb(afterPrefix, ReadOnlySpan<char>.Empty);
        }

        // System lines: "LOADING LEVEL [AreaKey]"
        if (log.StartsWith("LOADING LEVEL "))
            return new ParsedVerb(Verbs.LoadingLevel, log["LOADING LEVEL ".Length..]);

        if (log is "LOADING LEVEL")
            return new ParsedVerb(Verbs.LoadingLevel, ReadOnlySpan<char>.Empty);

        // System lines: "!!! Initializing area! (id): AreaKey"
        if (log.StartsWith("!!! Initializing area! "))
            return new ParsedVerb(Verbs.InitializingArea, log["!!! Initializing area! ".Length..]);

        if (log.StartsWith("!!! Initializing area!"))
            return new ParsedVerb(Verbs.InitializingArea, ReadOnlySpan<char>.Empty);

        // Asset loader: "Downloading Map [hash] GUID hash for area X runtime key hash[Map_<X>]"
        if (log.StartsWith("Downloading Map "))
            return new ParsedVerb(Verbs.DownloadingMap, log["Downloading Map ".Length..]);

        // Chat: login banner — "**** Logged In As ..."
        if (log.StartsWith(ChatLoginBannerPrefix))
            return new ParsedVerb(Verbs.ChatLoginBanner, log);

        // Chat: "[Status] ... added to inventory." — only the inventory variant
        if (log.StartsWith(StatusPrefix))
        {
            if (log.EndsWith(StatusInventorySuffix))
                return new ParsedVerb(Verbs.StatusInventory, log);
            return default;
        }

        // Chat: "[Channel] Speaker: text"
        if (log.Length > 1 && log[0] == '[')
        {
            var closeBracket = log.IndexOf(']');
            if (closeBracket > 1 && log.Length > closeBracket + 2)
                return new ParsedVerb(Verbs.ChatPlayerLine, log);
        }

        return default;
    }
}
