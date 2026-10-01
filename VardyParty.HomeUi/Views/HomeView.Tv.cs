#if ANDROID
using System.ComponentModel;
using Android.Graphics.Drawables;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;
using Keycode = Android.Views.Keycode;
using VardyParty.Kernel;
using VardyParty.Presentation;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// Android TV interaction for the homepage header and menu.
///
/// Header: the Menu button gets the same native treatment as cards —
/// focusable, DPAD_CENTER-activatable (MaterialButton click → MAUI Clicked →
/// ToggleMenu), a clearly visible focused state (scale + bright ring), and
/// silent (SoundEffectsEnabled=false; every D-pad move from it is
/// router-owned). Up from the first row reaches it via
/// <see cref="TvDpadFocusRouter.RegisterHeaderTarget"/>; the crest stays
/// skipped because the router targets this view directly. Down returns to
/// the last focused card (column memory).
///
/// Menu focus trap: when IsMenuOpen flips true, native focus goes to the
/// first menu item and D-pad navigation is owned inside the panel — up/down
/// step through the panel's focusables (clamped), left/right are consumed,
/// so focus can never escape to the cards behind the scrim. Every trapped
/// item gets a visible focused state. On close (Back/Close/menu key), focus
/// returns to the card focused before the menu opened
/// (<see cref="TvMenuFocusMemory"/> owns that bookkeeping; unit-tested).
/// </summary>
public partial class HomeView
{
    /// <summary>
    /// Frames the trap keeps retrying to land focus on the first menu item
    /// while the just-shown panel materializes its native views and lays out.
    /// </summary>
    private const int MenuTrapFocusRetryFrames = 30;

    /// <summary>
    /// Layout passes spent putting focus back on a card after the native
    /// player closes. A live card, even when detached, is posted and runs
    /// from that view's run queue on attach. A card that does not exist yet
    /// waits for one <c>OnGlobalLayout</c> of the rows list: posting on that
    /// already-attached recycler is a looper message and would spend this
    /// budget before the row is bound.
    /// </summary>
    private const int StreamExitFocusRetryFrames = 90;

    private int _streamExitFocusGeneration;
    private IStreamExitPass? _streamExitLayoutPass;

    private readonly TvMenuFocusMemory _menuFocusMemory = new();
    private readonly List<AView> _wiredTrapItems = new();
    private AView? _wiredMenuButton;
    private HomeViewModel? _tvTrapViewModel;

    partial void WireTvHeaderFocus()
    {
        MenuButton.HandlerChanged += OnMenuButtonHandlerChanged;
        OnMenuButtonHandlerChanged(MenuButton, EventArgs.Empty);
    }

    partial void RestoreTvCardFocus()
    {
        if (!IsTelevision())
        {
            return;
        }

        // Overlay Cancel held focus; when it hides, Android's default search
        // lands on Menu. Prefer the card that opened finding-streams
        // (NoteCardFocused while picking). Post so the overlay finishes
        // tearing down before RequestFocus.
        var card = TvDpadFocusRouter.LastFocusedCard();
        if (card is { IsAttachedToWindow: true, IsShown: true })
        {
            card.Post(() =>
            {
                if (!card.RequestFocus())
                {
                    TryFocusHeaderTargetSafe();
                }
            });
            return;
        }

        TryFocusHeaderTargetSafe();
    }

    private void TryFocusHeaderTargetSafe()
    {
        if (_wiredMenuButton is { IsAttachedToWindow: true, IsShown: true } menu)
        {
            menu.Post(() => menu.RequestFocus());
        }
    }

    partial void RestoreTvFocusAfterStreamExit(Game? watched)
    {
        if (!IsTelevision() || ViewModel is null)
        {
            return;
        }

        var rails = new List<IReadOnlyList<string>>(ViewModel.Rows.Count);
        foreach (var row in ViewModel.Rows)
        {
            var keys = new string[row.Cards.Count];
            for (var i = 0; i < row.Cards.Count; i++)
            {
                keys[i] = HomeBoardDiffer.GameKey(row.Cards[i].Game);
            }

            rails.Add(keys);
        }

        var watchedKey = watched is null ? null : HomeBoardDiffer.GameKey(watched);
        var choice = PlaybackExitFocus.Choose(watchedKey, rails);
        if (choice is null)
        {
            return;
        }

        var key = rails[choice.Value.RailIndex][choice.Value.CardIndex];
        var generation = ++_streamExitFocusGeneration;
        _streamExitLayoutPass?.Cancel();
        _streamExitLayoutPass = null;
        // Menu is the default focus target when the video activity returns
        // and the window has nothing focused. Hold it out until the card lands.
        TvDpadFocusRouter.HoldHeaderFocusForInitialCard();
        FocusCardAfterStreamExit(key, choice.Value.RailIndex, choice.Value.WatchedGame, StreamExitFocusRetryFrames, generation);
    }

