using System;
using System.Linq;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

/// <summary>
/// Unit tests for <see cref="MirrorEvidence.FromMediaPlaylist"/> and
/// <see cref="MirrorProofPolicy"/> (docs/STREAM_PLAYBACK_RULES.md "Proven mirrors").
/// Numbers are from the 31 Oct 2026 Aston Villa v Fulham session (v223 log, times UTC):
/// edgestream2.pro syaLqucEne4k-944307180.ts at 20:21:45.362, -953640450.ts at 20:23:31.002,
/// edgestream5.pro (same path) -957960270.ts at 20:24:17.960.
/// </summary>
public class MirrorProofPolicyTests
{
    internal const string Edge2 = "https://edgestream2.pro/hls/syaLqucEne4k.m3u8";
    internal const string Edge5 = "https://edgestream5.pro/hls/syaLqucEne4k.m3u8";

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

    internal static MirrorEvidence Edge2At2021 =>
        Evidence(Edge2, PtsPlaylist("syaLqucEne4k", 944307180), At2021);
    internal static MirrorEvidence Edge2At2023 =>
        Evidence(Edge2, PtsPlaylist("syaLqucEne4k", 953640450), At2023);
    internal static MirrorEvidence Edge5At2024 =>
        Evidence(Edge5, PtsPlaylist("syaLqucEne4k", 957960270), At2024);

    [Fact]
    public void Parser_ReadsStemPtsClockSequenceAndTargetDuration()
    {
        var e = Edge2At2021;

        Assert.Equal("syaLqucEne4k-{n}.ts", e.SegmentNameStem);
        Assert.True(e.SegmentNumbersArePts);
        Assert.Equal((long?)(944307180L + 2 * 540_000), e.EdgeSegmentNumber);
        Assert.Equal((long?)157384, e.MediaSequence);
        Assert.Equal((double?)6, e.TargetDurationSeconds);
        Assert.Equal(3, e.SegmentUris.Count);
        Assert.StartsWith("https://edgestream2.pro/hls/syaLqucEne4k-944307180.ts", e.SegmentUris[0]);
        Assert.Equal(At2021, e.ObservedAt);
    }

    [Fact]
    public void Parser_PlainCounterNamesAreNotAPtsClock()
    {
        var playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg-1001.ts\n#EXTINF:6.0,\nseg-1002.ts";
        var e = MirrorEvidence.FromMediaPlaylist(playlist, Edge2, Edge2, At2021)!;

        Assert.Equal("seg-{n}.ts", e.SegmentNameStem);
        Assert.False(e.SegmentNumbersArePts);
    }

    [Fact]
    public void Parser_SingleSegmentCannotProveAClock()
    {
        var e = Evidence(Edge2, PtsPlaylist("syaLqucEne4k", 944307180, segments: 1), At2021);
        Assert.False(e.SegmentNumbersArePts);
    }

    [Fact]
    public void Parser_ProgramDateTimeIsCarriedToTheLiveEdge()
    {
        var e = Evidence(Edge2, PtsPlaylist("abc", 100000, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 20, 21, 42, TimeSpan.Zero), e.EdgeProgramDateTime);
    }

    [Fact]
    public void Parser_EmptyOrSegmentlessPlaylistGivesNoEvidence()
    {
        Assert.Null(MirrorEvidence.FromMediaPlaylist("", Edge2, Edge2, At2021));
        Assert.Null(MirrorEvidence.FromMediaPlaylist("#EXTM3U\n#EXT-X-TARGETDURATION:6", Edge2, Edge2, At2021));
    }

    [Fact]
    public void PtsDelta_HandlesThe33BitWrap()
    {
        const long wrap = 1L << 33;
        Assert.Equal(540_000, MirrorEvidence.PtsDelta(wrap - 270_000, 270_000));
        Assert.Equal(-540_000, MirrorEvidence.PtsDelta(270_000, wrap - 270_000));
    }

    [Fact]
    public void Edgestream2And5_SamePathSameClock_AreProvenMirrors()
    {
        var proof = MirrorProofPolicy.Evaluate(Edge2At2021, Edge5At2024);

        // (957960270-944307180)/90000 = 151.70s of media in 152.60s of wall clock.
        Assert.True(proof.IsProven);
        Assert.Equal(MirrorProofBasis.PtsClock, proof.Basis);
        Assert.InRange(proof.DeltaSeconds!.Value, -1.0, -0.8);
        Assert.StartsWith("pts delta", proof.Reason);
    }

    [Fact]
    public void SameHostLaterRead_ClockAdvancesWithWallTime()
    {
        // 103.70s of media in 105.64s of wall clock: -1.94s, inside +/-5s.
        var proof = MirrorProofPolicy.Evaluate(Edge2At2021, Edge2At2023);
        Assert.True(proof.IsProven);
        Assert.InRange(proof.DeltaSeconds!.Value, -2.0, -1.9);
    }

    [Fact]
    public void EvaluationIsSymmetric()
    {
        var ab = MirrorProofPolicy.Evaluate(Edge2At2021, Edge5At2024);
        var ba = MirrorProofPolicy.Evaluate(Edge5At2024, Edge2At2021);
        Assert.Equal(ab.IsProven, ba.IsProven);
        Assert.Equal(ab.DeltaSeconds!.Value, -ba.DeltaSeconds!.Value, 3);
    }

