namespace VardyParty.HomeUi.Views;

/// <summary>
/// The crest turntable's lifecycle decisions as a pure state machine, so the
/// spin → abort → deferred restart, catalog-applied → queued settle, and
/// overdue-snap (watchdog) rules are unit-testable without MAUI animation
/// plumbing. <see cref="BrandLogoView"/> feeds it events plus the current
/// animation facts and executes the returned <see cref="Step"/>; the clock is
/// injectable for tests. Two invariants are encoded here:
/// deferred work never runs inline from a layout-adjacent callback (Windows
/// stowed-exception class — <see cref="Step.Defer"/> rides HomeView's apply
/// pump on Windows and a posted continuation elsewhere, never an
/// <c>IDispatcherTimer</c>), and once a settle is requested the machine keeps
/// answering with settle steps until <see cref="RestCompleted"/> — snapping
/// after <see cref="BrandCrestSpin.SettleOverdueMs"/> — so the crest always
/// reaches face-on rest.
/// </summary>
public sealed class BrandCrestSpinMachine
{
    /// <summary>What the view must do next; the machine never touches visuals.</summary>
    public enum Step
    {
        None,

        /// <summary>Commit a fresh 360° turn.</summary>
        StartSpin,

        /// <summary>Ease from the current angle to face-on rest.</summary>
        SettleAnimated,

        /// <summary>
        /// Kill anything in flight and rest face-on with direct property
        /// writes (layout cannot abort those) — the overdue terminal state.
        /// </summary>
        SnapToRest,

        /// <summary>
        /// Schedule a deferred crest tick (Windows: apply pump; elsewhere: a
        /// posted continuation) that calls <see cref="DeferredTick"/>.
        /// </summary>
        Defer,
    }

    private readonly Func<long> _nowMs;
    private long _settleRequestedAtMs = -1;

    public BrandCrestSpinMachine()
        : this(null)
    {
    }

    public BrandCrestSpinMachine(Func<long>? nowMs) =>
        _nowMs = nowMs ?? (() => Environment.TickCount64);

    public bool Spinning { get; private set; }

    /// <summary>A layout-aborted turn is waiting for a deferred restart.</summary>
    public bool RestartPending { get; private set; }

    /// <summary>Settle queued; stays set until the crest reaches rest.</summary>
    public bool SettleRequested { get; private set; }

    public bool AtRest { get; private set; }

    /// <summary>
    /// True while deferred continuations must keep coming (HomeView's Windows
    /// pump keeps ticking while this is set).
    /// </summary>
    public bool HasDeferredWork => RestartPending || (SettleRequested && !AtRest);

    /// <summary>Consulted by the spin animation's repeat callback at cycle end.</summary>
    public bool ShouldContinueSpin => BrandCrestSpin.ContinueSpinCycle(Spinning, SettleRequested);

    /// <summary>
    /// The settle had a full turn plus the ease plus slack to land and did
    /// not: stop animating and snap.
    /// </summary>
    public bool SettleOverdue =>
        SettleRequested
        && _settleRequestedAtMs >= 0
        && _nowMs() - _settleRequestedAtMs >= BrandCrestSpin.SettleOverdueMs;

    /// <summary>ShouldSpin turned true on a loaded view: run the turntable.</summary>
    public Step BeginLoading()
    {
        RestartPending = false;
        SettleRequested = false;
        AtRest = false;
        _settleRequestedAtMs = -1;
        if (Spinning)
        {
            // The live turn keeps going: clearing SettleRequested re-arms the
            // repeat callback, so a settle queued by an early catalog paint
            // is superseded by the new load.
            return Step.None;
        }

        Spinning = true;
        return Step.StartSpin;
    }

    /// <summary>
    /// A catalog apply flushed while the view is loaded. Supersedes the old
    /// "queue settle on any catalog apply" rule for the not-ready case: an
    /// apply WITHOUT API data (the pre-API null/empty boards the enriched-
    /// first feed can still deliver) must NOT settle the crest — it keeps
    /// spinning until real content lands (<paramref name="contentReady"/>
    /// false → self-heal a layout-killed turn instead of settling). A ready
    /// apply queues the settle as before, so a spinner killed by
    /// materialization still eases to rest from its last angle.
    /// </summary>
    public Step CatalogApplied(bool contentReady, bool spinAnimationRunning, bool settleAnimationRunning, bool atFaceOnRest)
    {
        if (contentReady)
        {
            return RequestSettle(spinAnimationRunning, settleAnimationRunning, atFaceOnRest);
        }

        if (spinAnimationRunning || settleAnimationRunning)
        {
            // Still loading with a live animation: leave it alone.
            return Step.None;
        }

        if (Spinning || RestartPending)
        {
            // Layout killed the turn (the paint that delivered this apply is
            // exactly the abort trigger) — re-drive the deferred restart so
            // the crest never sits edge-on while loading continues.
            Spinning = false;
            RestartPending = true;
            return Step.Defer;
        }

        return Step.None;
    }