    private void FocusCardAfterStreamExit(
        string gameKey, int railIndex, bool watchedGame, int attemptsLeft, int generation)
    {
        if (generation != _streamExitFocusGeneration)
        {
            return;
        }

        if (attemptsLeft <= 0)
        {
            FocusAttachedCardThenReleaseMenu(gameKey, railIndex);
            return;
        }

        EnsureRailVisible(railIndex);

        // Prefer the card itself, even when it is detached: View.Post on a
        // detached view runs from the run queue when that view attaches,
        // which is the next traversal that can actually take focus.
        var card = TvCardFocusRegistry.TryGet(gameKey);
        if (StreamExitFocusRetry.Choose(card is not null) == StreamExitFocusRetry.Wait.PostOnCard)
        {
            card!.Post(() => TryFocusPostedCard(
                card, gameKey, railIndex, watchedGame, attemptsLeft, generation));
            return;
        }

        ScheduleStreamExitFocusAfterLayout(gameKey, railIndex, watchedGame, attemptsLeft, generation);
    }

    /// <summary>
    /// The card has not been created yet. The rows list is already attached,
    /// so <c>View.Post</c> would drain the budget before the next traversal.
    /// One global-layout callback is one layout pass. A quiet hierarchy never
    /// lays out, so a choreographer frame is armed beside that listener and
    /// either signal consumes one attempt. Cancel drops both. If the list is
    /// not in a window yet, the frame alone is the same kind of wait.
    /// </summary>
    private void ScheduleStreamExitFocusAfterLayout(
        string gameKey, int railIndex, bool watchedGame, int attemptsLeft, int generation)
    {
        _streamExitLayoutPass?.Cancel();
        _streamExitLayoutPass = null;

        var attempt = new StreamExitFocusRetry.LayoutAttempt();
        IStreamExitPass? mine = null;

        void Continue()
        {
            if (generation != _streamExitFocusGeneration || !attempt.TryContinue())
            {
                return;
            }

            if (ReferenceEquals(_streamExitLayoutPass, mine))
            {
                _streamExitLayoutPass = null;
            }

            mine?.Cancel();
            FocusCardAfterStreamExit(gameKey, railIndex, watchedGame, attemptsLeft - 1, generation);
        }

        if (RowsList.Handler?.PlatformView is RecyclerView recycler
            && recycler.ViewTreeObserver is { IsAlive: true } observer)
        {
            var layout = new StreamExitLayoutPass(observer, Continue);
            var choreographer = global::Android.Views.Choreographer.Instance;
            mine = choreographer is null
                ? layout
                : new StreamExitPassPair(layout, new StreamExitFramePass(choreographer, Continue));
            _streamExitLayoutPass = mine;
            return;
        }

        var frameClock = global::Android.Views.Choreographer.Instance;
        if (frameClock is null)
        {
            FocusAttachedCardThenReleaseMenu(gameKey, railIndex);
            return;
        }

        mine = new StreamExitFramePass(frameClock, Continue);
        _streamExitLayoutPass = mine;
    }

    private void TryFocusPostedCard(
        AView card, string gameKey, int railIndex, bool watchedGame, int attemptsLeft, int generation)
    {
        if (generation != _streamExitFocusGeneration)
        {
            return;
        }

        if (card is { IsAttachedToWindow: true, IsShown: true } && card.Width > 0)
        {
            if (!watchedGame)
            {
                ResetStripToStart(card);
            }

            if (card.RequestFocus())
            {
                TvDpadFocusRouter.NoteCardFocused(card);
                if (railIndex == 0)
                {
                    TvDpadFocusRouter.PostTopAlignContaining(card);
                }

                TvDpadFocusRouter.ReleaseHeaderFocusForInitialCard();
                return;
            }
        }

        FocusCardAfterStreamExit(gameKey, railIndex, watchedGame, attemptsLeft - 1, generation);
    }

