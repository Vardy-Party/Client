using System;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class ResolverRaceThrottleTests
{
    [Fact]
    public async Task Enter_CapsConcurrentPlaysAtTwo()
    {
        // Arrange
        var throttle = new ResolverRaceThrottle();
        var first = await throttle.EnterAsync(CancellationToken.None);
        var second = await throttle.EnterAsync(CancellationToken.None);
        var thirdEntered = false;
        var third = throttle.EnterAsync(CancellationToken.None).ContinueWith(
            _ => thirdEntered = true,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Act
        await Task.Delay(50);

        // Assert
        Assert.False(thirdEntered);
        first.Dispose();
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(thirdEntered);
        second.Dispose();
    }

    [Fact]
    public async Task NonRaceEnter_WaitsUntilTheRaceEnds_WhileRaceLegsTakeTheSlots()
    {
        // Arrange
        var throttle = new ResolverRaceThrottle();
        var race = throttle.BeginRace();
        IDisposable? leg;
        using (throttle.MarkRaceLeg())
            leg = await throttle.EnterAsync(CancellationToken.None);
        var entered = false;

        // Act
        var waiting = throttle.EnterAsync(CancellationToken.None).ContinueWith(
            _ => entered = true,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await Task.Delay(50);

        // Assert
        Assert.False(entered);
        leg.Dispose();
        race.Dispose();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(entered);
    }

    [Fact]
    public async Task DrainOthers_WaitsForPlaysThatStartedBeforeTheRace()
    {
        // Arrange
        var throttle = new ResolverRaceThrottle();
        var held = await throttle.EnterAsync(CancellationToken.None);
        var race = throttle.BeginRace();
        var drained = false;

        // Act
        var waiting = throttle.DrainOthersAsync(CancellationToken.None).ContinueWith(
            _ => drained = true,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await Task.Delay(50);

        // Assert
        Assert.False(drained);
        held.Dispose();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(drained);
        race.Dispose();
    }

    [Fact]
    public void BeginRace_MarksRaceInFlight_UntilDisposed()
    {
        var throttle = new ResolverRaceThrottle();
        Assert.False(throttle.RaceInFlight);
        var hold = throttle.BeginRace();
        Assert.True(throttle.RaceInFlight);
        hold.Dispose();
        Assert.False(throttle.RaceInFlight);
    }

    [Fact]
    public void BeginPlaybackPriority_PausesFindPhase_SameAsRaceHold()
    {
        var throttle = new ResolverRaceThrottle();
        Assert.False(throttle.RaceInFlight);
        var hold = throttle.BeginPlaybackPriority();
        Assert.True(throttle.RaceInFlight);
        hold.Dispose();
        Assert.False(throttle.RaceInFlight);
    }

    [Fact]
    public void SetBackupWanted_WakesWaitersOnlyWhenTheValueChanges()
    {
        // Arrange
        var throttle = new ResolverRaceThrottle();
        var first = throttle.WaitForBackupWantedChangeAsync(CancellationToken.None);

        // Act
        throttle.SetBackupWanted(false);
        var unchanged = first.IsCompleted;
        throttle.SetBackupWanted(true);

        // Assert
        Assert.False(unchanged);
        Assert.True(first.IsCompleted);
        Assert.True(throttle.BackupWanted);
        Assert.False(throttle.WaitForBackupWantedChangeAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public void PlaybackPriorityTimeout_ComesFromFirstPlayOpenStrategy()
    {
        Assert.Equal(
            VardyParty.Kernel.FirstPlayOpenStrategy.PlaybackPriorityTimeout,
            ResolverRaceThrottle.PlaybackPriorityTimeout);
    }
}
