using VardyParty.Ports;

namespace VardyParty.Playback;

/// <summary>Playlist-only GET used by option 2. Never calls LocalService <c>/play</c>.</summary>
public static class SwitchBackPlaylistProbe
{
    private const int MaxBodyBytes = 256 * 1024;
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VardyParty/switch-back");
        return client;
    }

    public static async Task<(bool Playable, int StatusCode)> GetAsync(
        SwitchBackCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        using var req = new HttpRequestMessage(HttpMethod.Get, candidate.PlaylistUrl);
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
                req.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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

        return (PlaylistReloadEvaluation.IsPlayable(code, buffer.AsSpan(0, read)), code);
    }

    public static bool RestoreAndSwitchTo(IStreamSwitchingService? switching, SwitchBackCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (switching is null)
            return false;

        var rows = switching.GetHealthyStreams();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var catalog = row.Stream?.Url;
            var match = (!string.IsNullOrWhiteSpace(candidate.IdentityKey)
                    && string.Equals(catalog, candidate.IdentityKey, StringComparison.OrdinalIgnoreCase))
                || string.Equals(row.ResolvedM3U8Url, candidate.PlaylistUrl, StringComparison.OrdinalIgnoreCase);
            if (!match)
                continue;

            row.ResolvedM3U8Url = candidate.PlaylistUrl;
            row.UnusedResolvedUrl = true;
            return switching.SwitchToStream(i);
        }

        return false;
    }
}
