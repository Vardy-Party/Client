#if ANDROID
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using VardyParty.Playback;
using VardyParty.Presentation;
using VardyParty.Streaming;
using Exception = System.Exception;
using Uri = Android.Net.Uri;

namespace VardyParty.Platforms.Android
{
    /// <summary>
    /// Media3 <see cref="IDataSource"/> decorator: TikTok-CDN HLS segments that are
    /// MPEG-TS wrapped in a PNG (<c>TIKTIKPX</c>, <see cref="PngWrappedTs"/>) or a WebP EXIF chunk (<see cref="WebpWrappedTs"/>) are
    /// buffered, unwrapped and served as <c>video/mp2t</c>. Every other body
    /// (playlists, keys, plain TS / fMP4, real images) streams through untouched
    /// after a 12-byte peek. Ranged opens (Position &gt; 0 / explicit Length) pass
    /// through: a slice of a wrapper cannot be unwrapped.
    /// Mid-play playlist reloads go through <see cref="IPlaylistReloadRecoveryStrategy"/>
    /// so a transitory 5xx or non-#EXTM3U body is retried before the player sees it.
    /// </summary>
    public sealed class UnwrappingDataSource : Java.Lang.Object, IDataSource
    {
        private readonly IDataSource _upstream;
        private readonly PlaybackTransferMeter? _meter;
        private readonly PlaylistReloadSession? _playlistSession;
        private byte[] _pending = Array.Empty<byte>();
        private int _pendingOffset;
        private bool _upstreamDone;
        private IDictionary<string, IList<string>>? _unwrappedHeaders;
        private readonly Action<int>? _onOpenOutcome;

        /// <param name="onOpenOutcome">Called once per open: 0 when the server answered,
        /// else the HTTP status of the failure (0 when unknown). Feeds the refused-download rule.</param>
        public UnwrappingDataSource(
            IDataSource upstream,
            PlaybackTransferMeter? meter = null,
            Action<int>? onOpenOutcome = null,
            PlaylistReloadSession? playlistSession = null)
        {
            _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
            _meter = meter;
            _onOpenOutcome = onOpenOutcome;
            _playlistSession = playlistSession;
        }

        public void AddTransferListener(ITransferListener? transferListener)
        {
            if (transferListener is not null)
                _upstream.AddTransferListener(transferListener);
        }

        public long Open(DataSpec? dataSpec)
        {
            _pending = Array.Empty<byte>();
            _pendingOffset = 0;
            _upstreamDone = false;
            _unwrappedHeaders = null;

            if (dataSpec is not null)
                dataSpec = PlayingPlaylistRedirect.Apply(dataSpec);

            if (dataSpec is not null
                && IsPlaylist(dataSpec)
                && dataSpec.Position <= 0
                && (dataSpec.Length <= 0 || dataSpec.Length == C.LengthUnset))
            {
                return OpenPlaylistWithRecovery(dataSpec);
            }

            // Upstream IOExceptions (HTTP 403, DNS) must reach Media3 unchanged.
            long length;
            try
            {
                length = _upstream.Open(dataSpec);
            }
            catch (Exception ex)
            {
                ReportOpenOutcome(PlaybackPolicy.HttpStatusFromErrorText(ex.Message), failed: true);
                throw;
            }

            ReportOpenOutcome(0, failed: false);
            if (dataSpec is null
                || dataSpec.Position > 0
                || (dataSpec.Length > 0 && dataSpec.Length != C.LengthUnset))
            {
                return length;
            }

            PlayedSegmentMemory.Note(dataSpec.Uri?.ToString());

            WrappedSegmentRead read;
            try
            {
                read = WrappedSegmentReader.Read((buffer, offset, count) =>
                {
                    var n = _upstream.Read(buffer, offset, count);
                    return n == C.ResultEndOfInput ? 0 : n;
                });
            }
            catch (Exception ex)
            {
                throw new Java.IO.IOException(ex.ToString());
            }

            _pending = read.Body;
            _upstreamDone = read.UpstreamExhausted;
            if (!read.Unwrapped)
            {
                // Peeked bytes are replayed; length (if known) is unchanged.
                return length;
            }

            _unwrappedHeaders = Mp2tHeaders(_upstream.ResponseHeaders);
            global::Android.Util.Log.Info(
                "VardyParty",
                $"UnwrappingDataSource: image-wrapped TS unwrapped ({_pending.Length} bytes) {_upstream.Uri}");
            return _pending.Length;
        }

