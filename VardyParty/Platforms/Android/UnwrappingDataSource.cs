#if ANDROID
using System;
using System.Collections.Generic;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
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
    /// </summary>
    public sealed class UnwrappingDataSource : Java.Lang.Object, IDataSource
    {
        private readonly IDataSource _upstream;
        private byte[] _pending = Array.Empty<byte>();
        private int _pendingOffset;
        private bool _upstreamDone;
        private IDictionary<string, IList<string>>? _unwrappedHeaders;

        public UnwrappingDataSource(IDataSource upstream)
        {
            _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
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

            // Upstream IOExceptions (HTTP 403, DNS) must reach Media3 unchanged.
            var length = _upstream.Open(dataSpec);
            if (dataSpec is null
                || dataSpec.Position > 0
                || (dataSpec.Length > 0 && dataSpec.Length != C.LengthUnset))
            {
                return length;
            }

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
                return n;
            }

            if (_upstreamDone || _unwrappedHeaders is not null)
                return C.ResultEndOfInput;

            return _upstream.Read(buffer, offset, length);
        }

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

        public UnwrappingDataSourceFactory(IDataSourceFactory inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public IDataSource CreateDataSource()
        {
            var upstream = _inner.CreateDataSource()
                ?? throw new InvalidOperationException("Upstream data source factory returned null.");
            return new UnwrappingDataSource(upstream);
        }
    }
}
#endif