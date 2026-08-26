using VardyParty.Kernel;

namespace VardyParty.HomeUi;

/// <summary>Resolves packaged asset paths (league logos) for the current host.</summary>
public interface IHomeAssetLocator
{
    /// <summary>Absolute path of the league logo for a game, or null when none ships.</summary>
    string? ResolveLeagueLogoPath(Game game);
}
