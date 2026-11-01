#if ANDROID
using System;
using AndroidX.Media3.DataSource;
using VardyParty.Playback;
using Uri = Android.Net.Uri;

namespace VardyParty.Platforms.Android
{
    /// <summary>
    /// Process-wide target for the one playing playlist. The data source reads it on each open.
    /// Cleared when the visible player is given a new media source.
    /// </summary>
    internal static class PlayingPlaylistRedirect
    {
        private static readonly object Gate = new();
        private static string? _playingUrl;
        private static string? _nextUrl;

        /// <summary>True when the most recent <see cref="Apply"/> changed the request URL.</summary>
        public static bool LastApplyRewrote { get; private set; }

        public static void Arm(string playingUrl, string nextUrl)
        {
            lock (Gate)
            {
                _playingUrl = playingUrl;
                _nextUrl = nextUrl;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                _playingUrl = null;
                _nextUrl = null;
            }
        }

        public static DataSpec Apply(DataSpec dataSpec)
        {
            LastApplyRewrote = false;
            var request = dataSpec.Uri?.ToString();
            string? playing;
            string? next;
            lock (Gate)
            {
                playing = _playingUrl;
                next = _nextUrl;
            }

            var rewritten = PlaylistReloadRedirect.Rewrite(request, playing, next);
            if (rewritten is null || dataSpec.Uri is null)
                return dataSpec;

            var uri = Uri.Parse(rewritten);
            if (uri is null)
                return dataSpec;

            var built = dataSpec.BuildUpon()?.SetUri(uri)?.Build();
            if (built is null)
                return dataSpec;
            LastApplyRewrote = true;
            return built;
        }
    }
}
#endif
