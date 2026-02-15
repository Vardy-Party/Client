#if ANDROID
using System;

namespace VardyParty.Platforms.Android
{
    public class ManifestCacheEntry
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
        public long Size => Data?.LongLength ?? 0;
    }
}
#endif
