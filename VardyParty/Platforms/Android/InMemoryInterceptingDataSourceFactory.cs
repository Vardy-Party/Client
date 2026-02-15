#if ANDROID
using System.Collections.Generic;
using AndroidX.Media3.DataSource;
using Android.Net;
using System.IO;

namespace VardyParty.Platforms.Android
{
    // Wraps an inner IDataSourceFactory and serves specific URIs from an in-memory byte array map.
    public class InMemoryInterceptingDataSourceFactory : Java.Lang.Object, IDataSourceFactory
    {
        private readonly IDataSourceFactory _innerFactory;
        private readonly IDictionary<string, ManifestCacheEntry> _inMemoryMap;
        private readonly int _maxEntries;
        private readonly TimeSpan _maxAge;

        public InMemoryInterceptingDataSourceFactory(IDataSourceFactory innerFactory, IDictionary<string, ManifestCacheEntry> inMemoryMap, int maxEntries = 10, TimeSpan? maxAge = null)
        {
            _innerFactory = innerFactory;
            _inMemoryMap = inMemoryMap ?? new Dictionary<string, ManifestCacheEntry>();
            _maxEntries = maxEntries;
            _maxAge = maxAge ?? TimeSpan.FromSeconds(60);
        }

        public IDataSource CreateDataSource()
        {
            var inner = _innerFactory.CreateDataSource();
            return new InMemoryInterceptingDataSource(inner, _inMemoryMap, this);
        }

        internal void EnsureSizeLimitAndExpiry()
        {
            try
            {
                // Remove expired entries first
                var now = DateTimeOffset.UtcNow;
                var keysToRemove = new List<string>();
                foreach (var kv in _inMemoryMap)
                {
                    if (now - kv.Value.Added > _maxAge) keysToRemove.Add(kv.Key);
                }
                foreach (var k in keysToRemove) _inMemoryMap.Remove(k);

                // Enforce max entries by removing oldest
                while (_inMemoryMap.Count > _maxEntries)
                {
                    string? oldestKey = null;
                    DateTimeOffset oldest = DateTimeOffset.MaxValue;
                foreach (var kv in _inMemoryMap)
                {
                    if (kv.Value.Added < oldest)
                    {
                        oldest = kv.Value.Added;
                        oldestKey = kv.Key;
                    }
                }
                    if (oldestKey != null) _inMemoryMap.Remove(oldestKey);
                    else break;
                }
            }
            catch { }
        }
        // Track seen requests globally to detect first-segment requests
        private static System.Collections.Concurrent.ConcurrentDictionary<string, bool> _seenRequests = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();

        internal bool IsFirstRequest(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return _seenRequests.TryAdd(url, true);
        }
    }

    class InMemoryInterceptingDataSource : Java.Lang.Object, IDataSource
    {
        private readonly IDataSource _inner;
        private readonly IDictionary<string, ManifestCacheEntry> _map;
        private readonly InMemoryInterceptingDataSourceFactory _owner;
        private MemoryStream? _stream;
        private global::Android.Net.Uri? _currentUri;

        public InMemoryInterceptingDataSource(IDataSource inner, IDictionary<string, ManifestCacheEntry> map, InMemoryInterceptingDataSourceFactory owner)
        {
            _inner = inner;
            _map = map;
            _owner = owner;
        }

        public long Open(DataSpec dataSpec)
        {
            try
            {
                var key = dataSpec.Uri.ToString();
                if (!string.IsNullOrEmpty(key) && _map.TryGetValue(key, out var entry))
                {
                    _currentUri = dataSpec.Uri;
                    _stream = new MemoryStream(entry.Data);
                    // Update access time
                    try { entry.Added = DateTimeOffset.UtcNow; } catch { }
                    return _stream.Length;
                }
            }
            catch { }
            // Log first-time requests for diagnostics and fallback hits
            try
            {
                var url = dataSpec?.Uri?.ToString() ?? string.Empty;
                if (_owner.IsFirstRequest(url))
                {
                    try { global::Android.Util.Log.Info("VardyParty", $"[InMemoryDataSource] First request for {url}"); } catch { }
                }
                // Also log when a fallback manifest exists for this URL
                if (!string.IsNullOrEmpty(url) && _map.ContainsKey(url))
                {
                    try { global::Android.Util.Log.Info("VardyParty", $"[InMemoryDataSource] Serving in-memory fallback for {url}"); } catch { }
                }
            }
            catch { }
            return _inner.Open(dataSpec);
        }

        public int Read(byte[] buffer, int offset, int readLength)
        {
            try
            {
                if (_stream != null)
                {
                    int r = _stream.Read(buffer, offset, readLength);
                    return r == 0 ? -1 : r;
                }
            }
            catch { }
            return _inner.Read(buffer, offset, readLength);
        }

        public global::Android.Net.Uri Uri => _currentUri ?? _inner.Uri;

        public void Close()
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
            _currentUri = null;
            try { _inner.Close(); } catch { }
            // Owner may enforce size limits and expiry
            try { _owner?.EnsureSizeLimitAndExpiry(); } catch { }
        }

        public void AddTransferListener(AndroidX.Media3.DataSource.ITransferListener listener)
        {
            try { _inner.AddTransferListener(listener); } catch { }
        }
    }
}
#endif
