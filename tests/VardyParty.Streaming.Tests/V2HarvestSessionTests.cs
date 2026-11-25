using System;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public sealed class V2HarvestSessionTests
{
    private const string Page = "https://page.example.test/match/1";

    private static readonly DateTimeOffset Start = new(2026, 11, 12, 20, 12, 25, TimeSpan.Zero);

    private static LocalServiceUnavailableException Fault() =>
        new(LocalServiceUnavailableReason.ServiceError, "POST /mp returned 500");

    private static V2PlayEvent PlaylistOnly(string label) => new()
    {
        Type = "chip",
        Label = label,
        Url = $"https://stream.example.test/{label.Replace(' ', '-')}.m3u8"
    };

    private static V2PlayEvent ProbeReady(string label) => new()
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
        var harvest = new V2HarvestSession();
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
        var harvest = new V2HarvestSession();
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
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
        var harvest = new V2HarvestSession();
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
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
        var infra = new V2HarvestSession();

        // Act
        infra.Apply(new V2PlayEvent { Type = "error", StatusCode = 500, Error = "mp play execution failed" });

        // Assert
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => infra.WaitForAsync("Chip A", CancellationToken.None));
        Assert.Equal(LocalServiceUnavailableReason.ServiceError, ex.Reason);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task ErrorEvent4xx_IsAMiss()
    {
        // Arrange
        var miss = new V2HarvestSession();

        // Act
        miss.Apply(new V2PlayEvent { Type = "error", StatusCode = 404, Error = "M3U8 not captured from MP page." });

        // Assert
        Assert.Null(await miss.WaitForAsync("Chip A", CancellationToken.None));
        Assert.Null(miss.Fault);
    }

    [Fact]
    public void Fail_AfterDone_IsIgnored()
    {
        // Arrange
        var harvest = new V2HarvestSession();
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
        var harvest = new V2HarvestSession();
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"], SelectedStream = "Chip A" });
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
        var harvest = new V2HarvestSession();
        var seed = harvest.WaitForAsync(null, CancellationToken.None);

        // Act
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        var list = await seed;

        // Assert
        Assert.Equal("", list?.Url);
        Assert.Equal(["Chip A", "Chip B"], list?.Streams);
        Assert.False(harvest.IsCompleted);
        Assert.True(harvest.Listed.IsCompleted);
    }


    [Fact]
    public async Task WaitForAsync_Seed_FirstChipWithoutList_ReturnsThatChipAsAList()
    {
        // Arrange - rule 5: with no chips event, the first chip releases the seed.
        var harvest = new V2HarvestSession();
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
        var harvest = new V2HarvestSession();
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(new V2PlayEvent { Type = "chip", Label = "Chip B", Url = "" });

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
        var withPlaylist = new V2HarvestSession();
        withPlaylist.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
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
        var harvest = new V2HarvestSession();
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B", "Chip C"] });
        harvest.Apply(new V2PlayEvent { Type = "chip", Label = "Chip A", Url = "" });
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
        var harvest = new V2HarvestSession(clock);
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
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A"] });
        var list = await harvest.WaitForAsync(null, CancellationToken.None);
        Assert.Equal(["Chip A"], list?.Streams);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_PlaylistOnly_IsReadyImmediately()
    {
        // Arrange - a captured MP playlist is probe-ready without rewritten segments.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new V2HarvestSession(clock);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip A"));

        // Act
        var chip = await harvest.WaitForAsync("Chip A", CancellationToken.None);

        // Assert
        Assert.Equal("https://stream.example.test/Chip-A.m3u8", chip?.Url);
        Assert.Equal(0, clock.T, 3);
        Assert.True(harvest.IsChipReady("Chip A"));
        Assert.Equal("probe-ready", harvest.HandoverOf("Chip A"));
    }

    [Fact]
    public void IsChipReady_PlaylistOnlyChip_IsReadyAsSoonAsThePlaylistArrives()
    {
        // Arrange
        var harvest = new V2HarvestSession();
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A"] });

        // Act
        harvest.Apply(PlaylistOnly("Chip A"));

        // Assert
        Assert.True(harvest.IsChipReady("Chip A"));
    }

    [Fact]
    public async Task PlaylistOnlyChip_HandedOverAtOnce_OnlyWhileNoBackupExists()
    {
        // Arrange - a captured playlist is ready whether or not a backup exists.
        var clock = new Timeline.MilestoneClock(Start);
        var noBackup = false;
        var harvest = new V2HarvestSession(clock, handOverPlaylistOnlyNow: () => noBackup);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip A"));
        harvest.Apply(PlaylistOnly("Chip B"));
        var readyWithBackup = harvest.IsChipReady("Chip A");

        // Act
        noBackup = true;
        var readyWithoutBackup = harvest.IsChipReady("Chip A");
        var answer = await harvest.WaitForAsync("Chip B", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.True(readyWithBackup);
        Assert.True(readyWithoutBackup);
        Assert.Equal("https://stream.example.test/Chip-B.m3u8", answer?.Url);
        Assert.Equal("probe-ready", harvest.HandoverOf("Chip B"));
    }

    [Fact]
    public async Task PlaylistOnlyChip_IsNotOnTheEarlyHandoverPath()
    {
        // Arrange - a captured playlist is probe-ready, so it is not handed over
        // as an early chip and does not get the early-chip retry.
        var harvest = new V2HarvestSession(handOverPlaylistOnlyNow: () => true);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A"] });
        harvest.Apply(PlaylistOnly("Chip A"));

        // Act
        var answer = await harvest.WaitForAsync("Chip A", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal("https://stream.example.test/Chip-A.m3u8", answer?.Url);
        Assert.Equal("probe-ready", harvest.HandoverOf("Chip A"));
        Assert.True(harvest.IsChipReady("Chip A"));
        Assert.False(harvest.RetryEarlyChip("Chip A", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ChipHandedOverAtTheNormalTime_GetsNoRetry()
    {
        // Arrange - a captured playlist is handed over at once; that verdict stands (no retry).
        var harvest = new V2HarvestSession(handOverPlaylistOnlyNow: () => false);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip A"));
        harvest.Apply(ProbeReady("Chip B"));
        await harvest.WaitForAsync("Chip A", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await harvest.WaitForAsync("Chip B", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        // Act
        var retryA = harvest.RetryEarlyChip("Chip A", TimeSpan.FromSeconds(10));
        var retryB = harvest.RetryEarlyChip("Chip B", TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal("probe-ready", harvest.HandoverOf("Chip A"));
        Assert.Equal("probe-ready", harvest.HandoverOf("Chip B"));
        Assert.False(retryA);
        Assert.False(retryB);
    }

    [Fact]
    public async Task EarlyChipRetry_DoesNotApplyToACapturedPlaylist()
    {
        // Arrange
        var harvest = new V2HarvestSession(handOverPlaylistOnlyNow: () => true);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A"] });
        harvest.Apply(PlaylistOnly("Chip A"));
        await harvest.WaitForAsync("Chip A", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        // Act
        harvest.Complete(failed: false);

        // Assert
        Assert.True(harvest.IsChipReady("Chip A"));
        Assert.False(harvest.RetryEarlyChip("Chip A", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_NotSent_ThrowsRetryableOnceTheHarvestStalls()
    {
        // Arrange - rule 7: a direct wait for a listed chip LocalService has not sent,
        // while the harvest sends nothing for 30s, fails as infrastructure (never a 330s
        // wait). The resolver does not start the row (not ready): it stays queued.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new V2HarvestSession(clock);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
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
    public async Task WaitForAsync_NamedChip_JoiningAfterASilence_GetsItsOwnWait_NotAnInstantFailure()
    {
        // Arrange - TV log 13 Nov 2026 18:41:08: NW IT and TD Arabic joined a harvest that
        // had already been silent for 30s and failed within 1ms ("sent nothing for 30s").
        // A late joiner must wait its own ProbeReadyWait from when it asked.
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new V2HarvestSession(clock);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        using var silence = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        while (clock.T < 45 && clock.NextDue is not null)
            clock.AdvanceToNext();

        // Act
        var named = harvest.WaitForAsync("Chip B", CancellationToken.None);
        await Task.Yield();

        // Assert - not failed on arrival; fails only once it has itself waited ProbeReadyWait.
        Assert.False(named.IsCompleted);
        while (!named.IsCompleted && clock.NextDue is not null)
        {
            clock.AdvanceToNext();
            await Task.Yield();
        }

        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => named);
        Assert.Equal(LocalServiceUnavailableReason.CallTimeout, ex.Reason);
        Assert.Equal(45 + harvest.ProbeReadyWait.TotalSeconds, clock.T, 3);
    }

    [Fact]
    public async Task IsChipReady_ChipSentLongAfterAStall_TurnsReadyAndSignalsChanged()
    {
        // Arrange - rule 7: a chip LocalService sends 50s after the chip list, with
        // nothing in between, is picked up when it arrives (the resolver waits on Changed).
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new V2HarvestSession(clock);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
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
        var harvest = new V2HarvestSession(clock, probeReadyWait: TimeSpan.FromSeconds(30));
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B", "Chip C"] });
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
    public void Constructor_Defaults_Are45sSeedWaitAnd90sHardCap()
    {
        // Arrange / Act
        var harvest = new V2HarvestSession();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(45), V2HarvestSession.DefaultProbeReadyWait);
        Assert.Equal(TimeSpan.FromSeconds(90), V2HarvestSession.DefaultHarvestHardCap);
        Assert.Equal(V2HarvestSession.DefaultProbeReadyWait, harvest.ProbeReadyWait);
    }

    [Fact]
    public void IsChipReady_ListedButUnsent_IsNotReady()
    {
        // Arrange
        var harvest = new V2HarvestSession();

        // Act
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A"] });

        // Assert
        Assert.False(harvest.IsChipReady("Chip A"));
        Assert.Null(harvest.HandoverOf("Chip A"));
    }

    [Fact]
    public void Constructor_ProbeReadyWait_IsPerSession()
    {
        // Arrange
        var custom = TimeSpan.FromSeconds(12);

        // Act
        var harvest = new V2HarvestSession(probeReadyWait: custom);
        var other = new V2HarvestSession();

        // Assert
        Assert.Equal(custom, harvest.ProbeReadyWait);
        Assert.Equal(V2HarvestSession.DefaultProbeReadyWait, other.ProbeReadyWait);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_MissCompletesWithoutSoak()
    {
        // Arrange - definitive miss must complete named wait without soak/Complete.
        var harvest = new V2HarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });

        // Act
        harvest.Apply(new V2PlayEvent
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
        Assert.True(harvest.IsChipReady("Chip B"));
        Assert.Equal("miss", harvest.HandoverOf("Chip B"));
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_PlaylistWithNonProbeRewritten_IsReadyFromTheUrl()
    {
        // Arrange - a captured playlist is probe-ready even when rewritten samples
        // are not probe-shaped (plain .ts is not HasProbeReady).
        var harvest = new V2HarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new V2PlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = "https://stream.example.test/b.m3u8",
            RewrittenSegments = ["https://cdn.example.test/plain.ts"]
        });

        // Act
        var chip = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/b.m3u8", chip?.Url);
        Assert.Equal(["https://cdn.example.test/plain.ts"], chip?.RewrittenSegments);
        Assert.True(harvest.IsChipReady("Chip B"));
        Assert.Equal("probe-ready", harvest.HandoverOf("Chip B"));
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_EmptyUrlWithProbeShapedRewritten_IsProbeReadyNotMiss()
    {
        // Arrange - empty URL would also match IsHarvestedMiss; probe-shaped
        // rewritten segments must win and hand over as probe-ready.
        var harvest = new V2HarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new V2PlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = "",
            RewrittenSegments = ["https://seg.example.test/live/Chip-B/1.ts?_s2=a"]
        });

        // Act
        var chip = await waiting;

        // Assert
        Assert.True(string.IsNullOrWhiteSpace(chip?.Url));
        Assert.Equal(["https://seg.example.test/live/Chip-B/1.ts?_s2=a"], chip?.RewrittenSegments);
        Assert.True(harvest.IsChipReady("Chip B"));
        Assert.Equal("probe-ready", harvest.HandoverOf("Chip B"));
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_CompletesWhenThatLineArrives()
    {
        // Arrange
        var harvest = new V2HarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
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
    public async Task WaitForAsync_NamedChip_PlaylistOnlyRelease_IsReadyWithoutRewritten()
    {
        // Arrange - mirrors ReleaseChipThenSoak: playlist line, then soak enrich.
        var harvest = new V2HarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip B"));

        // Act
        var second = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-B.m3u8", second?.Url);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_CachedPlaylistOnly_IsReadyWithoutSoak()
    {
        // Arrange - sibling WaitForAsync starts after playlist-only was cached.
        var harvest = new V2HarvestSession();
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(PlaylistOnly("Chip B"));
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);

        // Act
        var second = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-B.m3u8", second?.Url);
    }

    [Fact]
    public async Task Complete_NamedWaiter_ReturnsPlaylistOnlyWhenSoakNeverEnriched()
    {
        // Arrange
        var harvest = new V2HarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(PlaylistOnly("Chip B"));

        // Act
        var second = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/Chip-B.m3u8", second?.Url);
        Assert.True(second?.RewrittenSegments is null or { Count: 0 });
    }

    [Fact]
    public async Task Complete_UnblocksMissingLabels()
    {
        // Arrange
        var harvest = new V2HarvestSession();
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
        var harvest = new V2HarvestSession();
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
        var harvest = new V2HarvestSession(clock);
        harvest.Apply(new V2PlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Complete(failed: false);

        // Act
        var soon = harvest.CanReuseFor(Page, Page);
        using var later = clock.CreateTimer(_ => { }, null, V2HarvestSession.VerdictMemory, Timeout.InfiniteTimeSpan);
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
        var harvest = new V2HarvestSession();
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
        var harvest = new V2HarvestSession();

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
        var harvest = new V2HarvestSession(mode: V2SeedMode.ProbeReadyOnly);
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
        var harvest = new V2HarvestSession();

        // Act
        var reuse = harvest.CanReuseFor(Page, Page);

        // Assert
        Assert.True(reuse);
    }

    [Fact]
    public void HasPlaylist_IsTrueAfterAChipUrl()
    {
        // Arrange
        var harvest = new V2HarvestSession();

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
        var harvest = new V2HarvestSession();
        var waiting = harvest.WaitForAsync("Chip A", CancellationToken.None);
        var withChip = new V2HarvestSession();
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

    [Fact]
    public async Task WaitForAsync_Seed_CaptchaCleared_ResetsClockWithinHardCap()
    {
        // Arrange
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new V2HarvestSession(clock);
        var seed = harvest.WaitForAsync(null, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(20));
        harvest.Apply(new V2PlayEvent { Type = V2PlayEvent.TypeCaptchaCleared });
        Assert.False(seed.IsCompleted);

        // Act - a 45s clock after a clear at 20s is still under the 90s cap.
        // Signal wakes the seed so it can re-arm; yield until that new timer
        // (t+65) is registered, then fire it. Do not spin AdvanceToNext while
        // no timer is due: that exits and leaves the seed waiting forever.
        var due = Start + TimeSpan.FromSeconds(65);
        for (var i = 0; i < 50 && !seed.IsCompleted && clock.NextDue != due; i++)
            await Task.Yield();
        Assert.Equal(due, clock.NextDue);
        clock.AdvanceToNext();
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => seed);

        // Assert
        Assert.Equal(LocalServiceUnavailableReason.CallTimeout, ex.Reason);
        Assert.Equal(65, clock.T, 3);
        Assert.False(harvest.IsCompleted);
    }

    [Fact]
    public async Task WaitForAsync_Seed_CaptchaCleared_IsCappedFromHarvestStart()
    {
        // Arrange
        var clock = new Timeline.MilestoneClock(Start);
        var harvest = new V2HarvestSession(clock, harvestHardCap: TimeSpan.FromSeconds(50));
        var seed = harvest.WaitForAsync(null, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(20));
        harvest.Apply(new V2PlayEvent { Type = V2PlayEvent.TypeCaptchaCleared });

        // Act - a fresh 45s clock would end at 65s, but the 50s cap wins (t+50).
        var due = Start + TimeSpan.FromSeconds(50);
        for (var i = 0; i < 50 && !seed.IsCompleted && clock.NextDue != due; i++)
            await Task.Yield();
        Assert.Equal(due, clock.NextDue);
        clock.AdvanceToNext();
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => seed);

        // Assert
        Assert.Equal(LocalServiceUnavailableReason.CallTimeout, ex.Reason);
        Assert.Equal(50, clock.T, 3);
    }

    [Fact]
    public async Task Apply_CaptchaUnsolved_503_CompletesAsMissNotInfrastructure()
    {
        // Arrange
        var harvest = new V2HarvestSession();
        var seed = harvest.WaitForAsync(null, CancellationToken.None);

        // Act
        harvest.Apply(new V2PlayEvent
        {
            Type = "error",
            StatusCode = 503,
            Error = "MP blocked by captcha (EdgeOne): still challenged",
            Reason = V2PlayEvent.ReasonCaptchaUnsolved
        });

        // Assert
        Assert.Null(await seed);
        Assert.True(harvest.IsCompleted);
        Assert.Null(harvest.Fault);
        Assert.Equal(V2PlayEvent.ReasonCaptchaUnsolved, harvest.Reason);
    }

    [Fact]
    public async Task Apply_Plain503_IsStillInfrastructure()
    {
        // Arrange
        var harvest = new V2HarvestSession();
        var seed = harvest.WaitForAsync(null, CancellationToken.None);

        // Act
        harvest.Apply(new V2PlayEvent { Type = "error", StatusCode = 503, Error = "mp play execution failed" });

        // Assert
        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => seed);
        Assert.NotNull(harvest.Fault);
    }

    [Fact]
    public async Task Apply_NoPlayer_404_CompletesAsMiss()
    {
        // Arrange
        var harvest = new V2HarvestSession();
        var seed = harvest.WaitForAsync(null, CancellationToken.None);

        // Act
        harvest.Apply(new V2PlayEvent
        {
            Type = "error",
            StatusCode = 404,
            Error = "MP page has no player.",
            Reason = V2PlayEvent.ReasonNoPlayer
        });

        // Assert
        Assert.Null(await seed);
        Assert.True(harvest.IsCompleted);
        Assert.Equal(V2PlayEvent.ReasonNoPlayer, harvest.Reason);
    }
}
