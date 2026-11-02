namespace VardyParty.Streaming;

/// <summary>
/// Result of <see cref="WrappedSegmentReader"/>: the bytes the player should
/// receive first. When <see cref="Unwrapped"/> is true, <see cref="Body"/> is the
/// whole MPEG-TS segment and the upstream must not be read any further. Otherwise
/// <see cref="Body"/> holds bytes already consumed from upstream (replay them,
/// then keep reading upstream unless <see cref="UpstreamExhausted"/>).
/// </summary>
public readonly record struct WrappedSegmentRead(byte[] Body, bool Unwrapped, bool UpstreamExhausted);

/// <summary>
/// Streaming front-end for <see cref="PngWrappedTs"/> shared by players that pull
/// segment bytes through our code (Android Media3 DataSource, Linux loopback
/// proxy). Peek <see cref="PeekBytes"/>; only PNG/WebP bodies are buffered (bounded by
/// <see cref="PngWrappedTs.MaxPngBytes"/>), everything else streams through
/// untouched. Never throws for malformed payloads; upstream read errors propagate.
/// </summary>
public static class WrappedSegmentReader
{
    /// <summary>Bytes needed to decide whether to buffer (PNG signature / RIFF....WEBP header).</summary>
    public const int PeekBytes = 12;

    private const int ChunkBytes = 64 * 1024;

    /// <summary>Synchronous variant. <paramref name="read"/> returns 0 (or negative) at end of input.</summary>
    public static WrappedSegmentRead Read(Func<byte[], int, int, int> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var buffer = new byte[ChunkBytes];
        var count = Fill(read, buffer, 0, PeekBytes, out var exhausted);
        if (exhausted || !SegmentPayload.IsWrapperContainer(buffer.AsSpan(0, count)))
            return new WrappedSegmentRead(buffer[..count], false, exhausted);

        while (!exhausted && count < PngWrappedTs.MaxPngBytes)
        {
            if (count == buffer.Length)
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, PngWrappedTs.MaxPngBytes));
            var want = buffer.Length - count;
            count = Fill(read, buffer, count, want, out exhausted);
        }

        return Finish(buffer, count, exhausted);
    }

    /// <summary>Asynchronous variant over a <see cref="Stream"/>.</summary>
    public static async Task<WrappedSegmentRead> ReadAsync(Stream upstream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        var buffer = new byte[ChunkBytes];
        var (count, exhausted) = await FillAsync(upstream, buffer, 0, PeekBytes, ct).ConfigureAwait(false);
        if (exhausted || !SegmentPayload.IsWrapperContainer(buffer.AsSpan(0, count)))
            return new WrappedSegmentRead(buffer[..count], false, exhausted);

        while (!exhausted && count < PngWrappedTs.MaxPngBytes)
        {
            if (count == buffer.Length)
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, PngWrappedTs.MaxPngBytes));
            (count, exhausted) = await FillAsync(upstream, buffer, count, buffer.Length - count, ct).ConfigureAwait(false);
        }

        return Finish(buffer, count, exhausted);
    }

    private static WrappedSegmentRead Finish(byte[] buffer, int count, bool exhausted)
    {
        var body = buffer.Length == count ? buffer : buffer[..count];
        // Over the cap: hand back what we have and let the caller stream the rest
        // (the player will reject it as it would have without us).
        if (!exhausted)
            return new WrappedSegmentRead(body, false, false);

        return SegmentPayload.TryUnwrap(body, out var ts)
            ? new WrappedSegmentRead(ts, true, true)
            : new WrappedSegmentRead(body, false, true);
    }

    private static int Fill(Func<byte[], int, int, int> read, byte[] buffer, int offset, int want, out bool exhausted)
    {
        exhausted = false;
        var end = offset + want;
        while (offset < end)
        {
            var n = read(buffer, offset, end - offset);
            if (n <= 0)
            {
                exhausted = true;
                break;
            }

            offset += n;
        }

        return offset;
    }

    private static async Task<(int Count, bool Exhausted)> FillAsync(
        Stream upstream, byte[] buffer, int offset, int want, CancellationToken ct)
    {
        var end = offset + want;
        while (offset < end)
        {
            var n = await upstream.ReadAsync(buffer.AsMemory(offset, end - offset), ct).ConfigureAwait(false);
            if (n <= 0)
                return (offset, true);
            offset += n;
        }

        return (offset, false);
    }
}