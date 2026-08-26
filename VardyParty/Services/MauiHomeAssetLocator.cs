using Microsoft.Maui.Storage;
using VardyParty.Catalog;
using VardyParty.HomeUi;
using VardyParty.Kernel;

namespace VardyParty.MauiServices;

/// <summary>
/// League logos ship as MauiAssets (LogicalName images/leagues/*). MAUI app
/// packages (Android assets, MSIX) are not plain directories, so the file is
/// copied once into the cache dir and the on-disk path handed to the shared
/// <see cref="IBadgeImageLoader"/>. Called from the badge loader's background
/// task, never the UI thread.
/// </summary>
public sealed class MauiHomeAssetLocator : IHomeAssetLocator
{
    public string? ResolveLeagueLogoPath(Game game)
    {
        var webPath = LeagueLogoMapper.GetLogoForLeague(game);
        if (string.IsNullOrWhiteSpace(webPath)) return null;

        var logical = webPath.TrimStart('/');
        var cached = Path.Combine(
            FileSystem.CacheDirectory,
            "home-assets",
            logical.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(cached)) return cached;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            using var source = FileSystem.OpenAppPackageFileAsync(logical).GetAwaiter().GetResult();
            using var target = File.Create(cached);
            source.CopyTo(target);
            return cached;
        }
        catch
        {
            // Missing asset just means no league icon for this row.
            return null;
        }
    }
}
