#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using Java.IO;
using VardyParty.Hosting;
using Exception = System.Exception;
using IOException = Java.IO.IOException;
using Uri = Android.Net.Uri;

namespace VardyParty.Platforms.Android
{
    /// <summary>
    /// Media3 <see cref="IDataSource"/> that fetches via managed <see cref="HttpClient"/>
    /// (DualStack + optional Cloudflare DoH) instead of Android system DNS.
    /// </summary>
    public sealed class ManagedHttpDataSource : Java.Lang.Object, IDataSource
    {
        private readonly HttpClient _http;
        private readonly IDictionary<string, string?> _headers;
        private HttpResponseMessage? _response;
        private IDictionary<string, IList<string>>? _javaResponseHeaders;
        private Stream? _stream;
        private Uri? _uri;
        private long _bytesRemaining = C.LengthUnset;
        private byte[] _scratch = new byte[16 * 1024];

        public ManagedHttpDataSource(HttpClient http, IDictionary<string, string?> headers)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _headers = headers ?? new Dictionary<string, string?>();
        }

        public void AddTransferListener(ITransferListener? transferListener)
        {
        }

        public long Open(DataSpec? dataSpec)
        {
            if (dataSpec?.Uri is null)
                throw new IOException("DataSpec URI is null");

            try
            {
                _uri = dataSpec.Uri;
                _javaResponseHeaders = null;
                var request = new HttpRequestMessage(HttpMethod.Get, _uri.ToString());
                foreach (var kv in _headers)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value ?? string.Empty);
                }

                if (dataSpec.Position > 0 || (dataSpec.Length > 0 && dataSpec.Length != C.LengthUnset))
                {
                    var end = dataSpec.Length > 0 && dataSpec.Length != C.LengthUnset
                        ? dataSpec.Position + dataSpec.Length - 1
                        : (long?)null;
                    var range = end is null
                        ? $"bytes={dataSpec.Position}-"
                        : $"bytes={dataSpec.Position}-{end}";
                    request.Headers.TryAddWithoutValidation("Range", range);
                }

                // Media3 Open is sync; Send is CA1416-unsupported on Android.
                _response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter()
                    .GetResult();
                if (!_response.IsSuccessStatusCode && _response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    var code = (int)_response.StatusCode;
                    _response.Dispose();
                    _response = null;
                    _javaResponseHeaders = null;
                    throw new IOException($"HTTP {code}");
                }

                _stream = _response.Content.ReadAsStream();
                if (dataSpec.Position > 0 && _response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    SkipFully(_stream, dataSpec.Position);

                // LENGTH_UNSET: Content-Length is the compressed size when
                // AutomaticDecompression is on, and Media3 then over-reads.
                _bytesRemaining = C.LengthUnset;
                _javaResponseHeaders = ToJavaResponseHeaders(_response);
                return C.LengthUnset;
            }
            catch (Exception ex) when (ex is not IOException)
            {
                throw new IOException(ex.ToString());
            }
        }

        public int Read(byte[]? buffer, int offset, int length)
        {
            if (length == 0)
                return 0;
            if (buffer is null || _stream is null)
                return C.ResultEndOfInput;
            if (_bytesRemaining == 0)
                return C.ResultEndOfInput;

            try
            {
                // JNI sometimes copies a Java slice but still passes the Java
                // offset. Stream.Read then throws ArgumentException, which we
                // used to wrap as Java.IO.IOException — that raises
                // UnhandledExceptionRaiser and MainApplication reloads the app.
                if ((uint)offset > (uint)buffer.Length)
                    offset = 0;
                var max = buffer.Length - offset;
                if (max <= 0)
                    return C.ResultEndOfInput;

                var toRead = System.Math.Min(length, max);
                if (_bytesRemaining > 0)
                    toRead = (int)System.Math.Min(toRead, _bytesRemaining);
                if (toRead <= 0)
                    return C.ResultEndOfInput;

                if (_scratch.Length < toRead)
                    _scratch = new byte[toRead];

                var read = _stream.Read(_scratch, 0, toRead);
                if (read <= 0)
                    return C.ResultEndOfInput;

                System.Array.Copy(_scratch, 0, buffer, offset, read);
                if (_bytesRemaining > 0)
                    _bytesRemaining -= read;
                return read;
            }
            catch (ObjectDisposedException)
            {
                return C.ResultEndOfInput;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("VardyParty", $"ManagedHttpDataSource.Read: {ex.GetType().Name}: {ex.Message}");
                return C.ResultEndOfInput;
            }
        }

        public Uri? Uri => _uri;

        /// <summary>
        /// Media3 HLS calls <c>FileTypes.inferFileTypeFromResponseHeaders</c> on this
        /// map — returning null NPEs (MP segments often omit Content-Type and use
        /// decoy extensions like .class/.ppt). Always return a non-null map.
        /// Values must be real <c>java.util.List</c> instances: a C# <c>List&lt;string&gt;</c>
        /// marshals as <c>mono.android.runtime.JavaObject</c> and Media3 then
        /// ClassCastException-crashes in <c>FileTypes.java</c>.
        /// </summary>
        public IDictionary<string, IList<string>> ResponseHeaders =>
            _javaResponseHeaders ?? new JavaDictionary<string, IList<string>>();

        public void Close()
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _response?.Dispose(); } catch { /* ignore */ }
            _stream = null;
            _response = null;
        }

        private static IDictionary<string, IList<string>> ToJavaResponseHeaders(HttpResponseMessage response)
        {
            var map = new JavaDictionary<string, IList<string>>();
            foreach (var header in response.Headers)
                map[header.Key] = ToJavaList(header.Value);
            foreach (var header in response.Content.Headers)
                map[header.Key] = ToJavaList(header.Value);
            return map;
        }

        private static IList<string> ToJavaList(IEnumerable<string> values)
        {
            var list = new JavaList<string>();
            foreach (var value in values)
                list.Add(value);
            return list;
        }

        private static void SkipFully(Stream stream, long count)
        {
            var remaining = count;
            var skip = new byte[4096];
            while (remaining > 0)
            {
                var n = stream.Read(skip, 0, (int)System.Math.Min(skip.Length, remaining));
                if (n <= 0)
                    break;
                remaining -= n;
            }
        }
    }

    /// <summary>Factory that builds <see cref="ManagedHttpDataSource"/> instances.</summary>
    public sealed class ManagedHttpDataSourceFactory : Java.Lang.Object, IDataSourceFactory
    {
        private readonly HttpClient _http;
        private readonly IDictionary<string, string?> _headers;

        public ManagedHttpDataSourceFactory(HttpClient http, IDictionary<string, string?> headers)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _headers = headers ?? new Dictionary<string, string?>();
        }

        public IDataSource CreateDataSource() => new ManagedHttpDataSource(_http, _headers);

        /// <summary>
        /// Prefer managed DoH-aware HTTP when the fallback preference is on;
        /// otherwise the existing header-injecting Android data source.
        /// </summary>
        public static IDataSourceFactory CreateForPlayback(
            IDictionary<string, string?> headers,
            bool dnsOverHttpsFallbackEnabled)
        {
            if (!dnsOverHttpsFallbackEnabled)
                return new HeaderInjectingDataSourceFactory(headers);

            var services = VardyParty.AppServiceProvider.ServiceProvider;
            var factory = services?.GetService(typeof(IHttpClientFactory)) as IHttpClientFactory;
            if (factory is null)
                return new HeaderInjectingDataSourceFactory(headers);

            var http = factory.CreateClient(PlaybackHttpClients.Media);
            return new ManagedHttpDataSourceFactory(http, headers);
        }
    }
}
#endif
