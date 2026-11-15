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
/// </summary>
public static class SwitchBackPlaylistProgress
{
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE:";

    public static string? Of(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
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
