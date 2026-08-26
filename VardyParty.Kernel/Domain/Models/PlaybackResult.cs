namespace VardyParty.Kernel;

public class PlaybackResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsRecoverable { get; set; }

    public static PlaybackResult Completed(string message, bool recoverable = false) => new()
    {
        Success = false,
        Message = message,
        IsRecoverable = recoverable
    };

    public static PlaybackResult SuccessResult(string message) => new()
    {
        Success = true,
        Message = message,
        IsRecoverable = true
    };
}
