using System;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class LocalServiceEndpointCacheTests
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ParallelLookups_ShareOneDiscovery_SuccessIsNotClearedByALoser()
    {
        var cache = new LocalServiceEndpointCache(SuccessTtl, FailureBackoff);
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;

        Task<string?> Discover()
        {
            Interlocked.Increment(ref started);
            return release.Task.ContinueWith(_ => (string?)"http://10.0.0.8:5070");
        }

        var first = cache.LookupOrJoin(now, Discover);
        var second = cache.LookupOrJoin(now, Discover);

        Assert.Equal(EndpointLookupKind.InFlight, first.Kind);
        Assert.Equal(EndpointLookupKind.InFlight, second.Kind);
        Assert.Equal(1, Volatile.Read(ref started));

        release.TrySetResult();
        Assert.Equal("http://10.0.0.8:5070", await first.Flight!);
        Assert.Equal("http://10.0.0.8:5070", await second.Flight!);

        var third = cache.LookupOrJoin(DateTimeOffset.UtcNow, () =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult<string?>(null);
        });

        Assert.Equal(EndpointLookupKind.Cached, third.Kind);
        Assert.Equal("http://10.0.0.8:5070", third.Url);
        Assert.Equal(1, Volatile.Read(ref started));
        Assert.True(cache.HasFresh(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task InFlightFailure_IsShared_AndBackoffSkipsAnotherProbe()
    {
        var cache = new LocalServiceEndpointCache(SuccessTtl, FailureBackoff);
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;

        Task<string?> Discover()
        {
            Interlocked.Increment(ref started);
            return release.Task.ContinueWith(_ => (string?)null);
        }

        var first = cache.LookupOrJoin(now, Discover);
        var joined = cache.LookupOrJoin(now, Discover);
        Assert.Equal(1, Volatile.Read(ref started));
        Assert.Same(first.Flight, joined.Flight);

        release.TrySetResult();
        Assert.Null(await first.Flight!);
        Assert.Null(await joined.Flight!);

        var backedOff = cache.LookupOrJoin(DateTimeOffset.UtcNow, () =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult<string?>("http://should-not-run");
        });
        Assert.Equal(EndpointLookupKind.Backoff, backedOff.Kind);
        Assert.Equal(1, Volatile.Read(ref started));

        var later = cache.LookupOrJoin(DateTimeOffset.UtcNow.AddSeconds(6), () =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult<string?>("http://10.0.0.9:1");
        });
        Assert.Equal(EndpointLookupKind.InFlight, later.Kind);
        Assert.Equal("http://10.0.0.9:1", await later.Flight!);
        Assert.Equal(2, Volatile.Read(ref started));
    }

    [Fact]
    public async Task CancelledProbe_DoesNotArmFailureBackoff()
    {
        var cache = new LocalServiceEndpointCache(SuccessTtl, FailureBackoff);
        var cancelled = cache.LookupOrJoin(
            DateTimeOffset.UtcNow,
            () => Task.FromException<string?>(new OperationCanceledException()));

        Assert.Null(await cancelled.Flight!);

        var next = cache.LookupOrJoin(
            DateTimeOffset.UtcNow,
            () => Task.FromResult<string?>("http://10.0.0.3:9"));
        Assert.Equal(EndpointLookupKind.InFlight, next.Kind);
        Assert.Equal("http://10.0.0.3:9", await next.Flight!);
    }

    [Fact]
    public async Task LastWaiterCancel_ProbeCompletesNull_NextLookupIsInFlightNotBackoff()
    {
        var cache = new LocalServiceEndpointCache(SuccessTtl, FailureBackoff);
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCts = new CancellationTokenSource();
        using var joinerCts = new CancellationTokenSource();

        var first = cache.LookupOrJoin(DateTimeOffset.UtcNow, firstCts.Token, token =>
        {
            probeStarted.TrySetResult();
            return Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (string?)null;
                }

                return (string?)null;
            });
        });
        var joined = cache.LookupOrJoin(
            DateTimeOffset.UtcNow,
            joinerCts.Token,
            _ => throw new InvalidOperationException("joiner must not start another probe"));

        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(first.Flight, joined.Flight);

        firstCts.Cancel();
        await Task.Delay(50);
        Assert.False(first.Flight!.IsCompleted);

        joinerCts.Cancel();
        Assert.Null(await first.Flight.WaitAsync(TimeSpan.FromSeconds(5)));

        var next = cache.LookupOrJoin(
            DateTimeOffset.UtcNow,
            () => Task.FromResult<string?>("http://10.0.0.4:1"));
        Assert.Equal(EndpointLookupKind.InFlight, next.Kind);
        Assert.Equal("http://10.0.0.4:1", await next.Flight!);
    }

    [Fact]
    public async Task SuccessTtl_ExpiresAt120Seconds_ThenServesLastGoodWhileReprobing()
    {
        var cache = new LocalServiceEndpointCache(SuccessTtl, FailureBackoff);
        var now = DateTimeOffset.UtcNow;
        cache.Remember("http://10.0.0.2:5070/", now);

        var fresh = cache.LookupOrJoin(now.AddSeconds(119), () => throw new InvalidOperationException("probe"));
        Assert.Equal(EndpointLookupKind.Cached, fresh.Kind);
        Assert.Equal("http://10.0.0.2:5070", fresh.Url);

        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired = cache.LookupOrJoin(now.AddSeconds(121), () =>
        {
            probed.TrySetResult();
            return release.Task.ContinueWith(_ => (string?)"http://10.1.1.1:1");
        });

        // Stale-while-revalidate: the caller keeps the known-good endpoint and a
        // background probe runs.
        Assert.Equal(EndpointLookupKind.Stale, expired.Kind);
        Assert.Equal("http://10.0.0.2:5070", expired.Url);
        await probed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // When the probe finds the service moved, the new endpoint wins.
        release.TrySetResult();
        for (var i = 0; i < 50 && cache.LastGood != "http://10.1.1.1:1"; i++)
            await Task.Delay(10);
        Assert.Equal("http://10.1.1.1:1", cache.LastGood);
    }

    [Fact]
    public async Task FailedReprobe_KeepsLastGood_InsteadOfForgettingTheService()
    {
        var cache = new LocalServiceEndpointCache(SuccessTtl, FailureBackoff);
        var now = DateTimeOffset.UtcNow;
        cache.Remember("http://10.0.0.2:5070", now);
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stale = cache.LookupOrJoin(now.AddSeconds(121), () =>
        {
            probed.TrySetResult();
            return Task.FromResult<string?>(null);
        });
        await probed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Equal(EndpointLookupKind.Stale, stale.Kind);
        Assert.Equal("http://10.0.0.2:5070", cache.LastGood);
        var again = cache.LookupOrJoin(now.AddSeconds(122), () => throw new InvalidOperationException("backoff must hold"));
        Assert.Equal(EndpointLookupKind.Stale, again.Kind);
        Assert.Equal("http://10.0.0.2:5070", again.Url);
    }

    [Fact]
    public void Invalidate_ForgetsLastGood()
    {
        var cache = new LocalServiceEndpointCache(SuccessTtl, FailureBackoff);
        var now = DateTimeOffset.UtcNow;
        cache.Remember("http://10.0.0.2:5070", now);

        cache.Invalidate();

        Assert.Null(cache.LastGood);
        Assert.False(cache.HasFresh(now));
        var lookup = cache.LookupOrJoin(now, () => Task.FromResult<string?>(null));
        Assert.Equal(EndpointLookupKind.InFlight, lookup.Kind);
    }
}
