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
    public void SuccessTtl_ExpiresAt120Seconds()
    {
        var cache = new LocalServiceEndpointCache(SuccessTtl, FailureBackoff);
        var now = DateTimeOffset.UtcNow;
        cache.Remember("http://10.0.0.2:5070/", now);

        var fresh = cache.LookupOrJoin(now.AddSeconds(119), () => throw new InvalidOperationException("probe"));
        Assert.Equal(EndpointLookupKind.Cached, fresh.Kind);
        Assert.Equal("http://10.0.0.2:5070", fresh.Url);

        var expired = cache.LookupOrJoin(now.AddSeconds(121), () => Task.FromResult<string?>("http://10.1.1.1:1"));
        Assert.Equal(EndpointLookupKind.InFlight, expired.Kind);
    }
}
