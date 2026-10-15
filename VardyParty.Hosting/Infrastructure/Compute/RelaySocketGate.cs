namespace VardyParty.Hosting;

/// <summary>
/// One guest WebSocket at a time. The relay closes an older guest socket when
/// a second one connects, and the PC then cancels the scrape that was running.
/// </summary>
internal sealed class RelaySocketGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RelaySocketLease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RelaySocketLease(this);
    }

    internal void Release() => _gate.Release();

    internal async Task ReleaseAfter(Task socketClosed)
    {
        try
        {
            await socketClosed.ConfigureAwait(false);
        }
        catch
        {
            // The next stream still needs this gate once the socket is gone.
        }
        finally
        {
            Release();
        }
    }
}

/// <summary>
/// Owns the single guest socket. Dispose releases it. <see cref="HandOff"/>
/// keeps it until a later task finishes, which is the /mp harvest that stays
/// open after the first chip is returned.
/// </summary>
internal sealed class RelaySocketLease : IAsyncDisposable
{
    private RelaySocketGate? _gate;

    internal RelaySocketLease(RelaySocketGate gate) => _gate = gate;

    public void HandOff(Task socketClosed)
    {
        var gate = Interlocked.Exchange(ref _gate, null);
        if (gate is null)
        {
            return;
        }

        _ = gate.ReleaseAfter(socketClosed);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _gate, null)?.Release();
        return ValueTask.CompletedTask;
    }
}
