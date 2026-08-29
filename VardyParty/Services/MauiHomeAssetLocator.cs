using Microsoft.Maui.Storage;
using VardyParty.Catalog;
using VardyParty.HomeUi;
using VardyParty.Kernel;

namespace VardyParty.MauiServices;

/// <summary>
/// League logos ship as MauiAssets (LogicalName images/leagues/*). MAUI app
/// packages (Android assets, MSIX) are not plain directories, so the file is
/// copied once into the cache dir and the on-disk path handed to the shared
/// <see cref="IBadgeImageLoader"/>. Reached on the UI thread at first render
/// (HomeViewModel.Apply -> LoadImagesAsync before its first await), which is
/// why the package extraction is genuinely async — a blocking
/// GetAwaiter().GetResult() here stalled the WinUI dispatcher during startup.
/// </summary>
public sealed class MauiHomeAssetLocator : IHomeAssetLocator
{
    public async Task<string?> ResolveLeagueLogoPathAsync(Game game)
    {
        var webPath = LeagueLogoMapper.GetLogoForLeague(game);
        if (string.IsNullOrWhiteSpace(webPath)) return null;

        var logical = webPath.TrimStart('/');
        var cached = Path.Combine(
            FileSystem.CacheDirectory,
            // Bump when packaged league assets change content (e.g. PL fill).
            // Exists-only cache otherwise keeps the first purple SVG forever.
            "home-assets-v3",
            logical.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(cached)) return cached;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            using var source = await FileSystem.OpenAppPackageFileAsync(logical);
            using var target = File.Create(cached);
            await source.CopyToAsync(target);
            return cached;
        }
        catch
        {
            // Missing asset just means no league icon for this row.
            return null;
        }
    }
}
