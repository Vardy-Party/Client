#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
        private Stream? _stream;
        private Uri? _uri;
        private long _bytesRemaining = C.LengthUnset;

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

                _response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead);
                if (!_response.IsSuccessStatusCode && _response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    var code = (int)_response.StatusCode;
                    _response.Dispose();
                    _response = null;
                    throw new IOException($"HTTP {code}");
                }

                _stream = _response.Content.ReadAsStream();
                var contentLength = _response.Content.Headers.ContentLength;
                _bytesRemaining = contentLength ?? C.LengthUnset;
                return _bytesRemaining;
            }
            catch (Exception ex) when (ex is not IOException)
            {
                throw new IOException(ex.ToString());
            }
        }

        public int Read(byte[]? buffer, int offset, int length)
        {
            if (buffer is null || _stream is null)
                return C.ResultEndOfInput;

            if (_bytesRemaining == 0)
                return C.ResultEndOfInput;

            try
            {
                var toRead = length;
                if (_bytesRemaining > 0)
                    toRead = (int)System.Math.Min(length, _bytesRemaining);

                var read = _stream.Read(buffer, offset, toRead);
                if (read <= 0)
                    return C.ResultEndOfInput;

                if (_bytesRemaining > 0)
                    _bytesRemaining -= read;

                return read;
            }
            catch (Exception ex)
            {
                throw new IOException(ex.ToString());
            }
        }

        public Uri? Uri => _uri;

        public IDictionary<string, IList<string>>? ResponseHeaders => null;

        public void Close()
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _response?.Dispose(); } catch { /* ignore */ }
            _stream = null;
            _response = null;
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
