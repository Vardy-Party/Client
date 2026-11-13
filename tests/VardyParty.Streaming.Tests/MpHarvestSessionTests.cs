using System;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public sealed class MpHarvestSessionTests
{
    private const string Page = "https://page.example.test/match/1";

    private static readonly DateTimeOffset Start = new(2026, 11, 12, 20, 12, 25, TimeSpan.Zero);

    private static LocalServiceUnavailableException Fault() =>
        new(LocalServiceUnavailableReason.ServiceError, "POST /mp returned 500");

    private static MpPlayEvent PlaylistOnly(string label) => new()
    {
        Type = "chip",
        Label = label,
        Url = $"https://stream.example.test/{label.Replace(' ', '-')}.m3u8"
    };

    private static MpPlayEvent ProbeReady(string label) => new()
    {
        Type = "chip",
        Label = label,
        Url = $"https://stream.example.test/{label.Replace(' ', '-')}.m3u8",
        RewrittenSegments = [$"https://cdn.example.test/{label.Replace(' ', '-')}.exe?_s2=1"]
    };

    [Fact]
    public async Task Fail_WaitersWithoutPlaylist_ThrowInfrastructure_NotNull()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        var seed = harvest.WaitForAsync(null, CancellationToken.None);
        var named = harvest.WaitForAsync("Chip B", CancellationToken.None);

        // Act
        harvest.Fail(Fault());

        // Assert
        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => seed);
        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => named);
        Assert.True(harvest.IsCompleted);
        Assert.NotNull(harvest.Fault);
        // A failed harvest with no playlist is not reused: the next row asks the service again.
        Assert.False(harvest.CanReuseFor(Page, Page));
    }

    [Fact]
    public async Task Fail_LabelWithCachedPlaylist_StillGetsIt_LateWaitersToo()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(ProbeReady("Chip A"));

        // Act
        harvest.Fail(Fault());

        // Assert
        Assert.Equal("https://stream.example.test/Chip-A.m3u8", (await harvest.WaitForAsync("Chip A", CancellationToken.None))?.Url);
        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => harvest.WaitForAsync("Chip B", CancellationToken.None));
    }

    [Fact]
    public async Task Fail_WithAPlaylist_ReusedHarvestReplaysTheFault_NotRetriedInPlace()
    {
        // Arrange - rule 13: a chip the faulted harvest never sent is Deferred; asking
        // the same finished harvest again at once would only replay the fault.
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(ProbeReady("Chip A"));
        harvest.Fail(Fault());

        // Act
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => harvest.WaitForAsync("Chip B", CancellationToken.None));

        // Assert
        Assert.Equal(LocalServiceUnavailableReason.ServiceError, ex.Reason);
        Assert.True(ex.FromFinishedHarvest);
        Assert.False(ex.IsRetryable);
        Assert.True(harvest.CanReuseFor(Page, Page));
    }

    [Fact]
    public async Task ErrorEvent5xx_FailsAsInfrastructure()
    {
        // Arrange
        var infra = new MpHarvestSession();

        // Act
        infra.Apply(new MpPlayEvent { Type = "error", StatusCode = 500, Error = "mp play execution failed" });

        // Assert
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => infra.WaitForAsync("Chip A", CancellationToken.None));
        Assert.Equal(LocalServiceUnavailableReason.ServiceError, ex.Reason);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task ErrorEvent4xx_IsAMiss()
    {
        // Arrange
        var miss = new MpHarvestSession();

        // Act
        miss.Apply(new MpPlayEvent { Type = "error", StatusCode = 404, Error = "M3U8 not captured from MP page." });

        // Assert
        Assert.Null(await miss.WaitForAsync("Chip A", CancellationToken.None));
        Assert.Null(miss.Fault);
    }

    [Fact]
    public void Fail_AfterDone_IsIgnored()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        harvest.Complete(failed: false);

        // Act
        harvest.Fail(Fault());

        // Assert
        Assert.Null(harvest.Fault);
    }

    [Fact]
    public async Task WaitForAsync_ReturnsFirstChipWithoutWaitingForLaterLabels()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"], SelectedStream = "Chip A" });
        harvest.Apply(ProbeReady("Chip A"));

        // Act
        var first = await harvest.WaitForAsync(null, CancellationToken.None);

        // Assert
        Assert.Equal("https://stream.example.test/Chip-A.m3u8", first?.Url);
        Assert.Equal("Chip A", first?.SelectedStream);
        Assert.Equal(["Chip A", "Chip B"], first?.Streams);
        Assert.False(harvest.IsCompleted);
    }

    [Fact]
    public async Task WaitForAsync_Seed_ReturnsChipListOnChipsEvent()
    {
        // Arrange - rule 5: the chip list releases the seed (no URL, every label) so
        // the resolver can queue each chip.
        var harvest = new MpHarvestSession();
        var seed = harvest.WaitForAsync(null, CancellationToken.None);

        // Act
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        var list = await seed;

        // Assert
        Assert.Equal("", list?.Url);
        Assert.Equal(["Chip A", "Chip B"], list?.Streams);
        Assert.False(harvest.IsCompleted);
        Assert.True(harvest.Listed.IsCompleted);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_StillWaitsForRewrittenAfterPlaylistOnlyLine()
    {
        // Arrange - a named wait holds out for probe-ready segments after a playlist-only line.
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip A"));
        var named = harvest.WaitForAsync("Chip A", CancellationToken.None);
        Assert.False(named.IsCompleted);
        Assert.False(harvest.IsChipReady("Chip A"));

        // Act
        harvest.Apply(ProbeReady("Chip A"));
        var chip = await named;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-A.m3u8", chip?.Url);
        Assert.Equal(["https://cdn.example.test/Chip-A.exe?_s2=1"], chip?.RewrittenSegments);
        Assert.True(harvest.IsChipReady("Chip A"));
    }

    [Fact]
    public async Task WaitForAsync_Seed_FirstChipWithoutList_ReturnsThatChipAsAList()
    {
        // Arrange - rule 5: with no chips event, the first chip releases the seed.
        var harvest = new MpHarvestSession();
        var seed = harvest.WaitForAsync(null, CancellationToken.None);

        // Act
        harvest.Apply(PlaylistOnly("Chip A"));
        var list = await seed;

        // Assert
        Assert.Equal("", list?.Url);
        Assert.Equal(["Chip A"], list?.Streams);
        Assert.Equal("Chip A", list?.SelectedStream);
    }

    [Fact]
    public async Task WaitForAsync_Seed_OtherLabelMiss_StillListsEveryChip()
    {
        // Arrange - rule 6: a miss on one label never turns the seed into a verdict.
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(new MpPlayEvent { Type = "chip", Label = "Chip B", Url = "" });

        // Act
        var list = await harvest.WaitForAsync(null, CancellationToken.None);

        // Assert
        Assert.Equal("", list?.Url);
        Assert.Equal(["Chip A", "Chip B"], list?.Streams);
    }

    [Fact]
    public async Task WaitForAsync_Seed_HarvestDoneWithAPlaylist_ReturnsTheListLedByIt()
    {
        // Arrange - rule 6: a harvest that ends with no probe-ready chip still hands over
        // its chips, led by one that has a playlist.
        var withPlaylist = new MpHarvestSession();
        withPlaylist.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        withPlaylist.Apply(PlaylistOnly("Chip B"));
        withPlaylist.Complete(failed: false);

        // Act
        var list = await withPlaylist.WaitForAsync(null, CancellationToken.None);

        // Assert
        Assert.Equal("", list?.Url);
        Assert.Equal(["Chip A", "Chip B"], list?.Streams);
    }

    [Fact]
    public async Task WaitForAsync_Seed_HarvestDoneWithNoPlaylist_ReturnsNull()
    {
        // Arrange - rule 6: every chip already has its verdict, so the seed is Failed
        // instead of queueing chips that would each ask again.
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B", "Chip C"] });
        harvest.Apply(new MpPlayEvent { Type = "chip", Label = "Chip A", Url = "" });
        harvest.Complete(failed: false);

        // Act
        var seed = await harvest.WaitForAsync(null, CancellationToken.None);

        // Assert
        Assert.Null(seed);
    }

    [Fact]
    public async Task WaitForAsync_Seed_GivesUpAfterProbeReadyWait()
    {
        // Arrange - rule 7: no chip list and no chip in 30s: the seed stops waiting.
        // Nothing is known about the page yet, so it is infrastructure (retry, then
        // Deferred), never a miss; the next ask on the same harvest gets the list.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new MpHarvestSession(clock);
        var seed = harvest.WaitForAsync(null, CancellationToken.None);
        Assert.False(seed.IsCompleted);
        Assert.Equal(Start + harvest.ProbeReadyWait, clock.NextDue);

        // Act
        clock.AdvanceToNext();
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => seed);

        // Assert
        Assert.Equal(LocalServiceUnavailableReason.CallTimeout, ex.Reason);
        Assert.True(ex.IsRetryable);
        Assert.Equal(harvest.ProbeReadyWait.TotalSeconds, clock.T, 3);
        Assert.False(harvest.IsCompleted);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A"] });
        var list = await harvest.WaitForAsync(null, CancellationToken.None);
        Assert.Equal(["Chip A"], list?.Streams);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_PlaylistOnly_GivesUpAfterProbeReadyWait()
    {
        // Arrange - rule 7: a chip whose playlist never gets probe-ready segments is
        // handed over as is 30s after its playlist-only line, not at the end of the harvest.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new MpHarvestSession(clock);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip A"));
        var named = harvest.WaitForAsync("Chip A", CancellationToken.None);
        Assert.False(named.IsCompleted);
        Assert.Equal(Start + harvest.ProbeReadyWait, clock.NextDue);

        // Act
        clock.AdvanceToNext();
        var chip = await named;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-A.m3u8", chip?.Url);
        Assert.Equal(harvest.ProbeReadyWait.TotalSeconds, clock.T, 3);
        Assert.True(harvest.IsChipReady("Chip A"));
    }

    [Fact]
    public void IsChipReady_PlaylistOnlyChip_SignalsChangedWhenItsDeadlinePasses()
    {
        // Arrange - the resolver only wakes on Changed; a chip that turns ready because
        // time passed must still wake it.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new MpHarvestSession(clock);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A"] });
        harvest.Apply(PlaylistOnly("Chip A"));
        var changed = harvest.Changed;

        // Act
        clock.AdvanceToNext();

        // Assert
        Assert.True(changed.IsCompleted);
        Assert.True(harvest.IsChipReady("Chip A"));
    }

    [Fact]
    public async Task PlaylistOnlyChip_HandedOverAtOnce_OnlyWhileNoBackupExists()
    {
        // Arrange - rule 7 (Jon, 13 Nov 2026): while no backup exists a playlist-only chip
        // is handed over at once; once a backup exists the 30s wait applies again.
        var clock = new Timeline.MilestoneClock(Start);
        var noBackup = false;
        var harvest = new MpHarvestSession(clock, handOverPlaylistOnlyNow: () => noBackup);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip A"));
        harvest.Apply(PlaylistOnly("Chip B"));
        var readyWithBackup = harvest.IsChipReady("Chip A");

        // Act
        noBackup = true;
        var readyWithoutBackup = harvest.IsChipReady("Chip A");
        var answer = await harvest.WaitForAsync("Chip B", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.False(readyWithBackup);
        Assert.True(readyWithoutBackup);
        Assert.Equal("https://stream.example.test/Chip-B.m3u8", answer?.Url);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_NotSent_ThrowsRetryableOnceTheHarvestStalls()
    {
        // Arrange - rule 7: a direct wait for a listed chip LocalService has not sent,
        // while the harvest sends nothing for 30s, fails as infrastructure (never a 330s
        // wait). The resolver does not start the row (not ready): it stays queued.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new MpHarvestSession(clock);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        var named = harvest.WaitForAsync("Chip B", CancellationToken.None);
        Assert.False(harvest.IsChipReady("Chip B"));

        // Act
        clock.AdvanceToNext();
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => named);

        // Assert
        Assert.Equal(LocalServiceUnavailableReason.CallTimeout, ex.Reason);
        Assert.True(ex.IsRetryable);
        Assert.Equal(harvest.ProbeReadyWait.TotalSeconds, clock.T, 3);
        Assert.False(harvest.IsChipReady("Chip B"));
    }

    [Fact]
    public async Task IsChipReady_ChipSentLongAfterAStall_TurnsReadyAndSignalsChanged()
    {
        // Arrange - rule 7: a chip LocalService sends 50s after the chip list, with
        // nothing in between, is picked up when it arrives (the resolver waits on Changed).
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new MpHarvestSession(clock);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        using var stall = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        while (clock.T < 45 && clock.NextDue is not null)
            clock.AdvanceToNext();
        Assert.False(harvest.IsChipReady("Chip B"));
        var changed = harvest.Changed;

        // Act
        harvest.Apply(ProbeReady("Chip B"));
        await changed.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(harvest.IsChipReady("Chip B"));
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_NotSent_WaitsWhileTheHarvestKeepsSendingChips()
    {
        // Arrange - a slow page (one chip every ~10s) is progressing, not stalled.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new MpHarvestSession(clock, probeReadyWait: TimeSpan.FromSeconds(30));
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B", "Chip C"] });
        var named = harvest.WaitForAsync("Chip C", CancellationToken.None);
        using var tick = clock.CreateTimer(_ => harvest.Apply(ProbeReady("Chip A")), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        using var tick2 = clock.CreateTimer(_ => harvest.Apply(ProbeReady("Chip B")), null, TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);
        using var tick3 = clock.CreateTimer(_ => harvest.Apply(ProbeReady("Chip C")), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);

        // Act
        while (!named.IsCompleted && clock.NextDue is not null && clock.T < 60)
        {
            clock.AdvanceToNext();
            await Task.Yield();
        }

        var chip = await named;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-C.m3u8", chip?.Url);
        Assert.Equal(60, clock.T, 3);
    }

    [Fact]
    public void Constructor_ProbeReadyWait_IsPerSession()
    {
        // Arrange
        var custom = TimeSpan.FromSeconds(12);

        // Act
        var harvest = new MpHarvestSession(probeReadyWait: custom);
        var other = new MpHarvestSession();

        // Assert
        Assert.Equal(custom, harvest.ProbeReadyWait);
        Assert.Equal(MpHarvestSession.DefaultProbeReadyWait, other.ProbeReadyWait);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_MissCompletesWithoutSoak()
    {
        // Arrange - definitive miss must complete named wait without soak/Complete.
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });

        // Act
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = ""
        });
        var miss = await waiting;

        // Assert
        Assert.True(string.IsNullOrWhiteSpace(miss?.Url));
        Assert.Equal("Chip B", miss?.SelectedStream);
        Assert.True(miss?.RewrittenSegments is null or { Count: 0 });
        Assert.False(harvest.IsCompleted);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_NonProbeRewritten_StillWaitsForSoak()
    {
        // Arrange - Count > 0 is not enough; URLs must look probe-shaped.
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = "https://stream.example.test/b.m3u8",
            RewrittenSegments = ["https://cdn.example.test/plain.ts"]
        });
        Assert.False(waiting.IsCompleted);

        // Act
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = "https://stream.example.test/b.m3u8",
            RewrittenSegments = ["https://cdn.example.test/b.exe?_s2=1"]
        });
        var second = await waiting;

        // Assert
        Assert.Equal(["https://cdn.example.test/b.exe?_s2=1"], second?.RewrittenSegments);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_CompletesWhenThatLineArrives()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(ProbeReady("Chip A"));
        Assert.False(waiting.IsCompleted);

        // Act
        harvest.Apply(ProbeReady("Chip B"));
        var second = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-B.m3u8", second?.Url);
        Assert.Equal("Chip B", second?.SelectedStream);
        Assert.Single(second!.RewrittenSegments!);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_WaitsForRewrittenAfterPlaylistOnlyRelease()
    {
        // Arrange - mirrors ReleaseChipThenSoak: playlist line, then soak enrich.
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip B"));
        Assert.False(waiting.IsCompleted);

        // Act
        harvest.Apply(ProbeReady("Chip B"));
        var second = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-B.m3u8", second?.Url);
        Assert.Equal(["https://cdn.example.test/Chip-B.exe?_s2=1"], second?.RewrittenSegments);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_CachedPlaylistOnly_StillWaitsForSoak()
    {
        // Arrange - sibling WaitForAsync starts after playlist-only was cached.
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip B"));
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        // Act
        harvest.Apply(ProbeReady("Chip B"));
        var second = await waiting;

        // Assert
        Assert.Single(second!.RewrittenSegments!);
    }

    [Fact]
    public async Task Complete_NamedWaiter_ReturnsPlaylistOnlyWhenSoakNeverEnriched()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(PlaylistOnly("Chip B"));
        Assert.False(waiting.IsCompleted);

        // Act
        harvest.Complete(failed: false);
        var second = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-B.m3u8", second?.Url);
        Assert.True(second?.RewrittenSegments is null or { Count: 0 });
    }

    [Fact]
    public async Task Complete_UnblocksMissingLabels()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);

        // Act
        harvest.Complete(failed: false);
        var result = await waiting;

        // Assert
        Assert.Null(result);
        Assert.True(harvest.IsCompleted);
        Assert.True(harvest.Listed.IsCompleted);
    }

    [Fact]
    public async Task Complete_EmptyDone_UnblocksFirstChipWait()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var waiting = harvest.WaitForAsync(null, timeout.Token);

        // Act
        harvest.Complete(failed: false);
        var first = await waiting;

        // Assert
        Assert.Null(first);
        Assert.True(harvest.IsCompleted);
        Assert.False(harvest.HasPlaylist);
    }

    [Fact]
    public void CanReuseFor_FinishedMiss_AnswersFromItsVerdictsForAWhile()
    {
        // Arrange - rule 6: chip rows that start after an all-miss harvest ended are
        // answered from it (no re-POST per chip); a later tap asks LocalService again.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new MpHarvestSession(clock);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Complete(failed: false);

        // Act
        var soon = harvest.CanReuseFor(Page, Page);
        using var later = clock.CreateTimer(_ => { }, null, MpHarvestSession.VerdictMemory, Timeout.InfiniteTimeSpan);
        clock.AdvanceToNext();
        var afterMemory = harvest.CanReuseFor(Page, Page);

        // Assert
        Assert.True(soon);
        Assert.True(harvest.IsChipReady("Chip B"));
        Assert.False(afterMemory);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_AfterComplete_ReturnsNull()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        harvest.Complete(failed: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        var result = await harvest.WaitForAsync("Chip B", timeout.Token);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Complete_RelayClosed_SetsErrorUntilAPlaylistArrives()
    {
        // Arrange
        var harvest = new MpHarvestSession();

        // Act
        harvest.Complete(failed: true, error: "The compute relay closed before a playlist arrived");

        // Assert
        Assert.False(harvest.HasPlaylist);
        Assert.Equal("The compute relay closed before a playlist arrived", harvest.Error);
    }

    [Fact]
    public void CanReuseFor_ProbeReadyOnly_RejectsACompletedMissOnTheSamePage()
    {
        // Arrange - remote compute keeps the old rule: a completed miss is never reused.
        var harvest = new MpHarvestSession(mode: MpSeedMode.ProbeReadyOnly);
        harvest.Complete(failed: true, error: "The compute relay closed before a playlist arrived");

        // Act
        var reuse = harvest.CanReuseFor(Page, Page);

        // Assert
        Assert.False(reuse);
    }

    [Fact]
    public void CanReuseFor_KeepsAnInFlightHarvestOnTheSamePage()
    {
        // Arrange
        var harvest = new MpHarvestSession();

        // Act
        var reuse = harvest.CanReuseFor(Page, Page);

        // Assert
        Assert.True(reuse);
    }

    [Fact]
    public void HasPlaylist_IsTrueAfterAChipUrl()
    {
        // Arrange
        var harvest = new MpHarvestSession();

        // Act
        harvest.Apply(PlaylistOnly("Chip A"));

        // Assert
        Assert.True(harvest.HasPlaylist);
        Assert.True(harvest.Listed.IsCompleted);
    }

    [Fact]
    public async Task Cancel_WaitersThrow_InsteadOfAFailedPlaylist()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip A", CancellationToken.None);
        var withChip = new MpHarvestSession();
        withChip.Apply(PlaylistOnly("Chip A"));

        // Act
        harvest.Cancel();
        withChip.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(harvest.CanReuseFor(Page, Page));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harvest.WaitForAsync(null, CancellationToken.None));
        Assert.True(withChip.HasPlaylist);
        Assert.False(withChip.CanReuseFor(Page, Page));
        Assert.True(harvest.Listed.IsCompleted);
    }
}
