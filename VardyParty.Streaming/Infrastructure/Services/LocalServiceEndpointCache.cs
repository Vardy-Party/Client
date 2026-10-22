namespace VardyParty.Streaming;

/// <summary>
/// Single-flight UDP discovery cache. Concurrent resolves share one probe.
/// A caller that loses the race cannot publish a failure backoff over a
/// success, and an in-flight probe is not a cache miss for the others.
/// </summary>
internal sealed class LocalServiceEndpointCache
{
    private readonly TimeSpan _successTtl;
    private readonly TimeSpan _failureBackoff;
    private readonly object _gate = new();
    private string? _url;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;
    private bool _failed;
    private Task<string?>? _inFlight;

    public LocalServiceEndpointCache(TimeSpan successTtl, TimeSpan failureBackoff)
    {
        _successTtl = successTtl;
        _failureBackoff = failureBackoff;
    }

    public void Remember(string url, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        lock (_gate)
        {
            _url = url.TrimEnd('/');
            _cachedAt = now;
            _failed = false;
            _failedAt = DateTimeOffset.MinValue;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _url = null;
            _cachedAt = DateTimeOffset.MinValue;
            _failed = false;
            _failedAt = DateTimeOffset.MinValue;
        }
    }

    public bool HasFresh(DateTimeOffset now)
    {
        lock (_gate)
        {
            return IsFresh(now);
        }
    }

    public EndpointLookup LookupOrJoin(DateTimeOffset now, Func<Task<string?>> startDiscovery)
    {
        ArgumentNullException.ThrowIfNull(startDiscovery);
        lock (_gate)
        {
            if (IsFresh(now))
                return EndpointLookup.Cached(_url!, _successTtl - (now - _cachedAt));

            if (_failed && now - _failedAt < _failureBackoff)
                return EndpointLookup.Backoff(_failureBackoff - (now - _failedAt));

            if (_inFlight is not null)
                return EndpointLookup.InFlight(_inFlight);

            // The slot is published before the probe runs. A probe that is
            // already complete would otherwise finish inside this lock and
            // clear _inFlight before the assignment, leaving the completed
            // task stuck as the next caller's "in flight" result.
            var slot = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = slot.Task;
            Task<string?> discovery;
            try
            {
                discovery = startDiscovery();
            }
            catch (Exception ex)
            {
                discovery = Task.FromException<string?>(ex);
            }

            _ = PublishAsync(slot, discovery);
            return EndpointLookup.InFlight(slot.Task);
        }
    }

    private bool IsFresh(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(_url) && now - _cachedAt < _successTtl;

    private async Task PublishAsync(TaskCompletionSource<string?> slot, Task<string?> discovery)
    {
        string? discovered = null;
        var cancelled = false;
        try
        {
            discovered = await discovery.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation must not become a 5s "service missing" backoff.
            cancelled = true;
        }
        catch (Exception)
        {
            discovered = null;
        }

        string? published = null;
        lock (_gate)
        {
            if (!cancelled && !string.IsNullOrWhiteSpace(discovered))
            {
                _url = discovered.TrimEnd('/');
                _cachedAt = DateTimeOffset.UtcNow;
                _failed = false;
                _failedAt = DateTimeOffset.MinValue;
                published = _url;
            }
            else if (IsFresh(DateTimeOffset.UtcNow))
            {
                _failed = false;
                published = _url;
            }
            else if (!cancelled)
            {
                _url = null;
                _cachedAt = DateTimeOffset.MinValue;
                _failed = true;
                _failedAt = DateTimeOffset.UtcNow;
            }

            if (ReferenceEquals(_inFlight, slot.Task))
                _inFlight = null;
        }

        slot.TrySetResult(published);
    }
}

internal enum EndpointLookupKind
{
    Cached,
    Backoff,
    InFlight
}

internal readonly struct EndpointLookup
{
    private EndpointLookup(EndpointLookupKind kind, string? url, TimeSpan remaining, Task<string?>? flight)
    {
        Kind = kind;
        Url = url;
        Remaining = remaining;
        Flight = flight;
    }

    public EndpointLookupKind Kind { get; }
    public string? Url { get; }
    public TimeSpan Remaining { get; }
    public Task<string?>? Flight { get; }

    public static EndpointLookup Cached(string url, TimeSpan remaining) =>
        new(EndpointLookupKind.Cached, url, remaining, null);

    public static EndpointLookup Backoff(TimeSpan remaining) =>
        new(EndpointLookupKind.Backoff, null, remaining, null);

    public static EndpointLookup InFlight(Task<string?> flight) =>
        new(EndpointLookupKind.InFlight, null, default, flight);
}