        private long OpenPlaylistWithRecovery(DataSpec dataSpec)
        {
            var session = _playlistSession;
            var strategy = session?.Strategy ?? PlaylistReloadStrategies.Default;
            var request = new PlaylistReloadRequest
            {
                IsFirstAttach = session is null || !session.FirstPlaylistCompleted,
                // Never re-serve a stale playlist: that re-shows already-watched
                // segments. When the hold budget is spent, give up and switch.
                LastGoodPlaylist = null,
                MaxHold = PlaybackPolicy.AndroidPlaylistHoldBudget,
                Row = session?.Row,
                Session = session,
                Log = (isInfo, line) =>
                {
                    if (isInfo)
                        global::Android.Util.Log.Info("VardyParty", line);
                    else
                        global::Android.Util.Log.Debug("VardyParty", line);
                }
            };

            PlaylistReloadOutcome outcome;
            try
            {
                outcome = PlaylistReloadCoordinator.Run(
                    strategy,
                    request,
                    _ => Task.FromResult(FetchPlaylistOnce(dataSpec)),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                ReportOpenOutcome(PlaybackPolicy.HttpStatusFromErrorText(ex.Message), failed: true);
                throw;
            }

            if (outcome.Kind == PlaylistReloadOutcomeKind.Failed
                || outcome.Kind == PlaylistReloadOutcomeKind.ServeLastGood)
            {
                ReportOpenOutcome(outcome.StatusCode, failed: true);
                var message = outcome.Kind == PlaylistReloadOutcomeKind.ServeLastGood
                    ? PlaylistReloadEvaluation.GiveUpMessage(
                        PlaybackErrorKind.CdnUnreachable,
                        outcome.StatusCode,
                        "stale playlist not served")
                    : (outcome.Message ?? "playlist reload failed");
                session?.NoteGiveUp(PlaybackErrorKind.CdnUnreachable, message);
                throw new Java.IO.IOException(message);
            }

            var served = outcome.Body ?? Array.Empty<byte>();
            if (outcome.Kind == PlaylistReloadOutcomeKind.Serve)
                session?.NoteServed(served);

            var bytes = TrimPlayedSegments(dataSpec, served);
            ReportOpenOutcome(0, failed: false);
            _pending = bytes;
            _upstreamDone = true;
            return bytes.Length;
        }

        private PlaylistReloadFetchResult FetchPlaylistOnce(DataSpec dataSpec)
        {
            try { _upstream.Close(); } catch { /* reopen */ }

            try
            {
                _upstream.Open(dataSpec);
                var body = ReadUpstreamBody();
                var contentType = ContentTypeOf(_upstream);
                try { _upstream.Close(); } catch { /* next attempt reopens */ }
                return PlaylistReloadFetchResult.FromHttp(200, body, contentType);
            }
            catch (Exception ex)
            {
                try { _upstream.Close(); } catch { /* next attempt reopens */ }
                if (_upstream is ManagedHttpDataSource managed && managed.LastOpenStatus is >= 100 and <= 599)
                {
                    return PlaylistReloadFetchResult.FromHttp(
                        managed.LastOpenStatus,
                        managed.LastErrorBody,
                        managed.LastErrorContentType);
                }

                var status = PlaybackPolicy.HttpStatusFromException(ex);
                return PlaylistReloadFetchResult.FromHttp(status, [], null);
            }
        }

        private byte[] ReadUpstreamBody()
        {
            const int maxBytes = 1024 * 1024;
            var buffer = new byte[8192];
            using var body = new System.IO.MemoryStream();
            while (body.Length < maxBytes)
            {
                var n = _upstream.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - body.Length));
                if (n == C.ResultEndOfInput || n <= 0)
                    break;
                body.Write(buffer, 0, n);
            }

            return body.ToArray();
        }

        private static byte[] TrimPlayedSegments(DataSpec dataSpec, byte[] bytes)
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (!text.AsSpan().TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
                return bytes;

            var playlistUrl = dataSpec.Uri?.ToString();
            PlayedSegmentMemory.NotePlaylist(playlistUrl);
            var rewritten = PlayingPlaylistRedirect.LastApplyRewrote;
            var stallPrepare = PlayedSegmentMemory.ConsumeTrim();
            var dropPlayed = rewritten || stallPrepare;
            var trimmed = dropPlayed
                ? UnplayedPlaylist.TrimPlayedPrefix(text, PlayedSegmentMemory.WasPlayed, playlistUrl)
                : new UnplayedPlaylist.Trim(text, 0);
            if (trimmed.Dropped <= 0)
                return bytes;

            global::Android.Util.Log.Info(
                "VardyParty",
                $"Playlist reload dropped {trimmed.Dropped} played segments");
            return Encoding.UTF8.GetBytes(trimmed.Text);
        }

