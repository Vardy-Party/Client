using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;

namespace VardyParty.Streaming.Tests.Timeline;

/// <summary>What LocalService does with one <c>GET /play</c>, as milestones after the request lands.</summary>
internal sealed record PlayScript(
    double AnswerAt,
    PlayAnswer Answer,
    double HealthTakes = 1,
    StreamHealthStatus Health = StreamHealthStatus.Healthy,
    PlayLink Link = PlayLink.Plain);

/// <summary>What kind of link a playable <c>/play</c> answer carries (its <c>requestHeaders</c>).</summary>
internal enum PlayLink
{
    /// <summary>No playback headers: the probed link is reused as is (no warm needed).</summary>
    Plain,

    /// <summary>A Referer soak header: the probed link plays at first play; as a backup the prewarmer warms it.</summary>
    Soak,

    /// <summary>A Cookie-bound session: single-use, so first play needs a fresh <c>/play</c> and it is never warmed.</summary>
    SingleUse
}

internal enum PlayAnswer
{
    /// <summary>200 with an m3u8 URL.</summary>
    Playlist,

    /// <summary>404 "M3U8 not captured": a dead FB link (Jon: ~20s to fail).</summary>
    NoPlaylist,

    /// <summary>504 page navigation timed out: CDN unreachable (Deferred, never Failed).</summary>
    CdnUnreachable
}

/// <summary>Shorthand for <see cref="PlayScript"/>; times are seconds after the request lands.</summary>
internal static class Play
{
    /// <summary>A dead FB link. Jon's fact: dead FB links take ~20s to fail.</summary>
    public const double DeadFbSeconds = 20;

    public static PlayScript HealthyAt(double seconds, double healthTakes = 1) =>
        new(seconds, PlayAnswer.Playlist, healthTakes);

    /// <summary>Healthy, with a Referer soak link (the prewarmer warms it as a backup).</summary>
    public static PlayScript SoakAt(double seconds, double healthTakes = 1) =>
        new(seconds, PlayAnswer.Playlist, healthTakes, Link: PlayLink.Soak);

    /// <summary>Healthy, with a Cookie-bound single-use link (first play needs a fresh <c>/play</c>).</summary>
    public static PlayScript SingleUseAt(double seconds, double healthTakes = 1) =>
        new(seconds, PlayAnswer.Playlist, healthTakes, Link: PlayLink.SingleUse);

    public static PlayScript Dead(double seconds = DeadFbSeconds) =>
        new(seconds, PlayAnswer.NoPlaylist);

    /// <summary>LocalService returns a playlist, but the client health check says it is dead.</summary>
    public static PlayScript PlaylistThenDeadHealth(double seconds, double healthTakes = 1) =>
        new(seconds, PlayAnswer.Playlist, healthTakes, StreamHealthStatus.ManifestNotFound);

    public static PlayScript CdnUnreachableAt(double seconds) =>
        new(seconds, PlayAnswer.CdnUnreachable);
}

internal enum ChipKind
{
    /// <summary>Playlist URL plus probe-ready rewritten segments (the seed accepts this).</summary>
    Playable,

    /// <summary>Playlist URL but no rewritten segments yet ("chip Y has a playlist but no segments").</summary>
    PlaylistOnly,

    /// <summary>LocalService answered the chip with no URL.</summary>
    Miss
}

internal sealed record MpLine(double At, string Json, string What);

/// <summary>
/// What LocalService streams back on one <c>POST /mp</c> (NDJSON), as milestones
/// after the request lands. Jon's fact: a normal <c>/mp</c> takes ~10s for the first
/// chip and ~1s per extra chip; 330s is only our harvest timeout.
/// </summary>
internal sealed class MpScript
{
    private readonly List<MpLine> _lines = new();
    private readonly Dictionary<string, (ChipKind Kind, double HealthTakes, StreamHealthStatus Health)> _chips =
        new(StringComparer.OrdinalIgnoreCase);

    private MpScript(double headersAt) => HeadersAt = headersAt;

    /// <summary>When the 200 response headers arrive (the body keeps streaming after).</summary>
    public double HeadersAt { get; }

    public IReadOnlyList<MpLine> Lines => _lines;

