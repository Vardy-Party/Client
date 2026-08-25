namespace VardyParty.Models
{
    public class PlaybackResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool IsRecoverable { get; set; }

        public static PlaybackResult Completed(string message, bool recoverable = false) => new PlaybackResult
        {
            Success = false,
            Message = message,
            IsRecoverable = recoverable
        };

        public static PlaybackResult SuccessResult(string message) => new PlaybackResult
        {
            Success = true,
            Message = message,
            IsRecoverable = true
        };
    }
}
