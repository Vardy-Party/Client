using System;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public sealed class MpHarvestSessionTests
{
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
            Url = "https://stream.example.test/a.m3u8"
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
