using System;
using System.Linq;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

/// <summary>
/// Unit tests for <see cref="MirrorEvidence.FromMediaPlaylist"/> and
/// <see cref="MirrorProofPolicy"/> (docs/STREAM_PLAYBACK_RULES.md "Proven mirrors").
/// Numbers are from a 31 Oct 2026 captured session (v223 log, times UTC):
/// cdn-alpha.example.com streamstem-944307180.ts at 20:21:45.362, -953640450.ts at 20:23:31.002,
/// cdn-beta.example.com (same path) -957960270.ts at 20:24:17.960.
/// </summary>
public class MirrorProofPolicyTests
{
    internal const string CdnAlpha = "https://cdn-alpha.example.com/hls/streamstem.m3u8";
    internal const string CdnBeta = "https://cdn-beta.example.com/hls/streamstem.m3u8";

    internal static readonly DateTimeOffset At2021 = new(2026, 10, 31, 20, 21, 45, 362, TimeSpan.Zero);
    internal static readonly DateTimeOffset At2023 = new(2026, 10, 31, 20, 23, 31, 2, TimeSpan.Zero);
    internal static readonly DateTimeOffset At2024 = new(2026, 10, 31, 20, 24, 17, 960, TimeSpan.Zero);

    /// <summary>Live playlist of 3 x 6s segments whose names carry the 90 kHz clock.</summary>
    internal static string PtsPlaylist(string stem, long firstPts, int segments = 3, double duration = 6.0,
        string? programDateTime = null, int targetDuration = 6, long ptsStep = -1)
    {
        var step = ptsStep >= 0 ? ptsStep : (long)(duration * 90_000);
        var lines = new System.Collections.Generic.List<string>
        {
            "#EXTM3U", "#EXT-X-VERSION:3", $"#EXT-X-TARGETDURATION:{targetDuration}", "#EXT-X-MEDIA-SEQUENCE:157384",
        };
        if (programDateTime is not null)
            lines.Add($"#EXT-X-PROGRAM-DATE-TIME:{programDateTime}");
        for (var i = 0; i < segments; i++)
        {
            lines.Add("#EXTINF:" + duration.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ",");
            lines.Add($"{stem}-{firstPts + i * step}.ts?st=tok{i}&e=1793488895");
        }

        return string.Join("\n", lines);
    }

    internal static MirrorEvidence Evidence(string url, string playlist, DateTimeOffset at) =>
        MirrorEvidence.FromMediaPlaylist(playlist, url, url, at)!;

    internal static MirrorEvidence AlphaAt2021 =>
        Evidence(CdnAlpha, PtsPlaylist("streamstem", 944307180), At2021);
    internal static MirrorEvidence AlphaAt2023 =>
        Evidence(CdnAlpha, PtsPlaylist("streamstem", 953640450), At2023);
    internal static MirrorEvidence BetaAt2024 =>
        Evidence(CdnBeta, PtsPlaylist("streamstem", 957960270), At2024);

    [Fact]
    public void Parser_ReadsStemPtsClockSequenceAndTargetDuration()
    {
        // Arrange
        var playlist = PtsPlaylist("streamstem", 944307180);

        // Act
        var e = Evidence(CdnAlpha, playlist, At2021);

        // Assert
        Assert.Equal("streamstem-{n}.ts", e.SegmentNameStem);
        Assert.True(e.SegmentNumbersArePts);
        Assert.Equal((long?)(944307180L + 2 * 540_000), e.EdgeSegmentNumber);
        Assert.Equal((long?)157384, e.MediaSequence);
        Assert.Equal((double?)6, e.TargetDurationSeconds);
        Assert.Equal(3, e.SegmentUris.Count);
        Assert.StartsWith("https://cdn-alpha.example.com/hls/streamstem-944307180.ts", e.SegmentUris[0]);
        Assert.Equal(At2021, e.ObservedAt);
    }

    [Fact]
    public void Parser_PlainCounterNamesAreNotAPtsClock()
    {
        // Arrange
        var playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg-1001.ts\n#EXTINF:6.0,\nseg-1002.ts";

        // Act
        var e = MirrorEvidence.FromMediaPlaylist(playlist, CdnAlpha, CdnAlpha, At2021)!;

        // Assert
        Assert.Equal("seg-{n}.ts", e.SegmentNameStem);
        Assert.False(e.SegmentNumbersArePts);
    }

    [Fact]
    public void Parser_SingleSegmentCannotProveAClock()
    {
        // Arrange
        var playlist = PtsPlaylist("streamstem", 944307180, segments: 1);

        // Act
        var e = Evidence(CdnAlpha, playlist, At2021);

        // Assert
        Assert.False(e.SegmentNumbersArePts);
    }

