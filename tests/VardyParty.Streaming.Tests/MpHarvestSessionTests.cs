using System;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public sealed class MpHarvestSessionTests
{
    private static LocalServiceUnavailableException Fault() =>
        new(LocalServiceUnavailableReason.ServiceError, "POST /mp returned 500");

    [Fact]
    public async Task Fail_WaitersWithoutPlaylist_ThrowInfrastructure_NotNull()
    {
        var harvest = new MpHarvestSession();
        var seed = harvest.WaitForAsync(null, CancellationToken.None);
        var named = harvest.WaitForAsync("Chip B", CancellationToken.None);

        harvest.Fail(Fault());

        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => seed);
        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => named);
        Assert.True(harvest.IsCompleted);
        Assert.NotNull(harvest.Fault);
        // A failed harvest is not reused: the next row asks the service again.
        Assert.False(harvest.CanReuseFor("https://page.example.test", "https://page.example.test"));
    }

    [Fact]
    public async Task Fail_LabelWithCachedPlaylist_StillGetsIt_LateWaitersToo()
    {
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8",
            RewrittenSegments = ["https://cdn.example.test/a.exe?_s2=1"]
        });

        harvest.Fail(Fault());

        Assert.Equal("https://stream.example.test/a.m3u8", (await harvest.WaitForAsync("Chip A", CancellationToken.None))?.Url);
        await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => harvest.WaitForAsync("Chip B", CancellationToken.None));
    }

    [Fact]
    public async Task ErrorEvent5xx_FailsAsInfrastructure_4xxIsAMiss()
    {
        var infra = new MpHarvestSession();
        infra.Apply(new MpPlayEvent { Type = "error", StatusCode = 500, Error = "mp play execution failed" });
        var ex = await Assert.ThrowsAsync<LocalServiceUnavailableException>(() => infra.WaitForAsync("Chip A", CancellationToken.None));
        Assert.Equal(LocalServiceUnavailableReason.ServiceError, ex.Reason);

        var miss = new MpHarvestSession();
        miss.Apply(new MpPlayEvent { Type = "error", StatusCode = 404, Error = "M3U8 not captured from MP page." });
        Assert.Null(await miss.WaitForAsync("Chip A", CancellationToken.None));
        Assert.Null(miss.Fault);
    }

    [Fact]
    public void Fail_AfterDone_IsIgnored()
    {
        var harvest = new MpHarvestSession();
        harvest.Complete(failed: false);

        harvest.Fail(Fault());

        Assert.Null(harvest.Fault);
    }

    [Fact]
    public async Task WaitForAsync_ReturnsFirstChipWithoutWaitingForLaterLabels()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"], SelectedStream = "Chip A" });
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8",
            RewrittenSegments = ["https://cdn.example.test/a.exe?_s2=1"]
        });

        // Act
        var first = await harvest.WaitForAsync(null, CancellationToken.None);

        // Assert
        Assert.Equal("https://stream.example.test/a.m3u8", first?.Url);
        Assert.Equal("Chip A", first?.SelectedStream);
        Assert.Equal(["Chip A", "Chip B"], first?.Streams);
        Assert.False(harvest.IsCompleted);
    }

    [Fact]
    public async Task WaitForAsync_FirstChip_WaitsForRewrittenAfterPlaylistOnlyRelease()
    {
        // Arrange — seed WaitForAsync(null) must not complete on playlist-only.
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync(null, CancellationToken.None);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8"
        });
        Assert.False(waiting.IsCompleted);

        // Act
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8",
            RewrittenSegments = ["https://cdn.example.test/a.exe?_s2=1"]
        });
        var first = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/a.m3u8", first?.Url);
        Assert.Equal(["https://cdn.example.test/a.exe?_s2=1"], first?.RewrittenSegments);
    }

    [Fact]
    public async Task WaitForAsync_FirstChip_OtherLabelMiss_DoesNotFinishSeed()
    {
        // Arrange — Chip A playlist-only, then Chip B miss must not complete seed.
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync(null, CancellationToken.None);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8"
        });
        Assert.False(waiting.IsCompleted);

        // Act
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = ""
        });

        // Assert
        Assert.False(waiting.IsCompleted);

        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8",
            RewrittenSegments = ["https://cdn.example.test/a.exe?_s2=1"]
        });
        var first = await waiting;
        Assert.Equal("https://stream.example.test/a.m3u8", first?.Url);
        Assert.Equal("Chip A", first?.SelectedStream);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_MissCompletesWithoutSoak()
    {
        // Arrange — definitive miss must complete named wait without soak/Complete.
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
        // Arrange — Count > 0 is not enough; URLs must look probe-shaped.
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
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8",
            RewrittenSegments = ["https://cdn.example.test/a.exe?_s2=1"]
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
        Assert.Equal("https://stream.example.test/b.m3u8", second?.Url);
        Assert.Equal("Chip B", second?.SelectedStream);
        Assert.Single(second!.RewrittenSegments!);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_WaitsForRewrittenAfterPlaylistOnlyRelease()
    {
        // Arrange — mirrors ReleaseChipThenSoak: playlist line, then soak enrich.
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = "https://stream.example.test/b.m3u8"
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
        Assert.Equal("https://stream.example.test/b.m3u8", second?.Url);
        Assert.Equal(["https://cdn.example.test/b.exe?_s2=1"], second?.RewrittenSegments);
    }

    [Fact]
    public async Task WaitForAsync_NamedChip_CachedPlaylistOnly_StillWaitsForSoak()
    {
        // Arrange — sibling WaitForAsync starts after playlist-only was cached.
        var harvest = new MpHarvestSession();
        harvest.Apply(new MpPlayEvent { Type = "chips", Streams = ["Chip A", "Chip B"] });
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = "https://stream.example.test/b.m3u8"
        });
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
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
        Assert.Single(second!.RewrittenSegments!);
    }

    [Fact]
    public async Task Complete_NamedWaiter_ReturnsPlaylistOnlyWhenSoakNeverEnriched()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip B", CancellationToken.None);
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip B",
            Url = "https://stream.example.test/b.m3u8"
        });
        Assert.False(waiting.IsCompleted);

        // Act
        harvest.Complete(failed: false);
        var second = await waiting;

        // Assert
        Assert.Equal("https://stream.example.test/b.m3u8", second?.Url);
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
    public void Complete_EmptyDone_RejectsReuseOnTheSamePage()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        harvest.Complete(failed: false);
        const string page = "https://page.example.test/match/1";

        // Act
        var reuse = harvest.CanReuseFor(page, page);

        // Assert
        Assert.False(reuse);
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
    public void CanReuseFor_RejectsACompletedMissOnTheSamePage()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        harvest.Complete(failed: true, error: "The compute relay closed before a playlist arrived");
        const string page = "https://page.example.test/match/1";

        // Act
        var reuse = harvest.CanReuseFor(page, page);

        // Assert
        Assert.False(reuse);
    }

    [Fact]
    public void CanReuseFor_KeepsAnInFlightHarvestOnTheSamePage()
    {
        // Arrange
        var harvest = new MpHarvestSession();
        const string page = "https://page.example.test/match/1";

        // Act
        var reuse = harvest.CanReuseFor(page, page);

        // Assert
        Assert.True(reuse);
    }

    [Fact]
    public void HasPlaylist_IsTrueAfterAChipUrl()
    {
        // Arrange
        var harvest = new MpHarvestSession();

        // Act
        harvest.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8"
        });

        // Assert
        Assert.True(harvest.HasPlaylist);
    }

    [Fact]
    public async Task Cancel_WaitersThrow_InsteadOfAFailedPlaylist()
    {
        var harvest = new MpHarvestSession();
        var waiting = harvest.WaitForAsync("Chip A", CancellationToken.None);

        harvest.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(harvest.CanReuseFor("https://page.example.test/match", "https://page.example.test/match"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harvest.WaitForAsync(null, CancellationToken.None));

        var withChip = new MpHarvestSession();
        withChip.Apply(new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8"
        });
        Assert.True(withChip.HasPlaylist);
        withChip.Cancel();
        Assert.False(withChip.CanReuseFor(
            "https://page.example.test/match",
            "https://page.example.test/match"));
    }
}
