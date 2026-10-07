using System.Collections.Concurrent;
using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// Collects streamed <c>/mp</c> chip events so the first playlist can return
/// while later labels wait on the same Chrome session.
/// </summary>
public sealed class MpHarvestSession
{
    private readonly ConcurrentDictionary<string, M3U8Response> _byLabel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<M3U8Response?>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource<M3U8Response?> _firstChip = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private List<string> _streams = [];
    private string? _error;
    private bool _done;

    public IReadOnlyList<string> Streams
    {
        get
        {
            lock (_gate)
            {
                return _streams.ToList();
            }
        }
    }

    public string? Error => _error;

    public bool IsCompleted => _completed.Task.IsCompleted;

    public bool HasPlaylist => _byLabel.Values.Any(r => !string.IsNullOrWhiteSpace(r.Url));

    /// <summary>
    /// Keep one Chrome harvest for the same page while it is still running or
    /// already has a playlist. A completed miss must not block the next tap.
    /// </summary>
    public bool CanReuseFor(string? page, string streamUrl) =>
        string.Equals(page, streamUrl, StringComparison.OrdinalIgnoreCase)
        && (!IsCompleted || HasPlaylist);

    public Task WhenCompleted => _completed.Task;

    public void Apply(MpPlayEvent ev)
    {
        switch (ev.Type)
        {
            case "chips":
                lock (_gate)
                {
                    if (ev.Streams is { Count: > 0 })
                    {
                        _streams = ev.Streams.ToList();
                    }
                }

                break;

            case "chip":
                var response = MpNdjson.ToResponse(ev, Streams);
                if (response is null)
                {
                    return;
                }

                var label = response.SelectedStream ?? ev.Label ?? "";
                if (!string.IsNullOrWhiteSpace(label))
                {
                    _byLabel[label] = response;
                    if (_waiters.TryRemove(label, out var waiter))
                    {
                        waiter.TrySetResult(response);
                    }
                }

                if (!string.IsNullOrWhiteSpace(response.Url))
                {
                    _firstChip.TrySetResult(response);
                }

                break;

            case "error":
                _error = ev.Error;
                Complete(failed: true);
                break;

            case "done":
                Complete(failed: false);
                break;
        }
    }

    public void ApplyLegacy(M3U8Response result)
    {
        if (result.Streams is { Count: > 0 })
        {
            Apply(new MpPlayEvent { Type = "chips", Streams = result.Streams, SelectedStream = result.SelectedStream });
        }

        if (!string.IsNullOrWhiteSpace(result.Url) || !string.IsNullOrWhiteSpace(result.SelectedStream))
        {
            Apply(new MpPlayEvent
            {
                Type = "chip",
                Label = result.SelectedStream,
                Url = result.Url,
                RequestHeaders = result.RequestHeaders,
                RewrittenSegments = result.RewrittenSegments,
                Streams = result.Streams
            });
        }

        Complete(failed: string.IsNullOrWhiteSpace(result.Url));
    }

    public async Task<M3U8Response?> WaitForAsync(string? label, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(label)
            && _byLabel.TryGetValue(label.Trim(), out var cached))
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            return await _firstChip.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var key = label.Trim();
        if (_done)
        {
            return _byLabel.TryGetValue(key, out var late) ? late : null;
        }

        var waiter = _waiters.GetOrAdd(
            key,
            _ => new TaskCompletionSource<M3U8Response?>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_byLabel.TryGetValue(key, out cached))
        {
            waiter.TrySetResult(cached);
        }
        else if (_done)
        {
            waiter.TrySetResult(_byLabel.TryGetValue(key, out var afterClear) ? afterClear : null);
        }

        return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Complete(bool failed, string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            _error = error;
        }

        _done = true;
        if (!_firstChip.Task.IsCompleted)
        {
            _firstChip.TrySetResult(null);
        }

        foreach (var waiter in _waiters.Values)
        {
            waiter.TrySetResult(null);
        }

        _waiters.Clear();
        _completed.TrySetResult();
    }
}