        private static string? ContentTypeOf(IDataSource source)
        {
            var headers = source.ResponseHeaders;
            if (headers is null)
                return null;
            foreach (var pair in headers)
            {
                if (pair.Key is not null
                    && pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                    && pair.Value is { Count: > 0 })
                {
                    return pair.Value[0];
                }
            }

            return null;
        }

        private void ReportOpenOutcome(int status, bool failed)
        {
            if (_onOpenOutcome is null || (failed && status == 0))
                return;
            try { _onOpenOutcome(status); }
            catch { /* reporting must never break a load */ }
        }

        private static bool IsPlaylist(DataSpec dataSpec)
        {
            var path = dataSpec.Uri?.Path;
            return path is not null && path.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        public int Read(byte[]? buffer, int offset, int length)
        {
            if (length == 0)
                return 0;
            if (buffer is null)
                return C.ResultEndOfInput;

            if (_pendingOffset < _pending.Length)
            {
                // Same JNI offset guard as ManagedHttpDataSource.Read.
                if ((uint)offset > (uint)buffer.Length)
                    offset = 0;
                var n = Math.Min(Math.Min(length, buffer.Length - offset), _pending.Length - _pendingOffset);
                if (n <= 0)
                    return C.ResultEndOfInput;
                Array.Copy(_pending, _pendingOffset, buffer, offset, n);
                _pendingOffset += n;
                NoteBytes(n);
                return n;
            }

            if (_upstreamDone || _unwrappedHeaders is not null)
                return C.ResultEndOfInput;

            var read = _upstream.Read(buffer, offset, length);
            if (read > 0)
                NoteBytes(read);
            return read;
        }

        private void NoteBytes(int count) => _meter?.Add(count, Environment.TickCount64);

        public Uri? Uri => _upstream.Uri;

        public IDictionary<string, IList<string>> ResponseHeaders =>
            _unwrappedHeaders ?? _upstream.ResponseHeaders ?? new JavaDictionary<string, IList<string>>();

        public void Close()
        {
            _pending = Array.Empty<byte>();
            _pendingOffset = 0;
            _upstream.Close();
        }

        /// <summary>Real java.util.Map/List (see ManagedHttpDataSource.ResponseHeaders).</summary>
        private static IDictionary<string, IList<string>> Mp2tHeaders(IDictionary<string, IList<string>>? upstream)
        {
            var map = new JavaDictionary<string, IList<string>>();
            if (upstream is not null)
            {
                foreach (var pair in upstream)
                {
                    if (pair.Key is null
                        || pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var values = new JavaList<string>();
                    if (pair.Value is not null)
                    {
                        foreach (var value in pair.Value)
                            values.Add(value);
                    }

                    map[pair.Key] = values;
                }
            }

            map["Content-Type"] = new JavaList<string> { "video/mp2t" };
            return map;
        }
    }

    /// <summary>Wraps any Media3 data source factory with <see cref="UnwrappingDataSource"/>.</summary>
    public sealed class UnwrappingDataSourceFactory : Java.Lang.Object, IDataSourceFactory
    {
        private readonly IDataSourceFactory _inner;
        private readonly PlaybackTransferMeter? _meter;
        private readonly Action<int>? _onOpenOutcome;
        private readonly PlaylistReloadSession _playlistSession;

        public UnwrappingDataSourceFactory(
            IDataSourceFactory inner,
            PlaybackTransferMeter? meter = null,
            Action<int>? onOpenOutcome = null,
            PlaylistReloadSession? playlistSession = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _meter = meter;
            _onOpenOutcome = onOpenOutcome;
            _playlistSession = playlistSession ?? new PlaylistReloadSession();
        }

        public IDataSource CreateDataSource()
        {
            var upstream = _inner.CreateDataSource()
                ?? throw new InvalidOperationException("Upstream data source factory returned null.");
            return new UnwrappingDataSource(upstream, _meter, _onOpenOutcome, _playlistSession);
        }
    }
}
#endif
