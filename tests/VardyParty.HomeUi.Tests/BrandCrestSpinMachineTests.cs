using System;
using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

/// <summary>
/// Lifecycle DECISIONS of the crest turntable (spin → abort → deferred
/// restart, catalog-applied → queued settle, overdue snap) against a fake
/// clock. MAUI animation plumbing (Commit/AbortAnimation, Loaded/Unloaded
/// handler teardown) is not exercised here and stays device/manual coverage.
/// </summary>
public class BrandCrestSpinMachineTests
{
    [Fact]
    public void BeginLoading_StartsTurntable()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();

        // Act
        var step = machine.BeginLoading();

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.StartSpin, step);
        Assert.True(machine.Spinning);
        Assert.True(machine.ShouldContinueSpin);
    }

    [Fact]
    public void BeginLoading_WhileSpinning_KeepsTheLiveTurn()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();

        // Act
        var step = machine.BeginLoading();

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.None, step);
        Assert.True(machine.Spinning);
    }

    [Fact]
    public void SpinCycleFinished_NaturalRolloverWhileLoading_KeepsSpinning()
    {
        // Arrange — MAUI invokes finished at every cycle end even when repeat
        // re-arms the turn.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();

        // Act
        var step = machine.SpinCycleFinished(cancelled: false, shouldSpin: true);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.None, step);
        Assert.True(machine.Spinning);
        Assert.True(machine.ShouldContinueSpin);
    }

    [Fact]
    public void SpinCycleFinished_AbortWhileLoading_DefersRestart_NeverRestartsInline()
    {
        // Arrange — catalog materialization aborts the turn mid-load. The
        // restart must ride the deferred tick (Windows: apply pump), never an
        // inline Commit or a Dispatcher.Dispatch from the layout-adjacent
        // callback.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();

        // Act
        var step = machine.SpinCycleFinished(cancelled: true, shouldSpin: true);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.Defer, step);
        Assert.True(machine.RestartPending);
        Assert.True(machine.HasDeferredWork);
        Assert.False(machine.Spinning);
    }

    [Fact]
    public void DeferredTick_AfterAbortWhileLoading_RestartsTheSpin()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.SpinCycleFinished(cancelled: true, shouldSpin: true);

        // Act
        var step = machine.DeferredTick(
            shouldSpin: true,
            spinAnimationRunning: false,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.StartSpin, step);
        Assert.True(machine.Spinning);
        Assert.False(machine.RestartPending);
    }

    [Fact]
    public void DeferredTick_RestartPendingButLoadingFinished_SettlesInstead()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.SpinCycleFinished(cancelled: true, shouldSpin: true);

        // Act
        var step = machine.DeferredTick(
            shouldSpin: false,
            spinAnimationRunning: false,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.SettleAnimated, step);
        Assert.False(machine.RestartPending);
        Assert.True(machine.SettleRequested);
    }

    [Fact]
    public void RequestSettle_CatalogAppliedDuringShouldSpin_QueuesSettleBehindTheLiveTurn()
    {
        // Arrange — rows painted while IsContentLoading has not flipped yet.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();

        // Act
        var step = machine.RequestSettle(
            spinAnimationRunning: true,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert — queued, not dropped: the turn stops repeating and the
        // cycle-completion callback settles.
        Assert.Equal(BrandCrestSpinMachine.Step.Defer, step);
        Assert.True(machine.SettleRequested);
        Assert.False(machine.ShouldContinueSpin);
        Assert.True(machine.HasDeferredWork);
    }

    [Fact]
    public void SpinCycleFinished_WithQueuedSettle_SettlesEvenIfLoadingFlagStillSet()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: true, settleAnimationRunning: false, atFaceOnRest: false);

        // Act
        var step = machine.SpinCycleFinished(cancelled: false, shouldSpin: true);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.SettleAnimated, step);
        Assert.False(machine.Spinning);
    }

    [Fact]
    public void RequestSettle_SpinnerKilledByLayout_SettlesImmediately()
    {
        // Arrange — the catalog apply killed the spin animation; the crest
        // must not sit edge-on.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.SpinCycleFinished(cancelled: true, shouldSpin: true);

        // Act
        var step = machine.RequestSettle(
            spinAnimationRunning: false,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert — the queued settle also outranks the pending restart.
        Assert.Equal(BrandCrestSpinMachine.Step.SettleAnimated, step);
        Assert.False(machine.RestartPending);
    }

    [Fact]
    public void CatalogApplied_ContentNotReady_NeverSettles_KeepsSpinning()
    {
        // Arrange — a pre-API/empty apply flushes while the turntable runs.
        // The enriched-first contract: "ready" = API data present, so this
        // apply must not queue a settle.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();

        // Act
        var step = machine.CatalogApplied(
            contentReady: false,
            spinAnimationRunning: true,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.None, step);
        Assert.False(machine.SettleRequested);
        Assert.True(machine.ShouldContinueSpin);
    }

    [Fact]
    public void CatalogApplied_ContentNotReady_SpinKilledByLayout_DefersRestart()
    {
        // Arrange — the paint that delivered the (still not ready) apply
        // aborted the turn; the crest must self-heal, not settle edge-on.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();

        // Act: the spin animation is no longer running but the machine still
        // believes it spins.
        var step = machine.CatalogApplied(
            contentReady: false,
            spinAnimationRunning: false,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.Defer, step);
        Assert.True(machine.RestartPending);
        Assert.False(machine.SettleRequested);
    }

    [Fact]
    public void CatalogApplied_ContentReady_QueuesSettleBehindTheLiveTurn()
    {
        // Arrange — the real (enriched) board painted while the loading flag
        // lags: same queued-settle behaviour as before.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();

        // Act
        var step = machine.CatalogApplied(
            contentReady: true,
            spinAnimationRunning: true,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.Defer, step);
        Assert.True(machine.SettleRequested);
        Assert.False(machine.ShouldContinueSpin);
    }

    [Fact]
    public void CatalogApplied_ContentReady_DeadSpinner_SettlesImmediately()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.SpinCycleFinished(cancelled: true, shouldSpin: true);

        // Act
        var step = machine.CatalogApplied(
            contentReady: true,
            spinAnimationRunning: false,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.SettleAnimated, step);
        Assert.False(machine.RestartPending);
    }

    [Fact]
    public void RequestSettle_AlreadyAtFaceOnRest_DoesNothing()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.RestCompleted();

        // Act
        var step = machine.RequestSettle(
            spinAnimationRunning: false,
            settleAnimationRunning: false,
            atFaceOnRest: true);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.None, step);
        Assert.False(machine.HasDeferredWork);
    }

    [Fact]
    public void RequestSettle_EaseAlreadyInFlight_DoesNotRestartIt_ButKeepsTheTickChainAlive()
    {
        // Arrange — image/catalog flushes arrive while the 480ms ease runs.
        // The ease must not restart (that would keep resetting it), but the
        // answer is Defer, never None: on the Desktop head's Avalonia backend
        // a "running" ease can be a zombie whose finished callback never
        // fires, so the tick chain must stay armed until rest.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: false, settleAnimationRunning: false, atFaceOnRest: false);

        // Act
        var step = machine.RequestSettle(
            spinAnimationRunning: false,
            settleAnimationRunning: true,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.Defer, step);
        Assert.True(machine.HasDeferredWork);
    }

    [Fact]
    public void DeferredTick_SettleOverdue_SnapsToRest()
    {
        // Arrange — a zombie turn survived a full turn plus the ease plus
        // slack after the settle was queued (the old IDispatcherTimer
        // watchdog's job, now clock-driven from reliable ticks).
        var now = 0L;
        var machine = new BrandCrestSpinMachine(() => now);
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: true, settleAnimationRunning: false, atFaceOnRest: false);
        now = BrandCrestSpin.SettleOverdueMs;

        // Act
        var step = machine.DeferredTick(
            shouldSpin: false,
            spinAnimationRunning: true,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.SnapToRest, step);
        Assert.False(machine.Spinning);
    }

    [Fact]
    public void DeferredTick_SettlePendingButAnimationsInFlight_KeepsTheTickChainAlive()
    {
        // Arrange — the nominally-live turn may be a zombie (Avalonia
        // backend: frames stalled, callbacks dead), so waiting must answer
        // Defer, never None — a None here would end the posted-continuation
        // chain and leave nothing to evaluate the overdue snap.
        var now = 0L;
        var machine = new BrandCrestSpinMachine(() => now);
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: true, settleAnimationRunning: false, atFaceOnRest: false);
        now = 100;

        // Act
        var step = machine.DeferredTick(
            shouldSpin: false,
            spinAnimationRunning: true,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert — deferred work continues, so the pump/chain keeps ticking.
        Assert.Equal(BrandCrestSpinMachine.Step.Defer, step);
        Assert.True(machine.HasDeferredWork);
    }

    [Fact]
    public void SettleAnimationFinished_AbortedByLayout_RetriesViaDeferredTick()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: false, settleAnimationRunning: false, atFaceOnRest: false);

        // Act
        var abortStep = machine.SettleAnimationFinished(cancelled: true);
        var retryStep = machine.DeferredTick(
            shouldSpin: false,
            spinAnimationRunning: false,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.Defer, abortStep);
        Assert.Equal(BrandCrestSpinMachine.Step.SettleAnimated, retryStep);
    }

    [Fact]
    public void SettleAnimationFinished_AbortedByANewLoad_DoesNotRetry()
    {
        // Arrange — BeginLoading cleared the settle before StartLoadingSpin
        // aborted the ease.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: false, settleAnimationRunning: false, atFaceOnRest: false);
        machine.BeginLoading();

        // Act
        var step = machine.SettleAnimationFinished(cancelled: true);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.None, step);
    }

    [Fact]
    public void BeginLoading_AfterQueuedSettle_ReArmsTheTurn()
    {
        // Arrange — a settle queued by an early catalog paint is superseded
        // by a genuine reload.
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: true, settleAnimationRunning: false, atFaceOnRest: false);

        // Act
        var step = machine.BeginLoading();

        // Assert — the live turn keeps going and repeats again.
        Assert.Equal(BrandCrestSpinMachine.Step.None, step);
        Assert.False(machine.SettleRequested);
        Assert.True(machine.ShouldContinueSpin);
    }

    [Fact]
    public void RestCompleted_ClearsAllDeferredWork()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: true, settleAnimationRunning: false, atFaceOnRest: false);

        // Act
        machine.RestCompleted();

        // Assert
        Assert.True(machine.AtRest);
        Assert.False(machine.HasDeferredWork);
        Assert.False(machine.SettleOverdue);
    }

    [Fact]
    public void Reset_OnUnload_DropsPendingWorkSoThePumpCanStop()
    {
        // Arrange
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();
        machine.SpinCycleFinished(cancelled: true, shouldSpin: true);

        // Act
        machine.Reset();

        // Assert
        Assert.False(machine.HasDeferredWork);
        Assert.False(machine.Spinning);
    }

    [Fact]
    public void SettleFromExactlyEdgeOn_UsesForwardRestTarget()
    {
        // Arrange — the machine's SettleAnimated step eases toward
        // RestTargetDegrees: from the edge-on freeze angle that must be 360
        // (forward), never 0 (backward through the coin-edge).
        var machine = new BrandCrestSpinMachine();
        machine.BeginLoading();

        // Act
        var step = machine.RequestSettle(
            spinAnimationRunning: false,
            settleAnimationRunning: false,
            atFaceOnRest: false);
        var target = BrandCrestSpin.RestTargetDegrees(180);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.SettleAnimated, step);
        Assert.Equal(360, target);
    }

    [Fact]
    public void DeferredTick_SpinCallbackNeverFires_TickChainAloneReachesTheSnap()
    {
        // Arrange — the Desktop head's Avalonia backend can stall animation
        // frames entirely: the turn stays registered ("running") but its
        // cycle-completion callback never fires. The settle must still reach
        // face-on rest from the tick chain alone: every pre-overdue tick
        // answers Defer (chain stays alive), and the first overdue tick
        // snaps.
        var now = 0L;
        var machine = new BrandCrestSpinMachine(() => now);
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: true, settleAnimationRunning: false, atFaceOnRest: false);

        // Act — 50ms ticks with the spin forever "running" and no callback.
        var step = BrandCrestSpinMachine.Step.None;
        var preOverdueStepsAllDefer = true;
        while (now < BrandCrestSpin.SettleOverdueMs + 50)
        {
            now += 50;
            step = machine.DeferredTick(
                shouldSpin: false,
                spinAnimationRunning: true,
                settleAnimationRunning: false,
                atFaceOnRest: false);
            if (step != BrandCrestSpinMachine.Step.SnapToRest)
            {
                preOverdueStepsAllDefer &= step == BrandCrestSpinMachine.Step.Defer;
            }
        }

        // Assert
        Assert.True(preOverdueStepsAllDefer);
        Assert.Equal(BrandCrestSpinMachine.Step.SnapToRest, step);
        Assert.False(machine.Spinning);
    }

    [Fact]
    public void DeferredTick_SettleEaseCallbackNeverFires_TickChainAloneReachesTheSnap()
    {
        // Arrange — the ease was committed (SettleAnimated) but its frames
        // stalled and the finished callback never fires; the tick chain must
        // carry the settle to the overdue snap on its own.
        var now = 0L;
        var machine = new BrandCrestSpinMachine(() => now);
        machine.BeginLoading();
        machine.SpinCycleFinished(cancelled: true, shouldSpin: true);
        machine.RequestSettle(spinAnimationRunning: false, settleAnimationRunning: false, atFaceOnRest: false);

        // Act — waiting ticks answer Defer while the zombie ease "runs".
        var waitingStep = machine.DeferredTick(
            shouldSpin: false,
            spinAnimationRunning: false,
            settleAnimationRunning: true,
            atFaceOnRest: false);
        now = BrandCrestSpin.SettleOverdueMs;
        var overdueStep = machine.DeferredTick(
            shouldSpin: false,
            spinAnimationRunning: false,
            settleAnimationRunning: true,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.Defer, waitingStep);
        Assert.Equal(BrandCrestSpinMachine.Step.SnapToRest, overdueStep);
    }

    [Fact]
    public void RequestSettle_AlreadyOverdueOnArrival_SnapsImmediately()
    {
        // Arrange — repeated catalog flushes while a settle stays unresolved:
        // once overdue, the very next request snaps instead of easing again.
        var now = 0L;
        var machine = new BrandCrestSpinMachine(() => now);
        machine.BeginLoading();
        machine.RequestSettle(spinAnimationRunning: true, settleAnimationRunning: false, atFaceOnRest: false);
        now = BrandCrestSpin.SettleOverdueMs + 1;

        // Act
        var step = machine.RequestSettle(
            spinAnimationRunning: true,
            settleAnimationRunning: false,
            atFaceOnRest: false);

        // Assert
        Assert.Equal(BrandCrestSpinMachine.Step.SnapToRest, step);
    }
}
