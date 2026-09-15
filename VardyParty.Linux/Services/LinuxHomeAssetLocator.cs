using VardyParty.Catalog;
using VardyParty.HomeUi;
using VardyParty.Kernel;

namespace VardyParty.Linux.Services;

/// <summary>
/// League logos ship in wwwroot/images/leagues next to the binary (same layout
/// as VardyParty.Linux). <see cref="LeagueLogoMapper"/> returns web-style
/// "/images/leagues/x.svg" paths; this maps them onto disk.
/// </summary>
public sealed class LinuxHomeAssetLocator : IHomeAssetLocator
{
    public Task<string?> ResolveLeagueLogoPathAsync(Game game)
    {
        var webPath = LeagueLogoMapper.GetLogoForLeague(game);
        if (string.IsNullOrWhiteSpace(webPath)) return Task.FromResult<string?>(null);

        var relative = webPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var absolute = Path.Combine(AppContext.BaseDirectory, "wwwroot", relative);
        return Task.FromResult(File.Exists(absolute) ? absolute : null);
    }
}
