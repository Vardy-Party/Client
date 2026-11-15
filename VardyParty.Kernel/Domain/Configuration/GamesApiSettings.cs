namespace VardyParty.Kernel;

public class GamesApiSettings
{
    public static string SectionName => "GamesApi";
    public required int CallTimeoutSeconds { get; set; }
    public required int MaxRetries { get; set; }
    public required int RefreshSchedule { get; set; } // RefreshSchedule for games cache, in seconds
    public required int M3U8CallTimeoutSeconds { get; set; }

    /// <summary>
    /// Rule 7 (Jon, 13 Nov 2026): a playlist-only MP chip handed over early (no backup yet)
    /// that fails its health check is checked once more this many seconds later before it is
    /// marked dead. Optional; defaults to <see cref="DefaultEarlyChipRetryDelaySeconds"/>.
    /// </summary>
    public int EarlyChipRetryDelaySeconds { get; set; } = DefaultEarlyChipRetryDelaySeconds;

    /// <summary>Default for <see cref="EarlyChipRetryDelaySeconds"/>: 10s (Jon: "30s is too long").</summary>
    public const int DefaultEarlyChipRetryDelaySeconds = 10;

    /// <summary>
    /// Mid-play playlist reload recovery. Default is option 1+2,
    /// <c>RetrySameUrlThenSwitchBack</c>: retry the same URL in the fetch layer,
    /// then switch to keep video going and auto-return when the user-chosen
    /// stream recovers. Set <c>RetrySameUrl</c> for option 1 only. Unknown names
    /// still use option 1 in the fetch layer and keep option 2 enabled.
    /// </summary>
    public string PlaylistReloadStrategy { get; set; } = "RetrySameUrlThenSwitchBack";

    /// <summary>
    /// Words kept upper-case when an ALL-CAPS league key is title-cased for a Home row
    /// heading (display only). Configurable under <c>GamesApi:LeagueAcronyms</c>; a
    /// configured list replaces <see cref="DefaultLeagueAcronyms"/>. Empty (the default)
    /// uses <see cref="DefaultLeagueAcronyms"/>. Kept empty here because the configuration
    /// binder appends to a pre-filled array instead of replacing it.
    /// </summary>
    public string[] LeagueAcronyms { get; set; } = [];

    /// <summary>Default for <see cref="LeagueAcronyms"/>.</summary>
    public static IReadOnlyList<string> DefaultLeagueAcronyms { get; } =
    [
        "UEFA", "CAF", "AFC", "FIFA", "CONCACAF", "CONMEBOL",
        "MLS", "USL", "NWSL", "WSL", "SPL", "EFL", "NBA", "NFL",
    ];
}
