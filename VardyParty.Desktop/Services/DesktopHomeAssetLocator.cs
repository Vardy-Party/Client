using VardyParty.Catalog;
using VardyParty.HomeUi;
using VardyParty.Kernel;

namespace VardyParty.Desktop.Services;

/// <summary>
/// League logos ship in wwwroot/images/leagues next to the binary (same layout
/// as VardyParty.Linux). <see cref="LeagueLogoMapper"/> returns web-style
/// "/images/leagues/x.svg" paths; this maps them onto disk.
/// </summary>
public sealed class DesktopHomeAssetLocator : IHomeAssetLocator
{
    public string? ResolveLeagueLogoPath(Game game)
    {
        var webPath = LeagueLogoMapper.GetLogoForLeague(game);
        if (string.IsNullOrWhiteSpace(webPath)) return null;

        var relative = webPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var absolute = Path.Combine(AppContext.BaseDirectory, "wwwroot", relative);
        return File.Exists(absolute) ? absolute : null;
    }
}
