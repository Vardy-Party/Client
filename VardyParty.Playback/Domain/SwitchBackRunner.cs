namespace VardyParty.Playback;

/// <summary>
/// Host-side option 2: re-arm with the current row (height + name) before the
/// URL is cleared, then run <see cref="SwitchBackLoop"/> until cancel or return.
/// </summary>
public sealed class SwitchBackRunner
{
    private readonly PlaybackSessionController _session;
    private readonly Func<SwitchBackCandidate?> _current;
    private readonly Func<SwitchBackCandidate, CancellationToken, Task<(bool Playable, int StatusCode)>> _probe;
    private readonly Func<SwitchBackCandidate, string, CancellationToken, Task> _switchBack;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SwitchBackRunner(
        PlaybackSessionController session,
        Func<SwitchBackCandidate?> current,
        Func<SwitchBackCandidate, CancellationToken, Task<(bool Playable, int StatusCode)>> probe,
        Func<SwitchBackCandidate, string, CancellationToken, Task> switchBack,
        Action<string>? log = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _switchBack = switchBack ?? throw new ArgumentNullException(nameof(switchBack));
        _log = log;
    }

    public void Consider(PlaybackCommand cmd)
    {
        SwitchBackCommands.Apply(
            _session.SwitchBack,
            _session.SwitchBackEnabled,
            cmd,
            _current(),
            DateTimeOffset.UtcNow,
            _log);
        EnsureLoop();
    }

    public void NoteBackupHeight(int? height)
        => _session.SwitchBack.NoteBackupHeight(height, DateTimeOffset.UtcNow);

    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); }
            catch { /* already cancelled */ }
        }
    }

    private void EnsureLoop()
    {
        if (!_session.SwitchBack.IsArmed)
            return;

        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => SwitchBackLoop.RunAsync(
                _session.SwitchBack,
                _probe,
                _switchBack,
                delay: null,
                clock: null,
                log: _log,
                cancellationToken: token), token);
        }
    }
}