    /// <summary>
    /// Content is ready (loading flag cleared, or the catalog painted while
    /// the flag lags — ShouldSpin still true). The settle is queued, never
    /// dropped: a live turn finishes its cycle first, a dead spinner settles
    /// now, an overdue settle snaps.
    /// </summary>
    public Step RequestSettle(bool spinAnimationRunning, bool settleAnimationRunning, bool atFaceOnRest)
    {
        if (AtRest && atFaceOnRest)
        {
            return Step.None;
        }

        AtRest = false;
        RestartPending = false;
        if (!SettleRequested)
        {
            SettleRequested = true;
            _settleRequestedAtMs = _nowMs();
        }

        if (SettleOverdue)
        {
            Spinning = false;
            return Step.SnapToRest;
        }

        if (settleAnimationRunning)
        {
            // The ease is in flight; its finished callback completes or
            // retries. Restarting it on every catalog/image flush would keep
            // resetting the 480ms ease.
            return Step.None;
        }

        if (spinAnimationRunning)
        {
            // The turn stops repeating (ShouldContinueSpin is now false) and
            // SpinCycleFinished settles — an animation-frame tick that fires
            // on every platform. Defer also arms the pump/post as watchdog.
            return Step.Defer;
        }

        Spinning = false;
        return Step.SettleAnimated;
    }

    /// <summary>
    /// The spin animation's finished callback: a natural cycle end, the last
    /// cycle before a queued settle, or a layout abort.
    /// </summary>
    public Step SpinCycleFinished(bool cancelled, bool shouldSpin)
    {
        if (SettleRequested || !shouldSpin)
        {
            // A queued settle outranks a restart: the rows are painted, so
            // the crest eases to rest even if the cycle was layout-cancelled.
            Spinning = false;
            if (!SettleRequested)
            {
                SettleRequested = true;
                _settleRequestedAtMs = _nowMs();
            }

            return SettleOverdue ? Step.SnapToRest : Step.SettleAnimated;
        }

        if (cancelled)
        {
            // Layout aborted the turn mid-load. Never restart inline (that
            // fights the same layout pass) and never Dispatcher.Dispatch on
            // Windows (the stowed-exception class): defer to the pump/post.
            Spinning = false;
            RestartPending = true;
            return Step.Defer;
        }

        // Natural cycle rollover while loading: repeat already re-armed.
        return Step.None;
    }

    /// <summary>
    /// A deferred crest tick arrived (Windows apply-pump tick or a posted
    /// continuation): run whatever work is still pending.
    /// </summary>
    public Step DeferredTick(bool shouldSpin, bool spinAnimationRunning, bool settleAnimationRunning, bool atFaceOnRest)
    {
        if (SettleRequested && !AtRest)
        {
            RestartPending = false;
            if (SettleOverdue)
            {
                Spinning = false;
                return Step.SnapToRest;
            }

            if (settleAnimationRunning || spinAnimationRunning)
            {
                // In flight; the animation's own callbacks resolve it (the
                // Windows pump keeps ticking via HasDeferredWork meanwhile).
                return Step.None;
            }

            Spinning = false;
            return Step.SettleAnimated;
        }

        if (!RestartPending)
        {
            return Step.None;
        }

        RestartPending = false;
        if (!shouldSpin)
        {
            // Loading finished while the restart waited: settle instead.
            return RequestSettle(spinAnimationRunning, settleAnimationRunning, atFaceOnRest);
        }

        Spinning = true;
        return spinAnimationRunning ? Step.None : Step.StartSpin;
    }

    /// <summary>
    /// The settle animation's finished callback. A layout-aborted ease
    /// retries from the next deferred tick; a completed one lets the view
    /// finish via <see cref="RestCompleted"/>.
    /// </summary>
    public Step SettleAnimationFinished(bool cancelled)
    {
        if (!cancelled)
        {
            return Step.None;
        }

        return SettleRequested ? Step.Defer : Step.None;
    }

    /// <summary>The crest is face-on: all pending work is done.</summary>
    public void RestCompleted()
    {
        Spinning = false;
        RestartPending = false;
        SettleRequested = false;
        AtRest = true;
        _settleRequestedAtMs = -1;
    }

    /// <summary>View unloaded: drop all pending work.</summary>
    public void Reset()
    {
        Spinning = false;
        RestartPending = false;
        SettleRequested = false;
        AtRest = false;
        _settleRequestedAtMs = -1;
    }
}
