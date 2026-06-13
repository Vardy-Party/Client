using VardyParty.Models;

namespace VardyParty.Services;

public static class InternationalTeamDisplay
{
    private const string TickerGameSeparator = "   \u26bd   ";

    public static string TickerSeparator => TickerGameSeparator;

    private static readonly Dictionary<string, string> TeamToIso = new(StringComparer.OrdinalIgnoreCase)
    {
        { "usa", "US" },
        { "united states", "US" },
        { "canada", "CA" },
        { "paraguay", "PY" },
        { "bosnia and herzegovina", "BA" },
        { "bosnia-herzegovina", "BA" },
        { "england", "GB" },
        { "scotland", "GB" },
        { "northern ireland", "GB" },
        { "republic of ireland", "IE" },
        { "ireland", "IE" },
        { "france", "FR" },
        { "germany", "DE" },
        { "spain", "ES" },
        { "italy", "IT" },
        { "portugal", "PT" },
        { "brazil", "BR" },
        { "argentina", "AR" },
        { "mexico", "MX" },
        { "netherlands", "NL" },
        { "belgium", "BE" },
        { "croatia", "HR" },
        { "serbia", "RS" },
        { "switzerland", "CH" },
        { "austria", "AT" },
        { "poland", "PL" },
        { "ukraine", "UA" },
        { "turkey", "TR" },
        { "japan", "JP" },
        { "south korea", "KR" },
        { "korea republic", "KR" },
        { "australia", "AU" },
        { "morocco", "MA" },
        { "senegal", "SN" },
        { "ghana", "GH" },
        { "nigeria", "NG" },
        { "cameroon", "CM" },
        { "ivory coast", "CI" },
        { "cote d'ivoire", "CI" },
        { "ecuador", "EC" },
        { "uruguay", "UY" },
        { "colombia", "CO" },
        { "chile", "CL" },
        { "peru", "PE" },
        { "costa rica", "CR" },
        { "saudi arabia", "SA" },
        { "laos", "LA" },
        { "qatar", "QA" },
        { "iran", "IR" },
        { "tunisia", "TN" },
        { "algeria", "DZ" },
        { "egypt", "EG" },
        { "denmark", "DK" },
        { "sweden", "SE" },
        { "norway", "NO" },
        { "finland", "FI" },
        { "czech republic", "CZ" },
        { "czechia", "CZ" },
        { "hungary", "HU" },
        { "romania", "RO" },
        { "greece", "GR" },
        { "slovakia", "SK" },
        { "slovenia", "SI" },
        { "wales", "GB" },
    };

    public static bool IsInternationalGame(Game? game)
    {
        if (game == null) return false;

        return IsInternationalMatch(
            game.DisplayLeague ?? game.League ?? game.ApiLeague,
            game.DisplayHome,
            game.DisplayAway);
    }

    public static bool IsInternationalMatch(string? league, string? home, string? away)
    {
        var leagueName = (league ?? string.Empty).Trim();
        if (leagueName.Contains("FIFA World Cup", StringComparison.OrdinalIgnoreCase)
            || leagueName.Contains("World Cup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (leagueName.Equals("Important Games", StringComparison.OrdinalIgnoreCase))
        {
            return LooksLikeNationalTeam(home) && LooksLikeNationalTeam(away);
        }

        return false;
    }

    public static string FormatTeamName(string? displayName, bool international)
    {
        var name = FormatTeamNamePlain(displayName);
        if (string.IsNullOrEmpty(name) || !international)
        {
            return name;
        }

        var flag = GetFlagEmoji(name);
        return string.IsNullOrEmpty(flag) ? name : $"{flag} {name}";
    }

    public static string FormatTeamNamePlain(string? displayName) =>
        (displayName ?? string.Empty).Trim();

    public static bool TryGetIsoCode(string? teamName, out string iso)
    {
        iso = GetIsoCode(teamName) ?? string.Empty;
        return iso.Length == 2;
    }

    public static string? GetFlagImageUrl(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso) || iso.Length != 2)
        {
            return null;
        }

        return $"https://flagcdn.com/16x12/{iso.ToLowerInvariant()}.png";
    }

    public static IEnumerable<TickerDisplayPart> TeamParts(string? displayName, bool international)
    {
        var name = FormatTeamNamePlain(displayName);
        if (string.IsNullOrEmpty(name))
        {
            yield break;
        }

        if (international && TryGetIsoCode(name, out var iso))
        {
            var flagUrl = GetFlagImageUrl(iso);
            if (!string.IsNullOrEmpty(flagUrl))
            {
                yield return new TickerDisplayPart(string.Empty, flagUrl);
                yield return new TickerDisplayPart($" {name}");
                yield break;
            }
        }

        yield return new TickerDisplayPart(name);
    }

    public static IEnumerable<TickerDisplayPart> TextParts(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        yield return new TickerDisplayPart(text);
    }

    public static IEnumerable<TickerDisplayPart> SeparatorParts()
    {
        yield return new TickerDisplayPart(TickerSeparator);
    }

    public static string PartsToPlainText(IEnumerable<TickerDisplayPart> parts) =>
        string.Concat(parts.Select(p => p.Text));

    public static string FormatMatchTitle(string? home, string? away, bool international)
    {
        var homeText = FormatTeamName(home, international);
        var awayText = FormatTeamName(away, international);
        if (string.IsNullOrEmpty(homeText) && string.IsNullOrEmpty(awayText)) return string.Empty;
        if (string.IsNullOrEmpty(homeText)) return awayText;
        if (string.IsNullOrEmpty(awayText)) return homeText;
        return $"{homeText} vs {awayText}";
    }

    private static bool LooksLikeNationalTeam(string? teamName)
    {
        if (string.IsNullOrWhiteSpace(teamName)) return false;
        return GetIsoCode(teamName) != null;
    }

    private static string? GetFlagEmoji(string teamName)
    {
        var iso = GetIsoCode(teamName);
        if (string.IsNullOrEmpty(iso)) return null;

        if (iso.StartsWith("GB-", StringComparison.OrdinalIgnoreCase))
        {
            iso = "GB";
        }

        if (iso.Length != 2) return null;

        return IsoToFlagEmoji(iso);
    }

    private static string? GetIsoCode(string teamName)
    {
        var normalized = Normalize(teamName);
        if (string.IsNullOrEmpty(normalized)) return null;

        if (TeamToIso.TryGetValue(normalized, out var iso))
        {
            return iso;
        }

        return null;
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string IsoToFlagEmoji(string iso2)
    {
        var upper = iso2.ToUpperInvariant();
        if (upper.Length != 2) return string.Empty;

        return string.Concat(
            char.ConvertFromUtf32(0x1F1E6 + (upper[0] - 'A')),
            char.ConvertFromUtf32(0x1F1E6 + (upper[1] - 'A')));
    }
}
