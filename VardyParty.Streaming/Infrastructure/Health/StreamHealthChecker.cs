using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;

namespace VardyParty.Streaming;

public class StreamHealthChecker(
    HttpClient httpClient,
    ILogger<StreamHealthChecker> logger,
    IOptions<StreamHealthSettings> streamHealthOptions) : IStreamHealthChecker
{
    private const int MaxRecursionDepth = 3;
    private StreamHealthSettings StreamHealthOptions = streamHealthOptions.Value;

    public async Task<StreamHealth> CheckStreamHealthAsync(string m3u8Url, string refererUrl,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var health = new StreamHealth { Url = m3u8Url, Status = StreamHealthStatus.Unknown };

        try
        {
            logger.LogInformation("[StreamHealthChecker] Checking stream health for {Url} with referer {Referer}",
                m3u8Url, refererUrl);
            // Use a short timeout for the overall check if token doesn't have one, 
            // but rely on caller's token primarily.
            // We'll enforce a specific timeout for the HTTP calls if needed.

            health.Status = await CheckRecursivelyAsync(m3u8Url, refererUrl, 0, cancellationToken);

            // Extract metadata from the manifest if check was successful
            if (health.Status == StreamHealthStatus.Healthy)
                await ExtractMetadataAsync(m3u8Url, refererUrl, health, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed for {Url}", m3u8Url);
            health.Status = StreamHealthStatus.ManifestUnreachable;
            health.ErrorMessage = ex.Message;
        }
        finally
        {
            sw.Stop();
            health.CheckDurationMs = sw.ElapsedMilliseconds;
        }

        return health;
    }

    private async Task ExtractMetadataAsync(string m3u8Url, string refererUrl, StreamHealth health,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(StreamHealthOptions.ManifestTimeoutSeconds));

            var request = new HttpRequestMessage(HttpMethod.Get, m3u8Url);
            // Use a browser-like User-Agent to match playback and avoid hosts treating probe requests differently
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            if (!string.IsNullOrEmpty(refererUrl) && Uri.TryCreate(refererUrl, UriKind.Absolute, out var refUri))
                request.Headers.Referrer = refUri;
            logger.LogInformation("[StreamHealthChecker] ExtractMetadataAsync requesting {Url} with referer {Referer}",
                m3u8Url, refererUrl);

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) return;

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(content)) return;

            ExtractStreamMetadata(content, health);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract metadata for {Url}", m3u8Url);
        }
    }

    private void ExtractStreamMetadata(string manifestContent, StreamHealth health)
    {
        var lines = manifestContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // Look for EXT-X-STREAM-INF tag which contains RESOLUTION, FRAME-RATE, BANDWIDTH
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                // Extract RESOLUTION (e.g., "1920x1080")
                var resolutionMatch = Regex.Match(line, @"RESOLUTION=([^,\s]+)", RegexOptions.IgnoreCase);
                if (resolutionMatch.Success) health.Resolution = resolutionMatch.Groups[1].Value;

                // Extract FRAME-RATE (e.g., "30" or "29.97")
                var frameRateMatch = Regex.Match(line, @"FRAME-RATE=([^,\s]+)", RegexOptions.IgnoreCase);
                if (frameRateMatch.Success && int.TryParse(frameRateMatch.Groups[1].Value, out var fps))
                    health.FrameRate = fps;

                // Extract BANDWIDTH in bits per second, convert to kbps
                var bandwidthMatch = Regex.Match(line, @"BANDWIDTH=([^,\s]+)", RegexOptions.IgnoreCase);
                if (bandwidthMatch.Success && int.TryParse(bandwidthMatch.Groups[1].Value, out var bandwidth))
                    health.Bitrate = bandwidth / 1000; // Convert bps to kbps

                // Extract CODECS attribute (e.g., CODECS="avc1.640028,mp4a.40.2")
                var codecsPrefix = "CODECS=\"";
                var idx = line.IndexOf(codecsPrefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var start = idx + codecsPrefix.Length;
                    var end = line.IndexOf('\"', start);
                    if (end > start)
                    {
                        var codecsText = line.Substring(start, end - start);
                        var codecs = codecsText.Split(',').Select(s => s.Trim()).ToArray();
                        if (codecs.Length > 0) health.VideoCodec = codecs[0];
                        if (codecs.Length > 1) health.AudioCodec = codecs[1];
                    }
                }

                // We found metadata, no need to continue
                break;
            }
        }
    }

    private async Task<StreamHealthStatus> CheckRecursivelyAsync(string url, string refererUrl, int depth,
        CancellationToken ct)
    {
        if (depth > MaxRecursionDepth)
        {
            logger.LogWarning("Max recursion depth reached for {Url}", url);
            return StreamHealthStatus.InvalidManifest;
        }

        string? content;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(StreamHealthOptions.ManifestTimeoutSeconds));

            content = await FetchManifestContentAsync(url, refererUrl, cts.Token);
            if (content is null && !string.IsNullOrEmpty(refererUrl))
            {
                logger.LogInformation(
                    "[StreamHealthChecker] Manifest fetch with referer failed for {Url}; retrying without referer",
                    url);
                content = await FetchManifestContentAsync(url, refererUrl: null, cts.Token);
            }

            if (content is null)
            {
                return StreamHealthStatus.ManifestUnreachable;
            }
        }
        catch
        {
            return StreamHealthStatus.ManifestUnreachable;
        }

        if (string.IsNullOrWhiteSpace(content)) return StreamHealthStatus.EmptyManifest;

        // Identify playlist type
        // Master playlist contains EXT-X-STREAM-INF
        if (content.Contains("#EXT-X-STREAM-INF"))
        {
            var nextUrl = ExtractFirstStreamUrl(content, url);
            if (string.IsNullOrEmpty(nextUrl)) return StreamHealthStatus.InvalidManifest;
            return await CheckRecursivelyAsync(nextUrl, refererUrl, depth + 1, ct);
        }

        // Media playlist contains EXTINF
        if (content.Contains("#EXTINF"))
        {
            var segmentUrl = ExtractFirstSegmentUrl(content, url);
            if (string.IsNullOrEmpty(segmentUrl)) return StreamHealthStatus.EmptyManifest;

            return await CheckSegmentAsync(segmentUrl, refererUrl, ct);
        }

        // If it starts with #EXTM3U but has neither stream nor inf, it might be empty or valid but with no segments yet (live)
        if (content.TrimStart().StartsWith("#EXTM3U")) return StreamHealthStatus.EmptyManifest;

        return StreamHealthStatus.InvalidManifest;
    }

    private string? ExtractFirstStreamUrl(string content, string baseUrl)
    {
        // Simple line parser. Find line after #EXT-X-STREAM-INF
        // Or regex
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("#EXT-X-STREAM-INF"))
                if (i + 1 < lines.Length)
                {
                    var uriLine = lines[i + 1].Trim();
                    if (!uriLine.StartsWith("#")) return ResolveUrl(baseUrl, uriLine);
                }

        return null;
    }

    private string? ExtractFirstSegmentUrl(string content, string baseUrl)
    {
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
            // Typically segment follows EXTINF
            if (lines[i].StartsWith("#EXTINF"))
                if (i + 1 < lines.Length)
                {
                    var uriLine = lines[i + 1].Trim();
                    // Some playlists might have other tags before the URL
                    // Keep scanning until we find a non-tag line or end
                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        var nextLine = lines[j].Trim();
                        if (string.IsNullOrEmpty(nextLine)) continue;

                        if (!nextLine.StartsWith("#")) return ResolveUrl(baseUrl, nextLine);
                        // If it's another tag, continue. 
                        // Note: #EXT-X-BYTERANGE might appear.
                    }
                }

        return null;
    }

    private string ResolveUrl(string baseUrl, string relativeUrl)
    {
        if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out _)) return relativeUrl;
        if (Uri.TryCreate(new Uri(baseUrl), relativeUrl, out var absoluteUri)) return absoluteUri.ToString();
        return relativeUrl;
    }

    private async Task<string?> FetchManifestContentAsync(string url, string? refererUrl, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        if (!string.IsNullOrEmpty(refererUrl) && Uri.TryCreate(refererUrl, UriKind.Absolute, out var refUri))
            request.Headers.Referrer = refUri;
        logger.LogInformation("[StreamHealthChecker] Fetching manifest {Url} with referer {Referer}", url,
            refererUrl ?? "(none)");

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Manifest fetch failed: {StatusCode} for {Url}", response.StatusCode, url);
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    private async Task<StreamHealthStatus> CheckSegmentAsync(string url, string refererUrl, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(StreamHealthOptions.SegmentTimeoutSeconds));

            if (await ProbeSegmentAsync(HttpMethod.Head, url, refererUrl, cts.Token))
            {
                return StreamHealthStatus.Healthy;
            }

            // Tokenized CDNs often reject HEAD (403/401/405) while ranged GET succeeds.
            if (await ProbeSegmentAsync(HttpMethod.Get, url, refererUrl, cts.Token, useRange: true))
            {
                return StreamHealthStatus.Healthy;
            }

            logger.LogWarning("Segment check failed for {Url}", url);
            return StreamHealthStatus.SegmentUnreachable;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Segment exception for {Url}", url);
            return StreamHealthStatus.SegmentUnreachable;
        }
    }

    private async Task<bool> ProbeSegmentAsync(
        HttpMethod method,
        string url,
        string refererUrl,
        CancellationToken ct,
        bool useRange = false)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        if (!string.IsNullOrEmpty(refererUrl) && Uri.TryCreate(refererUrl, UriKind.Absolute, out var refUri))
            request.Headers.Referrer = refUri;
        if (useRange)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

        logger.LogInformation("[StreamHealthChecker] Checking segment {Method} {Url} with referer {Referer}",
            method.Method, url, refererUrl);

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Segment {Method} probe returned {StatusCode} for {Url}",
                method.Method, response.StatusCode, url);
        }

        return response.IsSuccessStatusCode;
    }
}