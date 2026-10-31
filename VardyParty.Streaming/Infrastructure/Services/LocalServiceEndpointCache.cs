namespace VardyParty.Streaming;

/// <summary>
/// Single-flight UDP discovery cache. Concurrent resolves share one probe.
/// A caller that loses the race cannot publish a failure backoff over a
/// success, and an in-flight probe is not a cache miss for the others.
/// <para>
/// The last endpoint that worked is kept. A TTL expiry or a missed UDP reply
/// does not forget it: callers get it as <see cref="EndpointLookupKind.Stale"/>
/// while a background probe refreshes. Only <see cref="Invalidate"/> clears it,
/// and the service calls that only after repeated confirmed connection
/// failures. One missed probe at 16:35:57 UK time on 31 Oct 2026 used to wipe
/// the endpoint and fail every queued stream within 5s.
/// </para>
/// </summary>
internal sealed class LocalServiceEndpointCache
{
    private readonly TimeSpan _successTtl;
    private readonly TimeSpan _failureBackoff;
    private readonly object _gate = new();
    private string? _url;
    private string? _lastGood;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;
    private bool _failed;
    private Task<string?>? _inFlight;
    private CancellationTokenSource? _probeCts;
    private int _waiters;

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
            _lastGood = _url;
            _cachedAt = now;
            _failed = false;
            _failedAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>The last endpoint that worked, even when its TTL has expired.</summary>
    public string? LastGood
    {
        get
        {
            lock (_gate)
            {
                return _lastGood;
            }
        }
    }

    /// <summary>
    /// Forget the endpoint, including the last good one. Call this only after
    /// repeated confirmed connection failures to it, never for a slow call or
    /// a failed stream.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _url = null;
            _lastGood = null;
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

    public EndpointLookup LookupOrJoin(DateTimeOffset now, Func<Task<string?>> startDiscovery) =>
        LookupOrJoin(now, CancellationToken.None, _ => startDiscovery());

    public EndpointLookup LookupOrJoin(
        DateTimeOffset now,
        CancellationToken callerToken,
        Func<CancellationToken, Task<string?>> startDiscovery)
    {
        ArgumentNullException.ThrowIfNull(startDiscovery);
        CancellationTokenSource? joinedProbe = null;
        EndpointLookup result;
        lock (_gate)
        {
            if (IsFresh(now))
                return EndpointLookup.Cached(_url!, _successTtl - (now - _cachedAt));

            var inBackoff = _failed && now - _failedAt < _failureBackoff;
            if (_lastGood is not null)
            {
                // Stale-while-revalidate. The caller keeps using the endpoint that
                // worked, and one background probe (outside the failure backoff)
                // looks for a newer one. Nobody waits on it, so a caller's cancel
                // never aborts it.
                if (_inFlight is null && !inBackoff)
                    StartProbeLocked(startDiscovery);
                return EndpointLookup.Stale(_lastGood);
            }

            if (inBackoff)
                return EndpointLookup.Backoff(_failureBackoff - (now - _failedAt));

            // Keep the slot task in a local. A probe that is already complete
            // finishes inside this lock and clears _inFlight before we return,
            // which would hand the caller a null flight.
            var flight = _inFlight ?? StartProbeLocked(startDiscovery);

            if (callerToken.CanBeCanceled && _probeCts is not null)
            {
                if (callerToken.IsCancellationRequested)
                {
                    if (_waiters == 0)
                        _probeCts.Cancel();
                }
                else
                {
                    _waiters++;
                    joinedProbe = _probeCts;
                }
            }

            result = EndpointLookup.InFlight(flight);
        }

        if (joinedProbe is not null)
        {
            callerToken.Register(() =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_probeCts, joinedProbe))
                        return;

                    if (_waiters > 0)
                        _waiters--;
                    if (_waiters == 0)
                        joinedProbe.Cancel();
                }
            });
        }

        return result;
    }

    /// <summary>Starts the single shared probe. The caller holds <see cref="_gate"/>.</summary>
    private Task<string?> StartProbeLocked(Func<CancellationToken, Task<string?>> startDiscovery)
    {
        _waiters = 0;
        _probeCts = new CancellationTokenSource();
        var slot = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flight = slot.Task;
        _inFlight = flight;
        var probeToken = _probeCts.Token;
        Task<string?> discovery;
        try
        {
            discovery = startDiscovery(probeToken);
        }
        catch (Exception ex)
        {
            discovery = Task.FromException<string?>(ex);
        }

        _ = PublishAsync(slot, discovery, probeToken);
        return flight;
    }

    private bool IsFresh(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(_url) && now - _cachedAt < _successTtl;

    private async Task PublishAsync(
        TaskCompletionSource<string?> slot,
        Task<string?> discovery,
        CancellationToken probeToken)
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

        if (probeToken.IsCancellationRequested)
            cancelled = true;

        string? published = null;
        lock (_gate)
        {
            if (!cancelled && !string.IsNullOrWhiteSpace(discovered))
            {
                _url = discovered.TrimEnd('/');
                _lastGood = _url;
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
                // A missed UDP reply is not proof the service moved. Keep the
                // last good endpoint; the backoff only throttles the next probe.
                _url = null;
                _cachedAt = DateTimeOffset.MinValue;
                _failed = true;
                _failedAt = DateTimeOffset.UtcNow;
                published = _lastGood;
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

    /// <summary>
    /// The TTL expired, but the endpoint worked last time. Use it; a background
    /// probe refreshes it.
    /// </summary>
    Stale,
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

    public static EndpointLookup Stale(string url) =>
        new(EndpointLookupKind.Stale, url, TimeSpan.Zero, null);

    public static EndpointLookup Backoff(TimeSpan remaining) =>
        new(EndpointLookupKind.Backoff, null, remaining, null);

    public static EndpointLookup InFlight(Task<string?> flight) =>
        new(EndpointLookupKind.InFlight, null, default, flight);
}
