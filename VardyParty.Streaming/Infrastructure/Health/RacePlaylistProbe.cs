using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>Reads the playlist at a fresh <c>/play</c> URL before a startup race treats it as a winner.</summary>
public interface IRacePlaylistProbe
{
    Task<PlaylistVideoVerdict> ClassifyAsync(M3U8Response response, CancellationToken cancellationToken);
}

/// <summary>
/// GETs the playlist with the response headers. A failed or blocked fetch is
/// <see cref="PlaylistVideoVerdict.Unverified"/> (not a drop): soak CDNs often refuse
/// a bare client GET, and the player still confirms picture.
/// </summary>
public sealed class RacePlaylistProbe(HttpClient httpClient) : IRacePlaylistProbe
{
    public async Task<PlaylistVideoVerdict> ClassifyAsync(M3U8Response response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.Url)
            || !Uri.TryCreate(response.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return PlaylistVideoVerdict.Unverified;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            CopyProbeHeaders(response.RequestHeaders, request);
            using var httpResponse = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
                return PlaylistVideoVerdict.Unverified;

            var body = await httpResponse.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (body.Length > 65_536)
                body = body[..65_536];
            return PlaylistVideoEvidence.Classify(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PlaylistVideoVerdict.Unverified;
        }
        catch (Exception)
        {
            return PlaylistVideoVerdict.Unverified;
        }
    }

    private static void CopyProbeHeaders(Dictionary<string, string>? headers, HttpRequestMessage request)
    {
        if (headers is null)
            return;
        foreach (var pair in headers)
        {
            if (pair.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }
    }
}
