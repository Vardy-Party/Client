using VardyParty.Ports;

namespace VardyParty.Playback;

/// <summary>
/// Host-side option 2: re-arm with the current row (height + name) before the
/// URL is cleared, then run <see cref="SwitchBackLoop"/> until cancel or return.
/// Platforms build it with <see cref="Create"/> and pass only their settled
/// picture height and UI dispatcher, so Android, Windows and Linux behave the same.
/// </summary>
/// <remarks>
/// <code>
/// adapter ──Consider(cmd)──▶ SwitchBackCommands.Apply ──Arm──▶ SwitchBackSession
///                                 │                               ▲
///                                 └──EnsureLoop──▶ SwitchBackLoop ─┤ Next / NoteProbe(armId)
///                                                   │  probe: ISwitchBackProbe (thread pool)
///                                                   └─ switch: runOnUi(RestoreAndSwitchTo)
/// </code>
/// </remarks>
public sealed class SwitchBackRunner
{
    private readonly PlaybackSessionController _session;
    private readonly Func<SwitchBackCandidate?> _current;
    private readonly Func<SwitchBackCandidate, CancellationToken, Task<SwitchBackProbeResult>> _probe;
    private readonly Func<SwitchBackCandidate, string, CancellationToken, Task> _switchBack;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SwitchBackRunner(
        PlaybackSessionController session,
        Func<SwitchBackCandidate?> current,
        Func<SwitchBackCandidate, CancellationToken, Task<SwitchBackProbeResult>> probe,
        Func<SwitchBackCandidate, string, CancellationToken, Task> switchBack,
        Action<string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _switchBack = switchBack ?? throw new ArgumentNullException(nameof(switchBack));
        _log = log;
        _delay = delay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The one way platforms build a runner.
    /// </summary>
    /// <param name="session">The adapter's shared session controller.</param>
    /// <param name="switching">The pool; the current row becomes the candidate.</param>
    /// <param name="probe">The DI-registered <see cref="ISwitchBackProbe"/> (falls back to <see cref="HttpSwitchBackProbe.Default"/>).</param>
    /// <param name="settledHeight">
    /// The adapter's settled decoded picture height (<see cref="DecodedPictureSizeSettler.Published"/>),
    /// used when the row has no probed height so "return sooner to a higher
    /// resolution" works on every platform.
    /// </param>
    /// <param name="runOnUi">Runs an action on the UI thread; the switch always goes through it.</param>
    /// <param name="log">Log sink for <c>[switch-back]</c> lines.</param>
    public static SwitchBackRunner Create(
        PlaybackSessionController session,
        IStreamSwitchingService? switching,
        ISwitchBackProbe? probe,
        Func<int?> settledHeight,
        Func<Action, Task> runOnUi,
        Action<string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settledHeight);
        ArgumentNullException.ThrowIfNull(runOnUi);
        var effectiveProbe = probe ?? HttpSwitchBackProbe.Default;
        return new SwitchBackRunner(
            session,
            () => SwitchBackCommands.FromRow(switching?.GetCurrentStream(), settledHeight()),
            effectiveProbe.ProbeAsync,
            (candidate, reason, _) => runOnUi(() =>
            {
                if (!SwitchBackCommands.RestoreAndSwitchTo(switching, candidate))
                    log?.Invoke($"[switch-back] abandoned row not in pool ({reason})");
            }),
            log,
            delay,
            clock);
    }

    /// <summary>
    /// Adapts a fire-and-forget UI post (<c>RunOnUiThread</c>,
    /// <c>MainThread.BeginInvokeOnMainThread</c>, <c>Dispatcher.UIThread.Post</c>)
    /// into the awaitable <c>runOnUi</c> that <see cref="Create"/> needs. The task
    /// faults if the action throws, which <see cref="SwitchBackLoop"/> logs.
    /// </summary>
    public static Func<Action, Task> Dispatch(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return action =>
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            post(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            return tcs.Task;
        };
    }

    /// <summary>The running loop, if any (tests await it).</summary>
    internal Task? Loop
    {
        get { lock (_gate) return _loop; }
    }

    public void Consider(PlaybackCommand cmd)
    {
        SwitchBackCommands.Apply(
            _session.SwitchBack,
            _session.SwitchBackEnabled,
            cmd,
            _current(),
            _clock(),
            _log);
        EnsureLoop();
    }

    public void NoteBackupHeight(int? height)
        => _session.SwitchBack.NoteBackupHeight(height, _clock());

    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { /* already disposed */ }
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
            var log = _log;
            _loop = Task.Run(() => SwitchBackLoop.RunAsync(
                _session.SwitchBack,
                _probe,
                _switchBack,
                _delay,
                _clock,
                log,
                token), token);
            _loop.ContinueWith(
                t => log?.Invoke($"[switch-back] loop faulted ({t.Exception?.GetBaseException().GetType().Name})"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
