using System.Globalization;

namespace VardyParty.Presentation;

/// <summary>Primary/secondary colours for a team, as #RRGGBB hex strings.</summary>
public readonly record struct TeamColors(string Primary, string Secondary);

/// <summary>
/// Maps team names to brand colours for the "ephemeral graphics" card washes.
/// Well-known clubs get curated colours; everything else gets a deterministic,
/// pleasant fallback derived from the team name (same name → same colour, and
/// the saturation/lightness are clamped so it always reads well on a dark UI).
/// </summary>
public static class TeamPalette
{
    private static readonly Dictionary<string, TeamColors> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Premier League / EFL
        ["Arsenal"] = new("#EF0107", "#023474"),
        ["Aston Villa"] = new("#7A003C", "#95BFE5"),
        ["Bournemouth"] = new("#DA291C", "#000000"),
        ["AFC Bournemouth"] = new("#DA291C", "#000000"),
        ["Brentford"] = new("#E30613", "#FBB800"),
        ["Brighton"] = new("#0057B8", "#FFCD00"),
        ["Brighton & Hove Albion"] = new("#0057B8", "#FFCD00"),
        ["Burnley"] = new("#6C1D45", "#99D6EA"),
        ["Charlton Athletic"] = new("#E31B23", "#000000"),
        ["Chelsea"] = new("#034694", "#DBA111"),
        ["Crystal Palace"] = new("#1B458F", "#C4122E"),
        ["Everton"] = new("#003399", "#FFFFFF"),
        ["Fulham"] = new("#000000", "#CC0000"),
        ["Leeds United"] = new("#1D428A", "#FFCD00"),
        ["Leicester City"] = new("#003090", "#FDBE11"),
        ["Liverpool"] = new("#C8102E", "#00B2A9"),
        ["Manchester City"] = new("#6CABDD", "#1C2C5B"),
        ["Man City"] = new("#6CABDD", "#1C2C5B"),
        ["Manchester United"] = new("#DA291C", "#FBE122"),
        ["Man Utd"] = new("#DA291C", "#FBE122"),
        ["Newcastle United"] = new("#241F20", "#FFFFFF"),
        ["Newcastle"] = new("#241F20", "#FFFFFF"),
        ["Nottingham Forest"] = new("#DD0000", "#FFFFFF"),
        ["Sunderland"] = new("#EB172B", "#FFFFFF"),
        ["Tottenham Hotspur"] = new("#132257", "#FFFFFF"),
        ["Tottenham"] = new("#132257", "#FFFFFF"),
        ["West Ham United"] = new("#7A263A", "#1BB1E7"),
        ["West Ham"] = new("#7A263A", "#1BB1E7"),
        ["Wolverhampton Wanderers"] = new("#FDB913", "#231F20"),
        ["Wolves"] = new("#FDB913", "#231F20"),

        // Scotland
        ["Celtic"] = new("#018749", "#FFFFFF"),
        ["Rangers"] = new("#0033A0", "#D00027"),
        ["Hearts"] = new("#800910", "#FFFFFF"),
        ["Heart of Midlothian"] = new("#800910", "#FFFFFF"),
        ["Hibernian"] = new("#006630", "#FFFFFF"),
        ["Aberdeen"] = new("#E20E0E", "#FFFFFF"),

        // Spain
        ["Real Madrid"] = new("#FEBE10", "#00529F"),
        ["Barcelona"] = new("#A50044", "#004D98"),
        ["Atletico Madrid"] = new("#CB3524", "#262E62"),
        ["Atlético Madrid"] = new("#CB3524", "#262E62"),
        ["Sevilla"] = new("#D8091C", "#FFFFFF"),
        ["Athletic Bilbao"] = new("#EE2523", "#FFFFFF"),
        ["Real Sociedad"] = new("#0067B1", "#FFFFFF"),
        ["Villarreal"] = new("#FFE667", "#005187"),
        ["Valencia"] = new("#EE3524", "#FFDF1C"),

        // Germany
        ["Bayern Munich"] = new("#DC052D", "#0066B2"),
        ["Borussia Dortmund"] = new("#FDE100", "#000000"),
        ["Bayer Leverkusen"] = new("#E32221", "#000000"),
        ["RB Leipzig"] = new("#DD0741", "#001F47"),
        ["Eintracht Frankfurt"] = new("#E1000F", "#000000"),

        // Italy
        ["Juventus"] = new("#000000", "#FFFFFF"),
        ["Inter Milan"] = new("#0068A8", "#221F20"),
        ["Internazionale"] = new("#0068A8", "#221F20"),
        ["AC Milan"] = new("#FB090B", "#000000"),
        ["Napoli"] = new("#12A0D7", "#003C82"),
        ["Roma"] = new("#8E1F2F", "#F0BC42"),
        ["Lazio"] = new("#87D8F7", "#FFFFFF"),
        ["Atalanta"] = new("#1E71B8", "#000000"),