    /// <summary>
    /// Last chance after the layout budget. If the card is in a window, it
    /// gets focus. Menu is released either way so the header stays reachable.
    /// </summary>
    private static void FocusAttachedCardThenReleaseMenu(string gameKey, int railIndex)
    {
        if (TvCardFocusRegistry.TryGetAttached(gameKey) is { } attached && attached.RequestFocus())
        {
            TvDpadFocusRouter.NoteCardFocused(attached);
            if (railIndex == 0)
            {
                TvDpadFocusRouter.PostTopAlignContaining(attached);
            }
        }

        TvDpadFocusRouter.ReleaseHeaderFocusForInitialCard();
    }

    private void EnsureRailVisible(int railIndex)
    {
        if (RowsList.Handler?.PlatformView is not RecyclerView recycler)
        {
            return;
        }

        if (recycler.GetLayoutManager() is LinearLayoutManager linear)
        {
            linear.ScrollToPositionWithOffset(railIndex, 0);
        }
        else
        {
            recycler.ScrollToPosition(railIndex);
        }
    }

    private static void ResetStripToStart(AView card)
    {
        for (var parent = card.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is HorizontalScrollView strip)
            {
                strip.ScrollTo(0, 0);
                return;
            }
        }
    }

    partial void OnTvViewModelWired(HomeViewModel? vm)
    {
        if (ReferenceEquals(_tvTrapViewModel, vm))
        {
            return;
        }

        if (_tvTrapViewModel != null)
        {
            _tvTrapViewModel.PropertyChanged -= OnTvTrapViewModelPropertyChanged;
            _tvTrapViewModel.GamesUpdated -= OnTvGamesUpdatedForHeaderFocus;
        }

        _tvTrapViewModel = vm;
        if (_tvTrapViewModel != null)
        {
            _tvTrapViewModel.PropertyChanged += OnTvTrapViewModelPropertyChanged;
            _tvTrapViewModel.GamesUpdated += OnTvGamesUpdatedForHeaderFocus;
        }
    }

    private void OnTvGamesUpdatedForHeaderFocus(int gameCount)
    {
        // Empty delivered board never arms RequestsInitialFocus — release the
        // Menu hold so Settings remains reachable.
        if (gameCount == 0 && ViewModel is { IsContentLoading: false })
        {
            TvDpadFocusRouter.ReleaseHeaderFocusForInitialCard();
        }
    }

    // ------------------------------------------------------- header button --

    private void OnMenuButtonHandlerChanged(object? sender, EventArgs e)
    {
        if (!IsTelevision())
        {
            return;
        }

        if (MenuButton.Handler?.PlatformView is not AView native
            || ReferenceEquals(_wiredMenuButton, native))
        {
            return;
        }

        UnwireMenuButton();
        _wiredMenuButton = native;
        native.SoundEffectsEnabled = false;
        native.FocusChange += OnTvItemFocusChange;
        // Before any rail exists Android's default search lands on Menu
        // (field: Menu selected, first rail not on screen). Hold Menu out of
        // the focus order until the first card autofocus finishes — or until
        // an empty board settles (see OnTvGamesUpdated).
        TvDpadFocusRouter.HoldHeaderFocusForInitialCard();
        TvDpadFocusRouter.RegisterHeaderTarget(native);
    }

    private void UnwireMenuButton()
    {
        if (_wiredMenuButton is null)
        {
            return;
        }

        _wiredMenuButton.FocusChange -= OnTvItemFocusChange;
        _wiredMenuButton = null;
    }

    // ---------------------------------------------------- menu focus trap --

    private void OnTvTrapViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HomeViewModel.IsMenuOpen) || !IsTelevision())
        {
            return;
        }

        if (ViewModel?.IsMenuOpen == true)
        {
            OpenMenuTrap();
        }
        else
        {
            CloseMenuTrap();
        }
    }

    private void OpenMenuTrap()
    {
        // The activity-level key owner seals the trap while this is set: a
        // direction key that no trap item consumed is swallowed there, so
        // the default focus search can never move focus behind the scrim.
        TvDpadFocusRouter.MenuTrapOpen = true;
        _menuFocusMemory.OnTrapOpened(TvDpadFocusRouter.LastFocusedCard());
        FocusFirstMenuItemWhenShown(MenuTrapFocusRetryFrames);
    }

    /// <summary>
    /// The panel just flipped visible: its native views may not be shown or
    /// laid out on this frame (the league toggles were refreshed on open and
    /// materialize a frame or two later). Retry per dispatcher tick until the
    /// first focusable takes focus.
    /// </summary>
    private void FocusFirstMenuItemWhenShown(int attemptsLeft)
    {
        if (attemptsLeft <= 0 || ViewModel?.IsMenuOpen != true)
        {
            return;
        }

        var items = CollectAndWireTrapItems();
        if (items.Count > 0 && items[0].RequestFocus())
        {
            return;
        }

        Dispatcher.Dispatch(() => FocusFirstMenuItemWhenShown(attemptsLeft - 1));
    }

    private void CloseMenuTrap()
    {
        TvDpadFocusRouter.MenuTrapOpen = false;
        foreach (var item in _wiredTrapItems)
        {
            item.KeyPress -= OnMenuItemKeyPress;
            item.FocusChange -= OnTvItemFocusChange;
            ApplyTvFocusVisual(item, focused: false);
        }

        _wiredTrapItems.Clear();

        // Restore to the pre-menu card; a card recycled/detached while the
        // menu was open falls back to the header Menu button so focus never
        // silently vanishes.
        var restore = _menuFocusMemory.OnTrapClosed(static token =>
            token is AView { IsAttachedToWindow: true, IsShown: true }) as AView
            ?? _wiredMenuButton;

        // Post: the scrim/panel are mid-teardown on this callback; focus
        // lands cleanly on the next frame.
        restore?.Post(() => restore.RequestFocus());
    }

    /// <summary>
    /// The panel's shown focusables in traversal (visual) order, freshly
    /// collected — items can materialize a frame after open. Newly seen views
    /// get the trap wiring (key ownership, focus visuals, sound opt-out);
    /// already-wired ones keep it until the trap closes.
    /// </summary>
    private List<AView> CollectAndWireTrapItems()
    {
        var items = new List<AView>();
        if (MenuPanel.Handler?.PlatformView is AView { IsShown: true } panel)
        {
            CollectShownFocusables(panel, items);
        }

        foreach (var item in items)
        {
            if (_wiredTrapItems.Contains(item))
            {
                continue;
            }

            _wiredTrapItems.Add(item);
            item.SoundEffectsEnabled = false;
            item.KeyPress += OnMenuItemKeyPress;
            item.FocusChange += OnTvItemFocusChange;
        }

        return items;
    }

    /// <summary>
    /// Depth-first shown focusable leaves. ViewGroups that do not block
    /// descendants are traversed rather than collected (the league list's
    /// NestedScrollView is itself focusable but must never be a D-pad stop —
    /// its checkboxes are).
    /// </summary>
    private static void CollectShownFocusables(AView view, List<AView> into)
    {
        if (view is AViewGroup group
            && group.DescendantFocusability != global::Android.Views.DescendantFocusability.BlockDescendants)
        {
            for (var i = 0; i < group.ChildCount; i++)
            {
                if (group.GetChildAt(i) is { } child)
                {
                    CollectShownFocusables(child, into);
                }
            }

            return;
        }

        if (view is { Focusable: true, IsShown: true })
        {
            into.Add(view);
        }
    }

    private void OnMenuItemKeyPress(object? sender, AView.KeyEventArgs e)
    {
        if (e.Event?.Action != global::Android.Views.KeyEventActions.Down
            || sender is not AView view)
        {
            e.Handled = false;
            return;
        }

        switch (e.KeyCode)
        {
            case Keycode.DpadUp:
            case Keycode.DpadDown:
            {
                // Re-collect per move: visibility can change while open and
                // the wired set only grows. Router-owned move (silent),
                // clamped at both ends — the key is consumed either way, so
                // focus is trapped inside the panel.
                var items = CollectAndWireTrapItems();
                var index = items.IndexOf(view);
                var next = TvMenuFocusMemory.MoveIndex(
                    index, items.Count, forward: e.KeyCode == Keycode.DpadDown);
                if (next != index && next >= 0 && next < items.Count)
                {
                    items[next].RequestFocus();
                }

                e.Handled = true;
                break;
            }

            case Keycode.DpadLeft:
            case Keycode.DpadRight:
                // No horizontal concept inside the panel; consuming keeps
                // focus from escaping to the cards behind the scrim.
                e.Handled = true;
                break;

            default:
                // DPAD_CENTER toggles/clicks natively; Back reaches the
                // activity (HomeHostPage closes the menu → trap restores).
                e.Handled = false;
                break;
        }
    }

    // ------------------------------------------------------ focus visuals --

    private void OnTvItemFocusChange(object? sender, AView.FocusChangeEventArgs e)
    {
        if (sender is AView view)
        {
            ApplyTvFocusVisual(view, e.HasFocus);
        }
    }

    /// <summary>
    /// 10-foot focused state for header/menu controls, native-side so every
    /// widget kind (button, checkbox, switch) gets the identical treatment:
    /// a scale bump plus the bright TV focus ring drawn as a foreground
    /// overlay. Transform/overlay only — no layout or background mutation.
    /// </summary>
    private static void ApplyTvFocusVisual(AView view, bool focused)
    {
        view.ScaleX = focused ? 1.08f : 1f;
        view.ScaleY = focused ? 1.08f : 1f;

        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return;
        }

        if (!focused)
        {
            view.Foreground = null;
            return;
        }

        var density = view.Resources?.DisplayMetrics?.Density ?? 1f;
        var ring = new GradientDrawable();
        ring.SetColor(AColor.Transparent.ToArgb());
        // Same brush as the card TV focus ring (#E2ECFF) so the menu reads
        // as one focus system.
        ring.SetStroke((int)(3 * density), AColor.Rgb(0xE2, 0xEC, 0xFF));
        ring.SetCornerRadius(10 * density);
        view.Foreground = ring;
    }

    private interface IStreamExitPass
    {
        void Cancel();
    }

    /// <summary>
    /// Layout listener and frame callback for one attempt. Cancel removes both.
    /// </summary>
    private sealed class StreamExitPassPair : IStreamExitPass
    {
        private readonly IStreamExitPass _layout;
        private readonly IStreamExitPass _frame;

        public StreamExitPassPair(IStreamExitPass layout, IStreamExitPass frame)
        {
            _layout = layout;
            _frame = frame;
        }

        public void Cancel()
        {
            _layout.Cancel();
            _frame.Cancel();
        }
    }

    /// <summary>
    /// One rows-list layout pass. The listener is removed the first time it
    /// runs, or when a newer stream exit replaces this wait.
    /// </summary>
    private sealed class StreamExitLayoutPass : IStreamExitPass
    {
        private readonly global::Android.Views.ViewTreeObserver _observer;
        private readonly LayoutListener _listener;
        private bool _finished;

        public StreamExitLayoutPass(global::Android.Views.ViewTreeObserver observer, Action next)
        {
            _observer = observer;
            _listener = new LayoutListener(() =>
            {
                if (!Finish())
                {
                    return;
                }

                next();
            });
            observer.AddOnGlobalLayoutListener(_listener);
        }

        public void Cancel() => Finish();

        private bool Finish()
        {
            if (_finished)
            {
                return false;
            }

            _finished = true;
            if (_observer.IsAlive)
            {
                _observer.RemoveOnGlobalLayoutListener(_listener);
            }

            return true;
        }

        private sealed class LayoutListener : Java.Lang.Object, global::Android.Views.ViewTreeObserver.IOnGlobalLayoutListener
        {
            private readonly Action _onLayout;

            public LayoutListener(Action onLayout) => _onLayout = onLayout;

            public void OnGlobalLayout() => _onLayout();
        }
    }

    /// <summary>
    /// One choreographer frame. Armed beside the layout listener so a quiet
    /// hierarchy still consumes an attempt, and alone when the rows list is
    /// not in a window yet.
    /// <see cref="global::Android.Views.Choreographer.IFrameCallback"/>
    /// runs once per frame, not once per looper message.
    /// </summary>
    private sealed class StreamExitFramePass : IStreamExitPass
    {
        private readonly global::Android.Views.Choreographer _choreographer;
        private readonly FrameListener _listener;
        private bool _finished;

        public StreamExitFramePass(global::Android.Views.Choreographer choreographer, Action next)
        {
            _choreographer = choreographer;
            _listener = new FrameListener(() =>
            {
                if (!Finish())
                {
                    return;
                }

                next();
            });
            choreographer.PostFrameCallback(_listener);
        }

        public void Cancel() => Finish();

        private bool Finish()
        {
            if (_finished)
            {
                return false;
            }

            _finished = true;
            _choreographer.RemoveFrameCallback(_listener);
            return true;
        }

        private sealed class FrameListener : Java.Lang.Object, global::Android.Views.Choreographer.IFrameCallback
        {
            private readonly Action _onFrame;

            public FrameListener(Action onFrame) => _onFrame = onFrame;

            public void DoFrame(long frameTimeNanos) => _onFrame();
        }
    }
}
#endif
