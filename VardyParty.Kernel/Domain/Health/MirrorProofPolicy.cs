using System.Text.RegularExpressions;

namespace VardyParty.Kernel;

public enum MirrorProofBasis
{
    None,
    /// <summary>Same segment name stem and the 90 kHz numbers in the names advance in step.</summary>
    PtsClock,
    /// <summary>Live-edge PROGRAM-DATE-TIME agrees, same target duration and naming pattern.</summary>
    ProgramDateTime,
}

/// <param name="IsProven">True only when the evidence proves the two playlists are one encoder output.</param>
/// <param name="Basis">Which rule proved it (None when not proven).</param>
/// <param name="DeltaSeconds">
/// Clock disagreement after correcting for the time between the two observations
/// (seconds; null when no clock comparison was possible).
/// </param>
/// <param name="Reason">Short human-readable reason, for logs.</param>
public sealed record MirrorProof(bool IsProven, MirrorProofBasis Basis, double? DeltaSeconds, string Reason)
{
    public static MirrorProof No(string reason, double? delta = null) =>
        new(false, MirrorProofBasis.None, delta, reason);
}

/// <summary>
/// Decides whether two healthy streams with different m3u8 identities are proven mirrors
/// (docs/STREAM_PLAYBACK_RULES.md "Proven mirrors"). Pure: no I/O, no clock.
/// <para>
/// A merge needs proof from the media itself. Host-name similarity (edgestream2 vs
/// edgestream5) is never a reason; missing or ambiguous evidence never merges; different
/// <c>premiumNNN</c> channel ids never merge.
/// </para>
/// </summary>
public static class MirrorProofPolicy
{
    /// <summary>PTS rule: allowed disagreement after elapsed-time correction.</summary>
    public static readonly TimeSpan PtsTolerance = TimeSpan.FromSeconds(5);

    /// <summary>PROGRAM-DATE-TIME rule: allowed disagreement after elapsed-time correction.</summary>
    public static readonly TimeSpan ProgramDateTimeTolerance = TimeSpan.FromSeconds(2);

    private static readonly Regex PremiumId =
        new(@"premium(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static MirrorProof Evaluate(MirrorEvidence? a, MirrorEvidence? b)
    {
        if (a is null || b is null)
            return MirrorProof.No("missing evidence");

        var premiumA = PremiumIds(a);
        var premiumB = PremiumIds(b);
        if ((premiumA.Count > 0 || premiumB.Count > 0) && !premiumA.SetEquals(premiumB))
            return MirrorProof.No(
                $"different premium channel ids ({string.Join(",", premiumA)} vs {string.Join(",", premiumB)})");

        var elapsed = (b.ObservedAt - a.ObservedAt).TotalSeconds;

        // (a) Same name stem and both names carry the 90 kHz clock.
        if (a.SegmentNumbersArePts && b.SegmentNumbersArePts
            && a.SegmentNameStem is { Length: > 0 } stemA
            && string.Equals(stemA, b.SegmentNameStem, StringComparison.Ordinal)
            && a.EdgeSegmentNumber is { } edgeA && b.EdgeSegmentNumber is { } edgeB)
        {
            var mediaSeconds = MirrorEvidence.PtsDelta(edgeA, edgeB) / (double)MirrorEvidence.PtsClockHz;
            var delta = mediaSeconds - elapsed;
            return Math.Abs(delta) <= PtsTolerance.TotalSeconds
                ? new MirrorProof(true, MirrorProofBasis.PtsClock, delta, $"pts delta {delta:+0.00;-0.00}s")
                : MirrorProof.No($"pts out of tolerance ({delta:+0.00;-0.00}s)", delta);
        }

        // (b) Live-edge PROGRAM-DATE-TIME, same target duration and naming pattern.
        if (a.EdgeProgramDateTime is { } pdtA && b.EdgeProgramDateTime is { } pdtB
            && a.TargetDurationSeconds is { } tdA && b.TargetDurationSeconds is { } tdB
            && Math.Abs(tdA - tdB) < 0.001
            && a.NamingPattern is { Length: > 0 } patternA
            && string.Equals(patternA, b.NamingPattern, StringComparison.Ordinal))
        {
            var delta = (pdtB - pdtA).TotalSeconds - elapsed;
            return Math.Abs(delta) <= ProgramDateTimeTolerance.TotalSeconds
                ? new MirrorProof(true, MirrorProofBasis.ProgramDateTime, delta, $"pdt delta {delta:+0.00;-0.00}s")
                : MirrorProof.No($"program-date-time out of tolerance ({delta:+0.00;-0.00}s)", delta);
        }

        return MirrorProof.No("no comparable clock evidence");
    }

    private static HashSet<string> PremiumIds(MirrorEvidence evidence)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var url in evidence.SegmentUris.Prepend(evidence.PlaylistUrl).Prepend(evidence.SourceUrl))
        {
            foreach (Match m in PremiumId.Matches(PathOf(url)))
                ids.Add(m.Groups[1].Value.TrimStart('0'));
        }

        return ids;
    }

    private static string PathOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url.Split('?', '#')[0];
}
