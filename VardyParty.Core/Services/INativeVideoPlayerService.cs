using VardyParty.Models;

namespace VardyParty.Services
{
    public interface INativeVideoPlayerService
    {
        event EventHandler<bool>? BufferingStateChanged;

        Task<PlaybackResult> PlayVideoAsync(
            string m3u8Url, 
            string refererUrl, 
            string title,
            Func<Task>? onNextStreamRequested = null);

        /// <summary>
        /// Get current playback metrics including resolution, framerate, and codec information
        /// </summary>
        PlaybackMetrics? GetCurrentMetrics();
    }
}