    public IReadOnlyDictionary<string, (ChipKind Kind, double HealthTakes, StreamHealthStatus Health)> ChipVerdicts => _chips;

    /// <summary>A chip's verdict on its second health check (the rule 7 early-chip retry), when it differs.</summary>
    public IReadOnlyDictionary<string, StreamHealthStatus> RetryVerdicts => _retryVerdicts;

    private readonly Dictionary<string, StreamHealthStatus> _retryVerdicts = new(StringComparer.OrdinalIgnoreCase);

    public static MpScript Headers(double seconds) => new(seconds);

    /// <summary>The <c>chips</c> event: every label LocalService found on the page.</summary>
    public MpScript Chips(double seconds, params string[] labels)
    {
        var json = "{\"type\":\"chips\",\"streams\":[" + string.Join(",", labels.Select(Quote)) + "]}";
        _lines.Add(new MpLine(seconds, json, $"chips [{string.Join(", ", labels)}]"));
        return this;
    }

    /// <summary>One <c>chip</c> event.</summary>
    public MpScript Chip(
        double seconds,
        string label,
        ChipKind kind = ChipKind.Playable,
        double healthTakes = 1,
        StreamHealthStatus health = StreamHealthStatus.Healthy,
        StreamHealthStatus? retryHealth = null)
    {
        _chips[label] = (kind, healthTakes, health);
        if (retryHealth is { } retry)
            _retryVerdicts[label] = retry;
        _lines.Add(new MpLine(seconds, ChipJson(label, kind), $"chip {label} ({kind})"));
        return this;
    }

    public MpScript Done(double seconds)
    {
        _lines.Add(new MpLine(seconds, "{\"type\":\"done\"}", "done"));
        return this;
    }

    /// <summary>An NDJSON <c>error</c> line that ends the harvest (5xx: LocalService's own failure, infrastructure).</summary>
    public MpScript Error(double seconds, int statusCode = 500)
    {
        _lines.Add(new MpLine(seconds,
            "{\"type\":\"error\",\"statusCode\":" + statusCode + ",\"error\":\"mp play execution failed\"}",
            $"error {statusCode}"));
        return this;
    }

    /// <summary>A normal harvest: chip list and first chip at ~10s, then one chip per ~1s.</summary>
    public static MpScript Normal(
        IReadOnlyList<string> labels,
        double first = 10,
        double then = 1,
        ChipKind kind = ChipKind.Playable)
    {
        var script = Headers(first - 1).Chips(first - 1, labels.ToArray());
        for (var i = 0; i < labels.Count; i++)
            script.Chip(first + i * then, labels[i], kind);
        return script.Done(first + (labels.Count - 1) * then + 0.5);
    }

    /// <summary>
    /// A slow page from a real Windows run (12 Nov 2026, see docs/PROBE_SCHEDULING_RULES.md):
    /// headers at 8.4s, ten chips listed, each takes ~10s and none ever carries
    /// probe-ready segments, done at ~111s.
    /// </summary>
    public static MpScript SlowNoProbeReadySeed(IReadOnlyList<string> labels, double headersAt = 8.4, double every = 10.2, double doneAt = 111)
    {
        var script = Headers(headersAt).Chips(headersAt, labels.ToArray());
        for (var i = 0; i < labels.Count; i++)
            script.Chip(headersAt + (i + 1) * every, labels[i], ChipKind.PlaylistOnly);
        return script.Done(doneAt);
    }

    internal string ChipUrl(string rowId, string label) => $"https://cdn.example.test/{rowId}/{Uri.EscapeDataString(label)}.m3u8";

