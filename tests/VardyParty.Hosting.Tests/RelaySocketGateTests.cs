using System;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Hosting;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class RelaySocketGateTests
{
    [Fact]
    public async Task Dispose_LetsTheNextCallerOpenASocket()
    {
        // Arrange — a /play call holds the only guest socket until it closes.
        var gate = new RelaySocketGate();
        var first = await gate.AcquireAsync(CancellationToken.None);
        var waiting = gate.AcquireAsync(CancellationToken.None);

        // Act
        await Task.Delay(50);

        // Assert — the next stream cannot connect yet.
        Assert.False(waiting.IsCompleted);

        await first.DisposeAsync();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task HandOff_HoldsTheGateUntilTheHarvestSocketCloses()
    {
        // Arrange — /mp returns the first chip and keeps the socket for the harvest.
        var gate = new RelaySocketGate();
        var first = await gate.AcquireAsync(CancellationToken.None);
        var harvestClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = gate.AcquireAsync(CancellationToken.None);

        // Act — returning the first chip must not open a second socket.
        first.HandOff(harvestClosed.Task);
        await first.DisposeAsync();
        await Task.Delay(50);

        // Assert
        Assert.False(waiting.IsCompleted);

        harvestClosed.SetResult();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await second.DisposeAsync();
    }
}