    [Fact]
    public void Parser_ProgramDateTimeIsCarriedToTheLiveEdge()
    {
        // Arrange
        var playlist = PtsPlaylist("abc", 100000, programDateTime: "2026-10-31T20:21:30.000Z");

        // Act
        var e = Evidence(CdnAlpha, playlist, At2021);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 20, 21, 42, TimeSpan.Zero), e.EdgeProgramDateTime);
    }

    [Fact]
    public void Parser_EmptyOrSegmentlessPlaylistGivesNoEvidence()
    {
        // Arrange
        var empty = "";
        var headerOnly = "#EXTM3U\n#EXT-X-TARGETDURATION:6";

        // Act
        var fromEmpty = MirrorEvidence.FromMediaPlaylist(empty, CdnAlpha, CdnAlpha, At2021);
        var fromHeader = MirrorEvidence.FromMediaPlaylist(headerOnly, CdnAlpha, CdnAlpha, At2021);

        // Assert
        Assert.Null(fromEmpty);
        Assert.Null(fromHeader);
    }

    [Fact]
    public void PtsDelta_HandlesThe33BitWrap()
    {
        // Arrange
        const long wrap = 1L << 33;

        // Act
        var forward = MirrorEvidence.PtsDelta(wrap - 270_000, 270_000);
        var backward = MirrorEvidence.PtsDelta(270_000, wrap - 270_000);

        // Assert
        Assert.Equal(540_000, forward);
        Assert.Equal(-540_000, backward);
    }

    [Fact]
    public void CdnAlphaAndBeta_SamePathSameClock_AreProvenMirrors()
    {
        // Arrange
        var left = AlphaAt2021;
        var right = BetaAt2024;

        // Act
        var proof = MirrorProofPolicy.Evaluate(left, right);

        // Assert — (957960270-944307180)/90000 = 151.70s of media in 152.60s of wall clock.
        Assert.True(proof.IsProven);
        Assert.Equal(MirrorProofBasis.PtsClock, proof.Basis);
        Assert.InRange(proof.DeltaSeconds!.Value, -1.0, -0.8);
        Assert.StartsWith("pts delta", proof.Reason);
    }

    [Fact]
    public void SameHostLaterRead_ClockAdvancesWithWallTime()
    {
        // Arrange — 103.70s of media in 105.64s of wall clock: -1.94s, inside +/-5s.
        var left = AlphaAt2021;
        var right = AlphaAt2023;

        // Act
        var proof = MirrorProofPolicy.Evaluate(left, right);

        // Assert
        Assert.True(proof.IsProven);
        Assert.InRange(proof.DeltaSeconds!.Value, -2.0, -1.9);
    }

    [Fact]
    public void EvaluationIsSymmetric()
    {
        // Arrange
        var left = AlphaAt2021;
        var right = BetaAt2024;

        // Act
        var ab = MirrorProofPolicy.Evaluate(left, right);
        var ba = MirrorProofPolicy.Evaluate(right, left);

        // Assert
        Assert.Equal(ab.IsProven, ba.IsProven);
        Assert.Equal(ab.DeltaSeconds!.Value, -ba.DeltaSeconds!.Value, 3);
    }

    [Fact]
    public void OutOfTolerance_IsNotAMirror_EvenWithSamePathOnSiblingHost()
    {
        // Arrange — same stem, sibling host name, but 10s of media apart: host similarity is no reason.
        var shifted = Evidence(CdnBeta, PtsPlaylist("streamstem", 957960270 + 900_000), At2024);

        // Act
        var proof = MirrorProofPolicy.Evaluate(AlphaAt2021, shifted);

        // Assert
        Assert.False(proof.IsProven);
        Assert.Contains("out of tolerance", proof.Reason);
    }

    [Fact]
    public void JustInsideAndOutsideThePtsTolerance()
    {
        // Arrange — beta read is 152.598s after alpha; media 152.598 +/- 4.9 vs +/- 5.1 s.
        var exact = 944307180 + (long)Math.Round(152.598 * 90_000);
        var inside = Evidence(CdnBeta, PtsPlaylist("streamstem", exact + 441_000), At2024);
        var outside = Evidence(CdnBeta, PtsPlaylist("streamstem", exact - 459_000), At2024);

        // Act
        var insideProof = MirrorProofPolicy.Evaluate(AlphaAt2021, inside);
        var outsideProof = MirrorProofPolicy.Evaluate(AlphaAt2021, outside);

        // Assert
        Assert.True(insideProof.IsProven);
        Assert.False(outsideProof.IsProven);
    }

    [Fact]
    public void DifferentStems_UnrelatedStreams_AreNotMirrors()
    {
        // Arrange
        var other = Evidence("https://cdn-beta.example.com/hls/northgate.m3u8",
            PtsPlaylist("northgate", 957960270), At2024);

        // Act
        var proof = MirrorProofPolicy.Evaluate(AlphaAt2021, other);

        // Assert
        Assert.False(proof.IsProven);
    }

    [Theory]
    [InlineData("109", "155")]
    [InlineData("109", "261")]
    [InlineData("155", "261")]
    public void DifferentPremiumChannels_NeverMerge_EvenWhenClocksAgree(string a, string b)
    {
        // Arrange — one encoder farm can serve many channels; identical clocks and names
        // are possible, so the channel id in the path always wins.
        var ea = Evidence($"https://edge.channel-farm.example/premium{a}/index.m3u8",
            PtsPlaylist("seg", 944307180), At2021);
        var eb = Evidence($"https://edge.channel-farm.example/premium{b}/index.m3u8",
            PtsPlaylist("seg", 944307180), At2021);

        // Act
        var proof = MirrorProofPolicy.Evaluate(ea, eb);

        // Assert
        Assert.False(proof.IsProven);
        Assert.Contains("premium", proof.Reason);
    }

    [Fact]
    public void PremiumOnOneSideOnly_NeverMerges()
    {
        // Arrange
        var ea = Evidence("https://edge.channel-farm.example/premium109/index.m3u8",
            PtsPlaylist("seg", 944307180), At2021);
        var eb = Evidence("https://other.example/live/index.m3u8",
            PtsPlaylist("seg", 944307180), At2021);

        // Act
        var proof = MirrorProofPolicy.Evaluate(ea, eb);

        // Assert
        Assert.False(proof.IsProven);
    }

    [Fact]
    public void MissingEvidence_NeverMerges()
    {
        // Arrange
        MirrorEvidence? none = null;

        // Act
        var leftMissing = MirrorProofPolicy.Evaluate(none, BetaAt2024);
        var rightMissing = MirrorProofPolicy.Evaluate(AlphaAt2021, none);
        var bothMissing = MirrorProofPolicy.Evaluate(none, none);

        // Assert
        Assert.False(leftMissing.IsProven);
        Assert.False(rightMissing.IsProven);
        Assert.False(bothMissing.IsProven);
    }

    [Fact]
    public void AmbiguousEvidence_CounterNamesWithoutDateTime_NeverMerges()
    {
        // Arrange
        var p1 = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg-1001.ts\n#EXTINF:6.0,\nseg-1002.ts";
        var a = MirrorEvidence.FromMediaPlaylist(p1, CdnAlpha, CdnAlpha, At2021);
        var b = MirrorEvidence.FromMediaPlaylist(p1, CdnBeta, CdnBeta, At2021);

        // Act
        var proof = MirrorProofPolicy.Evaluate(a, b);

        // Assert
        Assert.False(proof.IsProven);
        Assert.Equal("no comparable clock evidence", proof.Reason);
    }

    [Fact]
    public void ProgramDateTime_WithinTwoSeconds_SameTargetAndPattern_IsProven()
    {
        // Arrange — counter-named segments (no PTS clock) with the same name stem, and
        // PROGRAM-DATE-TIME agrees once the 60s between the reads is taken out.
        var a = Evidence("https://a.example/live/x.m3u8",
            PtsPlaylist("chA", 1001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        var b = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("chA", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:22:31.000Z"), At2021.AddSeconds(60));

        // Act
        var proof = MirrorProofPolicy.Evaluate(a, b);

        // Assert
        Assert.True(proof.IsProven);
        Assert.Equal(MirrorProofBasis.ProgramDateTime, proof.Basis);
        Assert.Equal(1.0, proof.DeltaSeconds!.Value, 3);
    }

    [Fact]
    public void ProgramDateTime_OutsideTwoSeconds_OrDifferentTargetDuration_IsNotProven()
    {
        // Arrange
        var a = Evidence("https://a.example/live/x.m3u8",
            PtsPlaylist("chA", 1001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        var late = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("chA", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:21:33.000Z"), At2021);
        var otherTarget = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("chA", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z", targetDuration: 4),
            At2021);

        // Act
        var lateProof = MirrorProofPolicy.Evaluate(a, late);
        var targetProof = MirrorProofPolicy.Evaluate(a, otherTarget);

        // Assert
        Assert.False(lateProof.IsProven);
        Assert.False(targetProof.IsProven);
    }

    [Fact]
    public void ProgramDateTime_DifferentNamingPattern_IsNotProven()
    {
        // Arrange
        var a = Evidence("https://a.example/live/x.m3u8",
            PtsPlaylist("chA", 1001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        var b = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("media_v1_x", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);

        // Act
        var proof = MirrorProofPolicy.Evaluate(a, b);

        // Assert
        Assert.False(proof.IsProven);
    }

    [Fact]
    public void ProgramDateTime_Agrees_ButSegmentStemsDiffer_IsNotProven()
    {
        // Arrange — rule (b) tightened (31 Oct 2026): two unrelated live streams can have wall
        // clocks that agree to the second. Without the same segment-name stem the
        // clock is not proof.
        var a = Evidence("https://a.example/live/x.m3u8",
            PtsPlaylist("chA", 1001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        var b = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("chB", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:22:30.500Z"), At2021.AddSeconds(60));

        // Act
        var proof = MirrorProofPolicy.Evaluate(a, b);

        // Assert
        Assert.False(proof.IsProven);
        Assert.Equal(MirrorProofBasis.None, proof.Basis);
        Assert.Equal("different segment-name stems", proof.Reason);
    }
}
