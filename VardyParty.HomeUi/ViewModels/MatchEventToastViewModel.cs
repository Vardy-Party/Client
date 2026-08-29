using System.ComponentModel;

namespace VardyParty.HomeUi;

/// <summary>
/// Queue/dismiss state machine for the homepage match-event toast. Pure
/// timing logic with an injectable clock; the view owns the actual finite
/// animations and the one-shot dismiss delay (TV idle invariant: nothing here
/// schedules recurring work — every toast is one enter animation, one delayed
/// dismiss callback, one exit animation).
///
/// Near-simultaneous events queue sequentially behind the showing toast, at
/// most <see cref="MaxQueued"/> deep — beyond that the OLDEST queued toast is
/// dropped (a burst of goals must end on the freshest ones). Dismissal is
/// token-guarded: a stale delayed callback (superseded presentation) can
/// never dismiss the wrong toast.
/// </summary>
public sealed class MatchEventToastViewModel : INotifyPropertyChanged
{
    /// <summary>How long one toast stays up before its exit animation.</summary>
    public static readonly TimeSpan ShowDuration = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Dismiss-due tolerance: platform delayed callbacks can land a few
    /// milliseconds early, and a refused dismiss is never retried (the toast
    /// would stick forever).
    /// </summary>
    public static readonly TimeSpan DismissTolerance = TimeSpan.FromMilliseconds(100);

    /// <summary>Queue cap behind the showing toast; beyond it the oldest queued drops.</summary>
    public const int MaxQueued = 3;

    private readonly TimeProvider _time;
    private readonly Queue<MatchEventToastItem> _queue = new();
    private MatchEventToastItem? _current;
    private long _presentedTimestamp;
    private bool _dismissBegun;

    public MatchEventToastViewModel(HomeLayoutState layout, TimeProvider? time = null)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _time = time ?? TimeProvider.System;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a toast becomes the visible one (enter + card flash + dismiss scheduling).</summary>
    public event Action<MatchEventToastItem>? Presented;

    public HomeLayoutState Layout { get; }

    public MatchEventToastItem? Current
    {
        get => _current;
        private set
        {
            if (ReferenceEquals(_current, value)) return;
            _current = value;
            Raise(nameof(Current));
            Raise(nameof(IsToastVisible));
        }
    }

    public bool IsToastVisible => _current != null;

    /// <summary>Increments per presentation; guards stale dismiss callbacks.</summary>
    public int PresentationToken { get; private set; }

    /// <summary>How many toasts wait behind the showing one (test/diagnostic surface).</summary>
    public int QueuedCount => _queue.Count;

    /// <summary>UI thread only (the catalog apply pump / toast callbacks).</summary>
    public void Publish(MatchEventToastItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Current == null)
        {
            Present(item);
            return;
        }

        _queue.Enqueue(item);
        while (_queue.Count > MaxQueued)
        {
            _queue.Dequeue(); // drop-oldest beyond the cap
        }
    }

    /// <summary>
    /// The view's delayed dismiss callback: true when this token's toast is
    /// still showing and its time is up — the view then runs the exit
    /// animation and calls <see cref="CompleteDismiss"/>. False for stale
    /// tokens, an already-running dismissal, or a not-yet-due toast.
    /// </summary>
    public bool TryBeginDismiss(int token)
    {
        if (Current == null || token != PresentationToken || _dismissBegun)
        {
            return false;
        }

        if (_time.GetElapsedTime(_presentedTimestamp) < ShowDuration - DismissTolerance)
        {
            return false;
        }

        _dismissBegun = true;
        return true;
    }

    /// <summary>Exit animation finished: hide, then present the next queued toast.</summary>
    public void CompleteDismiss(int token)
    {
        if (token != PresentationToken || Current == null)
        {
            return;
        }

        Current = null;
        _dismissBegun = false;

        if (_queue.Count > 0)
        {
            Present(_queue.Dequeue());
        }
    }

    private void Present(MatchEventToastItem item)
    {
        PresentationToken++;
        _presentedTimestamp = _time.GetTimestamp();
        _dismissBegun = false;
        Current = item;
        Presented?.Invoke(item);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