    [Fact]
    public void OutOfTolerance_IsNotAMirror_EvenWithSamePathOnSiblingHost()
    {
        // Same stem, sibling host name, but 10s of media apart: host similarity is no reason.
        var shifted = Evidence(Edge5, PtsPlaylist("syaLqucEne4k", 957960270 + 900_000), At2024);
        var proof = MirrorProofPolicy.Evaluate(Edge2At2021, shifted);
        Assert.False(proof.IsProven);
        Assert.Contains("out of tolerance", proof.Reason);
    }

    [Fact]
    public void JustInsideAndOutsideThePtsTolerance()
    {
        // Edge5 read is 152.598s after Edge2; media 152.598 +/- 4.9 vs +/- 5.1 s.
        var exact = 944307180 + (long)Math.Round(152.598 * 90_000);
        var inside = Evidence(Edge5, PtsPlaylist("syaLqucEne4k", exact + 441_000), At2024);
        var outside = Evidence(Edge5, PtsPlaylist("syaLqucEne4k", exact - 459_000), At2024);
        Assert.True(MirrorProofPolicy.Evaluate(Edge2At2021, inside).IsProven);
        Assert.False(MirrorProofPolicy.Evaluate(Edge2At2021, outside).IsProven);
    }

    [Fact]
    public void DifferentStems_UnrelatedStreams_AreNotMirrors()
    {
        var other = Evidence("https://edgestream5.pro/hls/northgate.m3u8",
            PtsPlaylist("northgate", 957960270), At2024);
        Assert.False(MirrorProofPolicy.Evaluate(Edge2At2021, other).IsProven);
    }

    [Theory]
    [InlineData("109", "155")]
    [InlineData("109", "261")]
    [InlineData("155", "261")]
    public void DifferentPremiumChannels_NeverMerge_EvenWhenClocksAgree(string a, string b)
    {
        // wedd48c serves many channels from one encoder farm; identical clocks and names
        // are possible, so the channel id in the path always wins.
        var ea = Evidence($"https://edge.wedd48c.top/premium{a}/index.m3u8",
            PtsPlaylist("seg", 944307180), At2021);
        var eb = Evidence($"https://edge.wedd48c.top/premium{b}/index.m3u8",
            PtsPlaylist("seg", 944307180), At2021);

        var proof = MirrorProofPolicy.Evaluate(ea, eb);
        Assert.False(proof.IsProven);
        Assert.Contains("premium", proof.Reason);
    }

    [Fact]
    public void PremiumOnOneSideOnly_NeverMerges()
    {
        var ea = Evidence("https://edge.wedd48c.top/premium109/index.m3u8", PtsPlaylist("seg", 944307180), At2021);
        var eb = Evidence("https://other.example/live/index.m3u8", PtsPlaylist("seg", 944307180), At2021);
        Assert.False(MirrorProofPolicy.Evaluate(ea, eb).IsProven);
    }

    [Fact]
    public void MissingEvidence_NeverMerges()
    {
        Assert.False(MirrorProofPolicy.Evaluate(null, Edge5At2024).IsProven);
        Assert.False(MirrorProofPolicy.Evaluate(Edge2At2021, null).IsProven);
        Assert.False(MirrorProofPolicy.Evaluate(null, null).IsProven);
    }

    [Fact]
    public void AmbiguousEvidence_CounterNamesWithoutDateTime_NeverMerges()
    {
        var p1 = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg-1001.ts\n#EXTINF:6.0,\nseg-1002.ts";
        var a = MirrorEvidence.FromMediaPlaylist(p1, Edge2, Edge2, At2021);
        var b = MirrorEvidence.FromMediaPlaylist(p1, Edge5, Edge5, At2021);
        var proof = MirrorProofPolicy.Evaluate(a, b);
        Assert.False(proof.IsProven);
        Assert.Equal("no comparable clock evidence", proof.Reason);
    }

    [Fact]
    public void ProgramDateTime_WithinTwoSeconds_SameTargetAndPattern_IsProven()
    {
        // Counter-named segments (no PTS clock) but PROGRAM-DATE-TIME agrees once the
        // 60s between the reads is taken out.
        var a = Evidence("https://a.example/live/x.m3u8",
            PtsPlaylist("chA", 1001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        var b = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("chB", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:22:31.000Z"), At2021.AddSeconds(60));

        var proof = MirrorProofPolicy.Evaluate(a, b);
        Assert.True(proof.IsProven);
        Assert.Equal(MirrorProofBasis.ProgramDateTime, proof.Basis);
        Assert.Equal(1.0, proof.DeltaSeconds!.Value, 3);
    }

    [Fact]
    public void ProgramDateTime_OutsideTwoSeconds_OrDifferentTargetDuration_IsNotProven()
    {
        var a = Evidence("https://a.example/live/x.m3u8",
            PtsPlaylist("chA", 1001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        var late = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("chB", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:21:33.000Z"), At2021);
        var otherTarget = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("chB", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z", targetDuration: 4),
            At2021);

        Assert.False(MirrorProofPolicy.Evaluate(a, late).IsProven);
        Assert.False(MirrorProofPolicy.Evaluate(a, otherTarget).IsProven);
    }

    [Fact]
    public void ProgramDateTime_DifferentNamingPattern_IsNotProven()
    {
        var a = Evidence("https://a.example/live/x.m3u8",
            PtsPlaylist("chA", 1001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        var b = Evidence("https://b.example/live/y.m3u8",
            PtsPlaylist("media_v1_x", 2001, ptsStep: 1, programDateTime: "2026-10-31T20:21:30.000Z"), At2021);
        Assert.False(MirrorProofPolicy.Evaluate(a, b).IsProven);
    }
}
