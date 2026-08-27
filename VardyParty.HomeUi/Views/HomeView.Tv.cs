#if ANDROID
using System.ComponentModel;
using Android.Graphics.Drawables;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;
using Keycode = Android.Views.Keycode;

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

    private readonly TvMenuFocusMemory _menuFocusMemory = new();
    private readonly List<AView> _wiredTrapItems = new();
    private AView? _wiredMenuButton;
    private HomeViewModel? _tvTrapViewModel;

    partial void WireTvHeaderFocus()
    {
        MenuButton.HandlerChanged += OnMenuButtonHandlerChanged;
        OnMenuButtonHandlerChanged(MenuButton, EventArgs.Empty);
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
        }

        _tvTrapViewModel = vm;
        if (_tvTrapViewModel != null)
        {
            _tvTrapViewModel.PropertyChanged += OnTvTrapViewModelPropertyChanged;
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
        native.Focusable = true;
        native.FocusableInTouchMode = false;
        // Same uniform opt-out as the cards: DPAD_CENTER's performClick must
        // not play the system click alongside our UI tick.
        native.SoundEffectsEnabled = false;
        native.KeyPress += OnMenuButtonKeyPress;
        native.FocusChange += OnTvItemFocusChange;
        TvDpadFocusRouter.RegisterHeaderTarget(native);
    }

    private void UnwireMenuButton()
    {
        if (_wiredMenuButton is null)
        {
            return;
        }

        _wiredMenuButton.KeyPress -= OnMenuButtonKeyPress;
        _wiredMenuButton.FocusChange -= OnTvItemFocusChange;
        _wiredMenuButton = null;
    }

    private void OnMenuButtonKeyPress(object? sender, AView.KeyEventArgs e)
    {
        if (e.Event?.Action != global::Android.Views.KeyEventActions.Down)
        {
            e.Handled = false;
            return;
        }

        switch (e.KeyCode)
        {
            case Keycode.DpadDown:
                // Back to the card focused before the header round trip
                // (router-owned: silent). Fall through to the default search
                // only when no card has held focus yet.
                e.Handled = TvDpadFocusRouter.TryFocusLastCard();
                break;

            case Keycode.DpadUp:
            case Keycode.DpadLeft:
            case Keycode.DpadRight:
                // The header is a single focus stop: nothing above it, and
                // the crest (left) stays skipped. Consume so the default
                // search cannot wander (or play its navigation click).
                e.Handled = true;
                break;

            default:
                // DPAD_CENTER/Enter falls through to the native click.
                e.Handled = false;
                break;
        }
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
}
#endif
