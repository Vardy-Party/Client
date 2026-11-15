namespace VardyParty.Playback;

/// <summary>
/// Shared <see cref="ISwitchBackProbe"/>: a GET of the abandoned playlist with the
/// row's Referer and request headers, read up to 256 KB. Registered in DI as a
/// typed <see cref="HttpClient"/> (DoH-aware handler, 5s timeout) so Android,
/// Windows and Linux all probe through the same pipeline.
/// </summary>
public sealed class HttpSwitchBackProbe(HttpClient httpClient) : ISwitchBackProbe
{
    public const string UserAgent = "VardyParty/switch-back";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const int MaxBodyBytes = 256 * 1024;

    private static readonly Lazy<HttpSwitchBackProbe> Fallback = new(() =>
        new HttpSwitchBackProbe(new HttpClient { Timeout = Timeout }));

    /// <summary>Used only when a host has no DI-registered probe (e.g. a bare test host).</summary>
    public static ISwitchBackProbe Default => Fallback.Value;

    public async Task<SwitchBackProbeResult> ProbeAsync(SwitchBackCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var (code, body) = await FetchAsync(candidate.PlaylistUrl, candidate, cancellationToken).ConfigureAwait(false);
        var playable = PlaylistReloadEvaluation.IsPlayable(code, body);
        if (!playable)
            return new SwitchBackProbeResult(false, code);

        // A master playlist never changes, so its own fingerprint would look frozen on
        // every second probe. Follow one variant and judge that media playlist instead.
        var variantUrl = SwitchBackPlaylistProgress.VariantUrl(body, candidate.PlaylistUrl);
        if (variantUrl is null)
            return new SwitchBackProbeResult(true, code, SwitchBackPlaylistProgress.Of(body));

        var (variantCode, variantBody) = await FetchAsync(variantUrl, candidate, cancellationToken).ConfigureAwait(false);
        var variantPlayable = PlaylistReloadEvaluation.IsPlayable(variantCode, variantBody);
        return new SwitchBackProbeResult(
            variantPlayable,
            variantCode,
            variantPlayable ? SwitchBackPlaylistProgress.Of(variantBody) : null);
    }

    private async Task<(int Code, byte[] Body)> FetchAsync(
        string url,
        SwitchBackCandidate candidate,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (!string.IsNullOrWhiteSpace(candidate.Referer))
            req.Headers.TryAddWithoutValidation("Referer", candidate.Referer);
        if (candidate.RequestHeaders is { } headers)
        {
            foreach (var pair in headers)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    continue;
                if (pair.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(candidate.Referer))
                    continue;
                req.Headers.Remove(pair.Key);
                req.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        var buffer = new byte[MaxBodyBytes];
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                .ConfigureAwait(false);
            if (n <= 0)
                break;
            read += n;
        }

        return (code, buffer.AsSpan(0, read).ToArray());
    }
}
