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
}