        // France
        ["Paris Saint-Germain"] = new("#004170", "#DA291C"),
        ["PSG"] = new("#004170", "#DA291C"),
        ["Marseille"] = new("#2FAEE0", "#FFFFFF"),
        ["Olympique de Marseille"] = new("#2FAEE0", "#FFFFFF"),
        ["Olympique Lyonnais"] = new("#DA001A", "#153D8A"),
        ["Lyon"] = new("#DA001A", "#153D8A"),
        ["Monaco"] = new("#E6331B", "#FFFFFF"),
        ["Lille"] = new("#E01E13", "#120E4B"),

        // Portugal / Netherlands / elsewhere in Europe
        ["Porto"] = new("#00428C", "#FFFFFF"),
        ["Benfica"] = new("#E83030", "#FFFFFF"),
        ["Sporting CP"] = new("#008057", "#FFFFFF"),
        ["Ajax"] = new("#D2122E", "#FFFFFF"),
        ["PSV"] = new("#ED1C24", "#FFFFFF"),
        ["PSV Eindhoven"] = new("#ED1C24", "#FFFFFF"),
        ["Feyenoord"] = new("#E30613", "#000000"),
        ["Galatasaray"] = new("#A90432", "#FDB912"),
        ["Fenerbahce"] = new("#FFED00", "#163962"),
        ["Fenerbahçe"] = new("#FFED00", "#163962"),
        ["Besiktas"] = new("#000000", "#FFFFFF"),
        ["Beşiktaş"] = new("#000000", "#FFFFFF"),
        ["Rapid Wien"] = new("#009036", "#FFFFFF"),
        ["Rapid Vienna"] = new("#009036", "#FFFFFF"),
        ["AEK Athens"] = new("#FFD700", "#000000"),
        ["Olympiacos"] = new("#D6001C", "#FFFFFF"),
        ["Levski Sofia"] = new("#0053A0", "#FFFFFF"),
        ["Celje"] = new("#FFD500", "#00529C"),
        ["Slovan Bratislava"] = new("#6CB5E5", "#FFFFFF"),
        ["Red Star Belgrade"] = new("#D0103A", "#FFFFFF"),
        ["Shakhtar Donetsk"] = new("#F36F21", "#000000"),

        // International sides
        ["England"] = new("#CE1124", "#FFFFFF"),
        ["Scotland"] = new("#005EB8", "#FFFFFF"),
        ["Wales"] = new("#D30731", "#FFFFFF"),
        ["Northern Ireland"] = new("#008751", "#FFFFFF"),
        ["Republic of Ireland"] = new("#169B62", "#FF883E"),
        ["France"] = new("#0055A4", "#EF4135"),
        ["Germany"] = new("#000000", "#DD0000"),
        ["Spain"] = new("#AA151B", "#F1BF00"),
        ["Italy"] = new("#008C45", "#FFFFFF"),
        ["Portugal"] = new("#046A38", "#DA291C"),
        ["Netherlands"] = new("#FF6600", "#FFFFFF"),
        ["Belgium"] = new("#E30613", "#FDDA24"),
        ["Brazil"] = new("#009C3B", "#FFDF00"),
        ["Argentina"] = new("#74ACDF", "#FFFFFF"),
        ["Croatia"] = new("#ED1C24", "#0F4C81"),
    };

    public static TeamColors GetColors(string? teamName)
    {
        var name = (teamName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return new TeamColors("#4B5563", "#9CA3AF");
        }

        if (Known.TryGetValue(name, out var colors))
        {
            return colors;
        }

        // Try again without common suffixes ("FC", "CF", "SC", "AFC").
        var stripped = StripSuffix(name);
        if (!ReferenceEquals(stripped, name) && Known.TryGetValue(stripped, out colors))
        {
            return colors;
        }

        return FromHash(name);
    }

    private static string StripSuffix(string name)
    {
        string[] suffixes = [" FC", " CF", " SC", " AFC", " CD", " SK", " BK"];
        foreach (var suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length].TrimEnd();
            }
        }
        return name;
    }

    /// <summary>
    /// Deterministic fallback: FNV-1a hash of the lowercase name picks a hue;
    /// saturation and lightness are fixed so the wash always suits a dark UI.
    /// </summary>
    private static TeamColors FromHash(string name)
    {
        uint hash = 2166136261;
        foreach (var ch in name.ToLowerInvariant())
        {
            hash ^= ch;
            hash *= 16777619;
        }

        var hue = hash % 360u;
        var primary = HslToHex(hue, 0.62, 0.44);
        var secondary = HslToHex((hue + 40) % 360, 0.55, 0.62);
        return new TeamColors(primary, secondary);
    }

    private static string HslToHex(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        var red = (int)Math.Round((r + m) * 255);
        var green = (int)Math.Round((g + m) * 255);
        var blue = (int)Math.Round((b + m) * 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{red:X2}{green:X2}{blue:X2}");
    }
}
