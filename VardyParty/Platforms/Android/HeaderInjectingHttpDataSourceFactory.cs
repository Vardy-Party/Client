#if ANDROID
using System.Collections.Generic;
using AndroidX.Media3.DataSource;

namespace VardyParty.Platforms.Android
{
    // DataSource.Factory implementation that injects headers into each DefaultHttpDataSource instance
    public class HeaderInjectingDataSourceFactory : Java.Lang.Object, AndroidX.Media3.DataSource.IDataSourceFactory
    {
        private readonly DefaultHttpDataSource.Factory _innerFactory;
        private readonly IDictionary<string, string?> _headers;

        public HeaderInjectingDataSourceFactory(IDictionary<string, string?> headers)
        {
            _headers = headers ?? new Dictionary<string, string?>();
            _innerFactory = new DefaultHttpDataSource.Factory();
            try
            {
                if (_headers.TryGetValue("User-Agent", out var ua) && !string.IsNullOrEmpty(ua))
                {
                    _innerFactory.SetUserAgent(ua);
                }
            }
            catch { }
        }

        public AndroidX.Media3.DataSource.IDataSource CreateDataSource()
        {
            var ds = _innerFactory.CreateDataSource();
            try
            {
                if (ds is DefaultHttpDataSource dh)
                {
                    foreach (var kv in _headers)
                    {
                        try { dh.SetRequestProperty(kv.Key, kv.Value ?? string.Empty); } catch { }
                    }
                }
            }
            catch { }
            return ds;
        }
    }
}
#endif
