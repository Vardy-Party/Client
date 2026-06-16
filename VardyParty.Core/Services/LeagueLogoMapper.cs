using System.Diagnostics.CodeAnalysis;
using VardyParty.Models;

namespace VardyParty.Services;

public static class LeagueLogoMapper
{
    public static string GetLogoForLeague(Game game)
    {
        var name = !string.IsNullOrEmpty(game.BBCLeague) ? game.BBCLeague :
                  !string.IsNullOrEmpty(game.League) ? game.League :
                  string.Empty;

        return GetLogoForLeague(name);
    }

    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    private static string GetLogoForLeague(string? league)
    {
        if (string.IsNullOrWhiteSpace(league)) return string.Empty;
        var name = league.Trim();

        var path = name switch
        {
            _ when IsExact("Lebanese Premier League") => "images/leagues/lebanese-premier-league.png",
            _ when IsExact("FIFA World Cup") || IsExact("World Cup") => "images/leagues/fifa-world-cup-2026.svg",
            _ when Is("DFB Pokal") => "images/leagues/DFB_2025-logo_brandlogos.net_635b47.svg",
            _ when Is("Olympic") => "images/leagues/milano-cortina-2026-logo-brandlogos.net_wa7r7kszb.svg",
            _ when Is("Coupe de France") => "images/leagues/Coupe_de_France-logo.svg",
            _ when Is("MLS") || Is("Major League Soccer") || Is("US Major League Soccer") => "images/leagues/MLS-logo-Brandlogos.net.svg",
            _ when Is("Japan J1 League") || Is("J1 League") || Is("J.League") => "images/leagues/J.League.svg",
            _ when Is("Copa del Rey") => "images/leagues/serie-a-logo-brandlogos.net_hklrxdbdu.svg",
            _ when Is("Copa De La Reina") || Is("Copa de la Reina") => "images/leagues/Copa-De-La-Reina-RFEF.svg",
            _ when Is("La Liga 2") || Is("Segunda") || Is("laliga-hypermotion") => "images/leagues/laliga-hypermotion-logo-brandlogos.net_dn0w6izjc.svg",
            _ when Is("La Liga") || Is("laliga") => "images/leagues/la-liga-2023-logo-brandlogos.net_fi7yd18xl.svg",
            _ when Is("Serie A") => "images/leagues/serie-a-logo-brandlogos.net_hklrxdbdu.svg",
            _ when Is("Coppa Italia") || Is("Coppa d'Italia") || Is("Coppa_Italia") => "images/leagues/Coppa_Italia-OFndl6WG7_brandlogos.net.svg",
            _ when Is("Ligue 1") => "images/leagues/ligue-1-mcdonalds-vertical-logo-brandlogos.net_e79ws96yb.svg",
            _ when Is("Ligue 2") => "images/leagues/ligue-2-bkt-logo-brandlogos.net_a3f5wr67g.svg",
            _ when Is("Saudi Pro League") || Is("Saudi Arabian League") => "images/leagues/saudi-pro-league-logo-brandlogos.net_tik3d950d.svg",
            _ when Is("Primeiralia") || Is("Primeiraliga") || Is("Primeira Liga") || Is("Primeira") => "images/leagues/liga-portugal-logo-brandlogos.net_2b7dby3qh.svg",
            _ when Is("EFL Trophy") || Is("English Football League Trophy") || Is("EFL Vertu Trophy") || Is("Vertu Trophy") => "images/leagues/EFL_vertu_Trophy_lpgp.svg",
            _ when IsExact("Premier League") => "images/leagues/premier-league-logo-brandlogos.net_8gx2ul0qq.svg",
            _ when Is("USL Championship") => "images/leagues/USL_Championship-logo.svg",
            _ when Is("Championship") => "images/leagues/EFL_Championship-VM8vCs3X_brandlogos.net.svg",
            _ when Is("League 1") || Is("League One") => "images/leagues/EFL_League_One-OpT6pjxfV_brandlogos.net.svg",
            _ when Is("League 2") || Is("League Two") => "images/leagues/EFL_League_Two-O9RxRtxBf_brandlogos.net.svg",
            _ when Is("Scottish Premiership") => "images/leagues/scottish-premiership-logo-brandlogos.net_rhkzuu3i1.svg",
            _ when Is("Copa Libertadores") || Is("Libertadores") => "images/leagues/copa-libertadores-logo-brandlogos.net_4ttrycfhm.svg",
            _ when Is("Africa Cup") || Is("AFCON") || Is("Africa Cup of Nations") => "images/leagues/2025-africa-cup-of-nations-logo-brandlogos.net_fbuximlvy.svg",
            _ when Is("FA Cup") || Is("Emirates FA Cup") || Is("Emirates") => "images/leagues/Emirates_FA_Cup-5g9PL9zG_brandlogos.net.svg",
            _ when Is("League Cup") || Is("Carabao Cup") || Is("EFL Cup") || Is("Carabao") => "images/leagues/carabao-cup-efl-cup-logo-brandlogos.net_j9hqfyazu.svg",
            _ when Is("Bundesliga") => "images/leagues/bundesliga-logo-2AA0A3yP_brandlogos.net.svg",
            _ when Is("Spanish Supercopa") => "images/leagues/Supercopa_de_Espa~na-logo_brandlogos.net_8b74ed.svg",
            _ when Is("Turkish Super Lig") || Is("superlig") => "images/leagues/Super_Lig-OZzLTV8bU_brandlogos.net.svg",
            _ when Is("Brack Super League") || Is("Swiss Super League") => "images/leagues/swiss-super-league-logo-brandlogos.net_n12imc10u.svg",
            _ when Is("UEFA Champions League") || Is("Champions League") => "images/leagues/uefa-champions-league-logo-brandlogos.net_iyyz8y0dw.svg",
            _ when Is("UEFA Europa League") || Is("Europa League") => "images/leagues/uefa-europa-league-2024-logo-brandlogos.net_j9ualbg4k.svg",
            _ when Is("UEFA Conference League") || Is("Conference League") || Is("Europa Conference League") => "images/leagues/uefa-europa-conference-league-logo-75E25sU4_brandlogos.net.svg",
            _ when Is("GFF League") => "images/leagues/gambia_gambia-national-team.football-logos.cc.svg",
            _ => string.Empty
        };

        return string.IsNullOrEmpty(path) ? string.Empty : $"/{path.TrimStart('/')}";

        bool Is(string target) => name.Contains(target, StringComparison.OrdinalIgnoreCase);
        bool IsExact(string target) => name.Equals(target, StringComparison.OrdinalIgnoreCase);
    }
}