    private string ChipJson(string label, ChipKind kind)
    {
        // The URL is filled in per row by the harness ({row} placeholder).
        var url = kind == ChipKind.Miss ? "" : "{chipUrl:" + label + "}";
        var segments = kind == ChipKind.Playable
            ? ",\"rewrittenSegments\":[\"https://seg.example.test/live/" + Uri.EscapeDataString(label) + "/1.ts?_s2=a\"]"
            : "";
        return "{\"type\":\"chip\",\"label\":" + Quote(label) + ",\"url\":\"" + url + "\"" + segments + "}";
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <param name="Retry">What LocalService does with the row's second and later <c>/play</c> (a re-queue pass); null repeats <paramref name="Play"/>.</param>
internal sealed record RowSpec(string Id, PlayScript? Play, MpScript? Mp, int Run = 1, PlayScript? Retry = null)
{
    public bool IsMp => Mp is not null;
}

/// <summary>
/// One realistic case: the catalog (in catalog order), the crowd recommendations,
/// and what LocalService does for each row. The harness derives try-order with the
/// production <see cref="StreamTestOrderPolicy"/>.
/// </summary>
internal sealed class ProbeScenario
{
    private readonly List<RowSpec> _rows = new();
    private readonly List<(string Id, RecommendationConfidence Confidence)> _recommended = new();

    private ProbeScenario(string name) => Name = name;

    public string Name { get; }

    public IReadOnlyList<RowSpec> Rows => _rows;

    public IReadOnlyList<(string Id, RecommendationConfidence Confidence)> Recommended => _recommended;

    /// <summary>
    /// After first play, run the playback-priority window as the orchestrator does (rule 11):
    /// for <see cref="ResolverRaceThrottle.PlaybackPriorityTimeout"/> finding is paused only
    /// while a backup needs warming. On by default.
    /// </summary>
    public bool PriorityWindowAfterFirstPlay { get; private set; } = true;

    /// <summary>Stop the run (cancel) at this many seconds.</summary>
    public double Horizon { get; private set; } = 600;

    public static ProbeScenario Named(string name) => new(name);

    /// <summary>
    /// When set, the run is cancelled at this many seconds (a match switch) and a second
    /// resolver run starts on the same LocalLanPlayService and throttle with the rows
    /// added after <see cref="ThenSwitchAt"/>.
    /// </summary>
    public double? SwitchAt { get; private set; }

    private int Run => SwitchAt is null ? 1 : 2;

    public ProbeScenario ThenSwitchAt(double seconds)
    {
        SwitchAt = seconds;
        return this;
    }

    /// <summary>An FB row; <paramref name="retry"/> answers its later <c>/play</c>s (e.g. a re-queue pass).</summary>
    public ProbeScenario Fb(string id, PlayScript script, PlayScript? retry = null)
    {
        _rows.Add(new RowSpec(id, script, null, Run, retry));
        return this;
    }

    public ProbeScenario DeadFb(params string[] ids)
    {
        foreach (var id in ids)
            Fb(id, Play.Dead());
        return this;
    }

    /// <summary>An unlabeled catalog MP row (V2 page; LocalService discovers the chips).</summary>
    public ProbeScenario Mp(string id, MpScript script)
    {
        _rows.Add(new RowSpec(id, null, script, Run));
        return this;
    }

    public ProbeScenario Recommend(string id, RecommendationConfidence confidence = RecommendationConfidence.High)
    {
        _recommended.Add((id, confidence));
        return this;
    }

    /// <summary>
    /// When set, the orchestrator's re-queue pass is modelled: this many seconds after run 1
    /// ends, every row that ended Deferred is resolved again (same rows, same session, so the
    /// tiers and the Medium cap still apply, but no head start). The orchestrator waits 5s/15s
    /// for a LocalService outage and 2 min for a CDN-unreachable row.
    /// </summary>
    public double? ReResolveDeferredAfter { get; private set; }

    public ProbeScenario ThenReResolveDeferredAfter(double seconds)
    {
        ReResolveDeferredAfter = seconds;
        return this;
    }

    /// <summary>
    /// The CDN-unreachable recheck: like <see cref="ThenReResolveDeferredAfter"/>, but the wait
    /// is the orchestrator's first recheck (<see cref="CdnReachabilityPolicy.RecheckDelay(int, bool)"/>):
    /// 30s while the pool holds fewer than two rows when run 1 ends, else 2 min.
    /// </summary>
    public bool CdnRecheckFromPolicy { get; private set; }

    public ProbeScenario ThenCdnRecheckDeferred()
    {
        ReResolveDeferredAfter = 0;
        CdnRecheckFromPolicy = true;
        return this;
    }

    public ProbeScenario WithoutPriorityWindow()
    {
        PriorityWindowAfterFirstPlay = false;
        return this;
    }

    public ProbeScenario StopAt(double seconds)
    {
        Horizon = seconds;
        return this;
    }

    public override string ToString() => Name;
}
