using System.Globalization;
using System.Text.RegularExpressions;

namespace VardyParty.Kernel;

/// <summary>
/// What a passing media (variant) playlist looked like when the health check read it.
/// <see cref="MirrorProofPolicy"/> compares two of these to decide whether two different
/// URLs are the same encoder output served from two hosts (docs/STREAM_PLAYBACK_RULES.md
/// "Proven mirrors"). Host names are deliberately not part of the evidence.
/// </summary>
public sealed record MirrorEvidence
{
    /// <summary>The m3u8 the health check started from (master or media).</summary>
    public required string SourceUrl { get; init; }

    /// <summary>The media playlist the segments were read from.</summary>
    public required string PlaylistUrl { get; init; }

    /// <summary>Absolute segment URIs in playlist order.</summary>
    public IReadOnlyList<string> SegmentUris { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Segment file name with its trailing number replaced by <c>{n}</c>, e.g.
    /// <c>syaLqucEne4k-{n}.ts</c>. Null when the segments do not share one stem.
    /// </summary>
    public string? SegmentNameStem { get; init; }

    /// <summary>
    /// Shape of <see cref="SegmentNameStem"/> (letter runs to <c>a</c>, digit runs to <c>9</c>),
    /// e.g. <c>a9a-{n}.ts</c>. Used only with the PROGRAM-DATE-TIME proof.
    /// </summary>
    public string? NamingPattern { get; init; }

    /// <summary>Number in the live-edge (last) segment name.</summary>
    public long? EdgeSegmentNumber { get; init; }

    /// <summary>
    /// True when consecutive segment numbers advance by EXTINF x 90000, i.e. the names carry
    /// the MPEG-TS 90 kHz presentation clock. Needs at least two numbered segments.
    /// </summary>
    public bool SegmentNumbersArePts { get; init; }

    public long? MediaSequence { get; init; }

    /// <summary>PROGRAM-DATE-TIME of the live-edge segment (last tag plus following EXTINFs).</summary>
    public DateTimeOffset? EdgeProgramDateTime { get; init; }

    public double? TargetDurationSeconds { get; init; }

    /// <summary>When the playlist was read (wall clock of this device).</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    public const long PtsClockHz = 90_000;

    private static readonly Regex TrailingNumber =
        new(@"^(?<prefix>.*?)(?<num>\d{4,})(?<ext>\.[A-Za-z0-9]{1,5})?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a media playlist. Returns null when it has no segments. Never throws on odd input.
    /// </summary>
    public static MirrorEvidence? FromMediaPlaylist(
        string content,
        string playlistUrl,
        string sourceUrl,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<(string Uri, double Duration, DateTimeOffset? Pdt)>();
        long? mediaSequence = null;
        double? targetDuration = null;
        double? pendingDuration = null;
        DateTimeOffset? pendingPdt = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var seq))
                    mediaSequence = seq;
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(line["#EXT-X-TARGETDURATION:".Length..].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var td))
                    targetDuration = td;
            }
            else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTimeOffset.TryParse(line["#EXT-X-PROGRAM-DATE-TIME:".Length..].Trim(),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pdt))
                    pendingPdt = pdt;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["#EXTINF:".Length..];
                var comma = value.IndexOf(',');
                if (comma >= 0)
                    value = value[..comma];
                pendingDuration = double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var d) ? d : null;
            }
            else if (!line.StartsWith('#') && pendingDuration.HasValue)
            {
                DateTimeOffset? pdt = pendingPdt;
                if (pdt is null && segments.Count > 0 && segments[^1].Pdt is { } previous)
                    pdt = previous.AddSeconds(segments[^1].Duration);
                segments.Add((HlsUriResolver.Resolve(playlistUrl, line), pendingDuration.Value, pdt));
                pendingDuration = null;
                pendingPdt = null;
            }
        }

        if (segments.Count == 0)
            return null;

        string? stem = null;
        var stemsAgree = true;
        var numbers = new List<long?>(segments.Count);
        foreach (var segment in segments)
        {
            var (segmentStem, number) = SplitName(segment.Uri);
            numbers.Add(number);
            if (segmentStem is null)
            {
                stemsAgree = false;
                continue;
            }

            if (stem is null)
                stem = segmentStem;
            else if (!string.Equals(stem, segmentStem, StringComparison.Ordinal))
                stemsAgree = false;
        }

        if (!stemsAgree)
            stem = null;

        var isPts = stem is not null && segments.Count >= 2 && numbers.All(n => n.HasValue);
        for (var i = 1; isPts && i < segments.Count; i++)
        {
            var step = PtsDelta(numbers[i - 1]!.Value, numbers[i]!.Value);
            var expected = segments[i - 1].Duration * PtsClockHz;
            // 90 kHz names advance by the segment duration; a plain counter advances by 1.
            if (expected <= 0 || Math.Abs(step - expected) > Math.Max(0.15 * expected, 0.25 * PtsClockHz))
                isPts = false;
        }

        return new MirrorEvidence
        {
            SourceUrl = sourceUrl,
            PlaylistUrl = playlistUrl,
            SegmentUris = segments.Select(s => s.Uri).ToList().AsReadOnly(),
            SegmentNameStem = stem,
            NamingPattern = stem is null ? null : ShapeOf(stem),
            EdgeSegmentNumber = stem is null ? null : numbers[^1],
            SegmentNumbersArePts = isPts,
            MediaSequence = mediaSequence,
            EdgeProgramDateTime = segments[^1].Pdt,
            TargetDurationSeconds = targetDuration,
            ObservedAt = observedAt,
        };
    }

    /// <summary>
    /// Signed difference <paramref name="later"/> - <paramref name="earlier"/> on the 33-bit
    /// MPEG-TS clock, so a wrap between the two observations still gives a small delta.
    /// </summary>
    public static long PtsDelta(long earlier, long later)
    {
        const long wrap = 1L << 33;
        var delta = (later - earlier) % wrap;
        if (delta >= wrap / 2) delta -= wrap;
        if (delta < -wrap / 2) delta += wrap;
        return delta;
    }

    private static (string? Stem, long? Number) SplitName(string uri)
    {
        var path = uri;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            path = parsed.AbsolutePath;
        else
        {
            var cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path[..cut];
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        var match = TrailingNumber.Match(name);
        if (!match.Success
            || !long.TryParse(match.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return (null, null);

        return (match.Groups["prefix"].Value + "{n}" + match.Groups["ext"].Value, n);
    }

    private static string ShapeOf(string stem)
    {
        var body = stem.Replace("{n}", "\u0000", StringComparison.Ordinal);
        body = Regex.Replace(body, "[A-Za-z]+", "a");
        body = Regex.Replace(body, "[0-9]+", "9");
        return body.Replace("\u0000", "{n}", StringComparison.Ordinal);
    }
}
