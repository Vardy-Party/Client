#if ANDROID
using System;
using System.Collections.Generic;
using VardyParty.Playback;

namespace VardyParty.Platforms.Android
{
    /// <summary>
    /// Segment URLs already fetched for the playlist currently playing.
    /// A different playlist path clears the set.
    /// </summary>
    internal static class PlayedSegmentMemory
    {
        private const int MaxIds = 500;
        private static readonly object Gate = new();
        private static readonly Queue<string> Order = new();
        private static readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase);
        private static string? _playlistIdentity;
        private static int _trimOnce;

        /// <summary>The next playlist body should drop segments already fetched. A stall prepare sets this.</summary>
        public static void ArmTrimOnce() => System.Threading.Interlocked.Exchange(ref _trimOnce, 1);

        public static bool ConsumeTrim() => System.Threading.Interlocked.Exchange(ref _trimOnce, 0) == 1;

        public static void NotePlaylist(string? playlistUrl)
        {
            var identity = UnplayedPlaylist.SegmentIdentity(playlistUrl);
            lock (Gate)
            {
                if (string.Equals(identity, _playlistIdentity, StringComparison.OrdinalIgnoreCase))
                    return;
                _playlistIdentity = identity;
                Order.Clear();
                Ids.Clear();
            }
        }

        public static void Note(string? url)
        {
            var identity = UnplayedPlaylist.SegmentIdentity(url);
            if (identity.Length == 0
                || identity.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (Gate)
            {
                if (!Ids.Add(identity))
                    return;
                Order.Enqueue(identity);
                while (Order.Count > MaxIds && Order.TryDequeue(out var expired))
                    Ids.Remove(expired);
            }
        }

        public static bool WasPlayed(string url)
        {
            var identity = UnplayedPlaylist.SegmentIdentity(url);
            lock (Gate)
                return identity.Length > 0 && Ids.Contains(identity);
        }
    }
}
#endif
