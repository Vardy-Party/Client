using VardyParty.Kernel;

namespace VardyParty.HomeUi;

/// <summary>Resolves packaged asset paths (league logos) for the current host.</summary>
public interface IHomeAssetLocator
{
    /// <summary>
    /// Absolute path of the league logo for a game, or null when none ships.
    /// Async because implementations may need to extract the asset from the
    /// app package first (and are reached from the UI thread at first render).
    /// </summary>
    Task<string?> ResolveLeagueLogoPathAsync(Game game);
}
