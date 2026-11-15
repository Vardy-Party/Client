using System.Text;

namespace VardyParty.Playback;

/// <summary>
/// Option 2 playlist probe (a playlist-only GET; never LocalService <c>/play</c>).
/// One shared implementation (<see cref="HttpSwitchBackProbe"/>) is registered in DI
/// so every platform probes the same way; tests substitute a fake.
/// </summary>
public interface ISwitchBackProbe
{
    /// <summary>
    /// Fetches <see cref="SwitchBackCandidate.PlaylistUrl"/> with the row's headers.
    /// May throw on a network failure; <see cref="SwitchBackLoop"/> treats that as
    /// an unhealthy probe.
    /// </summary>
    Task<SwitchBackProbeResult> ProbeAsync(SwitchBackCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>
/// Fingerprint of a live playlist window: <c>#EXT-X-MEDIA-SEQUENCE</c> plus the
/// last segment path (query dropped, so rotating tokens on the same segment do
/// not look like progress). Two probes with the same fingerprint mean the CDN is
/// serving a frozen playlist.
/// A master playlist (<c>#EXT-X-STREAM-INF</c>) never moves, so it has no
/// fingerprint (<see langword="null"/>); <see cref="HttpSwitchBackProbe"/> follows
/// <see cref="VariantUrl"/> once and fingerprints that media playlist instead.
/// </summary>
public static class SwitchBackPlaylistProgress
{
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE:";
    private const string StreamInfTag = "#EXT-X-STREAM-INF:";

    public static bool IsMasterPlaylist(ReadOnlySpan<byte> body) =>
        !body.IsEmpty
        && Encoding.UTF8.GetString(body).Contains(StreamInfTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The highest-BANDWIDTH variant of a master playlist (first one on a tie or when
    /// none declares BANDWIDTH), resolved against <paramref name="masterUrl"/>.
    /// Null when the body is not a master playlist.
    /// </summary>
    public static string? VariantUrl(ReadOnlySpan<byte> body, string masterUrl)
    {
        if (!IsMasterPlaylist(body))
            return null;

        string? best = null;
        var bestBandwidth = long.MinValue;
        long? pending = null;
        var expectUri = false;
        foreach (var raw in Encoding.UTF8.GetString(body).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith(StreamInfTag, StringComparison.OrdinalIgnoreCase))
            {
                pending = Bandwidth(line[StreamInfTag.Length..]);
                expectUri = true;
                continue;
            }

            if (line[0] == '#' || !expectUri)
                continue;
            expectUri = false;
            var bandwidth = pending ?? -1;
            if (best is null || bandwidth > bestBandwidth)
            {
                best = line;
                bestBandwidth = bandwidth;
            }
        }

        if (best is null)
            return null;
        return Uri.TryCreate(masterUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, best, out var resolved)
            ? resolved.AbsoluteUri
            : best;
    }

    private static long? Bandwidth(string attributes)
    {
        foreach (var part in attributes.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Trim().Equals("BANDWIDTH", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(pair[1].Trim(), out var value))
            {
                return value;
            }
        }

        return null;
    }

    public static string? Of(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty || IsMasterPlaylist(body))
            return null;

        string? sequence = null;
        string? lastSegment = null;
        foreach (var raw in Encoding.UTF8.GetString(body).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith(MediaSequenceTag, StringComparison.OrdinalIgnoreCase))
            {
                sequence = line[MediaSequenceTag.Length..].Trim();
                continue;
            }

            if (line[0] == '#')
                continue;
            var query = line.IndexOf('?', StringComparison.Ordinal);
            lastSegment = query >= 0 ? line[..query] : line;
        }

        return sequence is null && lastSegment is null
            ? null
            : $"{sequence ?? "-"}|{lastSegment ?? "-"}";
    }
}
