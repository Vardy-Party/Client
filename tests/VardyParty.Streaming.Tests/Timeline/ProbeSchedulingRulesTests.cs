using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace VardyParty.Streaming.Tests.Timeline;

[CollectionDefinition(nameof(ProbeTimelineCollection), DisableParallelization = true)]
public sealed class ProbeTimelineCollection
{
}

/// <summary>
/// docs/PROBE_SCHEDULING_RULES.md as timeline tests. Each case runs the real resolver,
/// LocalLanPlayService and throttle against scripted LocalService milestones on a
/// fake clock (instant). On failure the message carries the whole milestone table.
/// Runs alone (no parallel tests) because the harness waits for the thread pool to go
/// quiet between milestones.
/// </summary>
[Collection(nameof(ProbeTimelineCollection))]
public class ProbeSchedulingRulesTests(ITestOutputHelper output)
{
    public static TheoryData<string> CurrentCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in ProbeSchedulingScenarios.Current)
            data.Add(c.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CurrentCases))]
    public async Task Current(string name)
    {
        // Arrange
        var probeCase = ProbeSchedulingScenarios.Find(name);

        // Act
        var timeline = await ProbeTimelineHarness.RunAsync(probeCase.Scenario());

        // Assert
        output.WriteLine($"{name}: {timeline.SettleCount} settles, {timeline.SettleTotalMs:0} ms, slowest {timeline.SettleMaxMs:0} ms");
        var failures = probeCase.Expect
            .Select(e => (e, Why: e.Check(timeline)))
            .Where(x => x.Why is not null)
            .Select(x => $"  - {x.e.Description}: {x.Why}")
            .ToList();
        Assert.True(failures.Count == 0,
            $"{name} [{probeCase.Rules}]\n{string.Join("\n", failures)}\n\n{timeline.Render()}");
    }

    /// <summary>
    /// A1-A5 for every scenario, written to the test output (run with a detailed
    /// console logger to read it). Fails only when a measure that applies is missed:
    /// A1 when there is a healthy row, A2 and A4 when there is an MP page, A3 always,
    /// A5 when there is a top tier (High-confidence recommendations).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllScenarios))]
    public async Task Acceptance(string scenario)
    {
        // Arrange
        var make = ProbeSchedulingScenarios.All.Single(s => s.Name == scenario).Scenario;
        var mpIds = make().Rows.Where(r => r.IsMp).Select(r => r.Id).ToList();

        // Act
        var timeline = await ProbeTimelineHarness.RunAsync(make());

        // Assert
        var rows = new List<(string Measure, string? Why, bool Applies)>
        {
            ("A1", Expect.FirstPlayWithinOfFirstHealthy().Check(timeline),
                timeline.HealthVerdicts.Any(h => h.Status == VardyParty.Kernel.StreamHealthStatus.Healthy)),
            ("A3", Expect.NoIdleSlotWhileQueued().Check(timeline), true),
            ("A5", Expect.TopTierHeadStartHeld().Check(timeline), timeline.HeadStart is not null),
        };
        foreach (var id in mpIds)
        {
            var listed = timeline.ListedAt(id) is not null;
            rows.Add(($"A2 {id}", Expect.ChipsVisibleWithin(id).Check(timeline), listed));
            rows.Add(($"A4 {id}", Expect.SeedReleasesSlotBy(id, ProbeAcceptance.SeedGiveUpSeconds + 0.001).Check(timeline), true));
        }

        foreach (var (measure, why, applies) in rows)
        {
            var known = KnownMisses.TryGetValue($"{scenario} {measure}", out var reason) ? $" (known: {reason})" : "";
            output.WriteLine($"{scenario} {measure}: {(!applies ? "n/a" : why is null ? "PASS" : "FAIL " + why + known)}");
        }

        var failed = rows.Where(r => r.Applies && r.Why is not null && !KnownMisses.ContainsKey($"{scenario} {r.Measure}")).ToList();
        Assert.True(failed.Count == 0,
            $"{scenario}: {string.Join("; ", failed.Select(f => $"{f.Measure} {f.Why}"))}\n\n{timeline.Render()}");
    }

    /// <summary>Acceptance misses the rules accept, with why. Reported, not failed.</summary>
    private static readonly Dictionary<string, string> KnownMisses = new()
    {
        ["SeedSilent A2 mp"] =
            "the seed gave up at t+30s (R7) and its infrastructure retry asks again at t+37s; LocalService listed the chips at t+34s",
    };

    public static TheoryData<string> AllScenarios()
    {
        var data = new TheoryData<string>();
        foreach (var s in ProbeSchedulingScenarios.All)
            data.Add(s.Name);
        return data;
    }

    [Fact]
    public void NoOpenGaps()
    {
        // Arrange
        var gaps = ProbeSchedulingScenarios.Gaps;

        // Act
        var open = gaps.Select(g => g.Name).ToList();

        // Assert
        Assert.Empty(open);
    }
}
