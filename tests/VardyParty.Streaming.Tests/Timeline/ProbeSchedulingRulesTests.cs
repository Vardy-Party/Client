using System.Linq;
using System.Threading.Tasks;
using Xunit;

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
public class ProbeSchedulingRulesTests
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
    public Task Current(string name) => RunCase(name);

    [Fact(Skip = "GAP R5: the /mp slot (ResolverRaceThrottle) is held until the seed resolves (LocalLanPlayService.cs:420, :768).")]
    public Task Gap_R5_MpReleasesSlotOnChipList() => RunCase("GAP R5 Czechia: /mp releases its slot once the chip list arrives");

    [Fact(Skip = "GAP R6: a null seed drops the listed chips (StreamResolver.cs:292 -> SiblingsFrom :474-476).")]
    public Task Gap_R6_ChipsNeverDropped() => RunCase("GAP R6 Czechia: all 10 chips queued as rows, none dropped");

    [Fact(Skip = "GAP R4: /mp takes one of the two window places, so only one FB runs beside it (StreamResolver.cs:101-131).")]
    public Task Gap_R4_TwoFbBesideMp() => RunCase("GAP R4 two FB at once while /mp is pending");

    [Fact(Skip = "GAP R7: no seed deadline; only the 330s harvest timeout (LocalLanPlayService.cs:730; MpHarvestSession.cs:95-101).")]
    public Task Gap_R7_SeedGiveUpDeadline() => RunCase("GAP R7 seed gives up after 30s with no probe-ready chip");

    [Fact(Skip = "GAP R12: DrainOthersAsync waits on in-flight /mp (StreamResolutionOrchestrator.cs:1380-1382; ResolverRaceThrottle EnterAsync).")]
    public Task Gap_R12_RaceDrainIgnoresMp() => RunCase("GAP R12 the startup race never waits on /mp (Czechia)");

    public static TheoryData<string> GapCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in ProbeSchedulingScenarios.Gaps)
            data.Add(c.Name);
        return data;
    }

    /// <summary>
    /// Proves each GAP is real today: its outcome is NOT met on the current code. When a
    /// fix lands this fails for that case - un-skip its Gap_ fact and delete the matching
    /// TODAY case.
    /// </summary>
    [Theory]
    [MemberData(nameof(GapCases))]
    public async Task GapStillReproduces(string name)
    {
        var probeCase = ProbeSchedulingScenarios.Find(name);
        var timeline = await ProbeTimelineHarness.RunAsync(probeCase.Scenario());
        Assert.True(probeCase.Expect.Any(e => e.Check(timeline) is not null),
            $"{name} now holds - un-skip its Gap_ fact and remove the TODAY case.\n\n{timeline.Render()}");
    }

    [Fact]
    public void EveryGapCaseHasASkippedFact()
    {
        var facts = typeof(ProbeSchedulingRulesTests).GetMethods()
            .Where(m => m.Name.StartsWith("Gap_"))
            .Select(m => m.GetCustomAttributes(typeof(FactAttribute), false).Cast<FactAttribute>().Single())
            .ToList();
        Assert.Equal(ProbeSchedulingScenarios.Gaps.Length, facts.Count);
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Skip)));
    }

    private static async Task RunCase(string name)
    {
        var probeCase = ProbeSchedulingScenarios.Find(name);
        var timeline = await ProbeTimelineHarness.RunAsync(probeCase.Scenario());
        var failures = probeCase.Expect
            .Select(e => (e, Why: e.Check(timeline)))
            .Where(x => x.Why is not null)
            .Select(x => $"  - {x.e.Description}: {x.Why}")
            .ToList();
        Assert.True(failures.Count == 0,
            $"{name} [{probeCase.Rules}]\n{string.Join("\n", failures)}\n\n{timeline.Render()}");
    }
}
