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
        // Arrange
        var gate = new RelaySocketGate();
        var first = await gate.AcquireAsync(CancellationToken.None);
        var waiting = gate.AcquireAsync(CancellationToken.None);

        // Act
        await first.DisposeAsync();

        // Assert
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

        // Act
        first.HandOff(harvestClosed.Task);

        // Assert
        Assert.False(waiting.IsCompleted);
        harvestClosed.SetResult();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task HandOff_FaultedTask_LetsTheNextAcquireComplete()
    {
        // Arrange
        var gate = new RelaySocketGate();
        var first = await gate.AcquireAsync(CancellationToken.None);
        var dropped = Task.FromException(new InvalidOperationException("socket dropped"));

        // Act
        first.HandOff(dropped);

        // Assert
        var second = await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_AfterHandOff_DoesNotThrow()
    {
        // Arrange
        var gate = new RelaySocketGate();
        var first = await gate.AcquireAsync(CancellationToken.None);
        var harvestClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = gate.AcquireAsync(CancellationToken.None);
        first.HandOff(harvestClosed.Task);

        // Act
        var disposing = first.DisposeAsync();

        // Assert
        await disposing;
        Assert.False(waiting.IsCompleted);
        harvestClosed.SetResult();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        var third = gate.AcquireAsync(CancellationToken.None);
        Assert.False(third.IsCompleted);
        await second.DisposeAsync();
        var released = await third.WaitAsync(TimeSpan.FromSeconds(2));
        await released.DisposeAsync();
    }
}
