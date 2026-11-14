using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Display-only league heading spelling. Never writes back to
/// <see cref="Game.ApiLeague"/> or <see cref="Game.League"/>: those stay as
/// the API emitted them so stream URLs and health reports keep working.
/// </summary>
public static class LeagueDisplayName
{
    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "UEFA",
        "CAF",
        "FIFA",
        "MLS",
        "NBA",
        "NFL",
        "EFL",
    };

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "of",
        "the",
        "and",
        "de",
        "la",
        "at",
        "vs",
    };

    /// <summary>
    /// Title-cases an ALL-CAPS API key, keeping known short acronyms
    /// uppercase. Mixed-case names (BBC, already-resolved headings) are left
    /// unchanged.
    /// </summary>
    public static string ForDisplay(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || !IsAllCaps(trimmed))
        {
            return trimmed;
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var titled = new string[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            titled[i] = TitleCaseToken(words[i], firstWord: i == 0);
        }

        return string.Join(' ', titled);
    }

    /// <summary>
    /// Picks the heading for a case-insensitive league group: prefer a
    /// ResolvedLeague or BBC spelling, avoid ALL CAPS, then title-case if the
    /// winner is still an ALL-CAPS API key.
    /// </summary>
    public static string ForRow(IEnumerable<Game> games)
    {
        var candidates = new List<(string Name, bool FromReconciled, bool AllCaps, bool MixedCase)>();
        foreach (var game in games)
        {
            var name = string.IsNullOrWhiteSpace(game.DisplayLeague)
                ? string.Empty
                : game.DisplayLeague.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var fromReconciled = IsReconciledSpelling(game, name);
            candidates.Add((name, fromReconciled, IsAllCaps(name), IsMixedCase(name)));
        }

        if (candidates.Count == 0)
        {
            return HomeRowsBuilder.FallbackLeague;
        }

        var chosen = candidates
            .OrderBy(c => c.AllCaps)
            .ThenByDescending(c => c.FromReconciled)
            .ThenByDescending(c => c.MixedCase)
            .First();

        return ForDisplay(chosen.Name);
    }

    public static bool IsAllCaps(string name) =>
        name.Any(char.IsLetter) && name.All(c => !char.IsLetter(c) || char.IsUpper(c));

    private static bool IsMixedCase(string name) =>
        name.Any(char.IsUpper) && name.Any(char.IsLower);

    private static bool IsReconciledSpelling(Game game, string name)
    {
        if (!string.IsNullOrWhiteSpace(game.ResolvedLeague)
            && name.Equals(game.ResolvedLeague.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(game.BBCLeague)
            && name.Equals(game.BBCLeague.Trim(), StringComparison.Ordinal);
    }

    private static string TitleCaseToken(string token, bool firstWord)
    {
        var parts = token.Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = TitleCasePart(parts[i], firstWord && i == 0);
        }

        return string.Join('-', parts);
    }

    private static string TitleCasePart(string part, bool firstWord)
    {
        if (part.Length == 0)
        {
            return part;
        }

        if (Acronyms.Contains(part))
        {
            return part.ToUpperInvariant();
        }

        if (!firstWord && SmallWords.Contains(part))
        {
            return part.ToLowerInvariant();
        }

        return char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
    }
}
