using System.ComponentModel;
using VardyParty.Presentation;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// The homepage match-event toast overlay. Everything here is event-driven
/// and finite (TV idle invariant): one enter slide/fade per presentation, one
/// awaited dismiss delay, one exit slide/fade — no recurring timers, and the
/// whole overlay is input-transparent and never focusable. The queue/dismiss
/// rules live in <see cref="MatchEventToastViewModel"/> (clock-injectable,
/// unit-tested); this view only executes them.
/// </summary>
public partial class MatchEventToastView : ContentView
{
    private const uint EnterMs = 200;
    private const uint ExitMs = 160;
    private const double SlideDistance = 18;

    private MatchEventToastViewModel? _wired;
    private HomeLayoutState? _observedLayout;

    public MatchEventToastView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private MatchEventToastViewModel? ViewModel => BindingContext as MatchEventToastViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (_wired != null)
        {
            _wired.Presented -= OnPresented;
            _wired = null;
        }

        if (_observedLayout != null)
        {
            _observedLayout.PropertyChanged -= OnLayoutChanged;
            _observedLayout = null;
        }

        if (ViewModel is { } vm)
        {
            _wired = vm;
            _wired.Presented += OnPresented;
            _observedLayout = vm.Layout;
            _observedLayout.PropertyChanged += OnLayoutChanged;
            ApplyPlacement();
        }
    }

    private void OnUnloaded(object? sender, EventArgs e) => ToastCard.CancelAnimations();

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(HomeLayoutState.Class))
        {
            ApplyPlacement();
        }
    }

    /// <summary>
    /// Top-right within the safe area on Desktop/TV (the page-padding margin
    /// is bound in XAML); bottom-centre on the phone classes, where the top
    /// edge is status-bar/notch territory and thumbs rest low.
    /// </summary>
    private bool IsBottomPlaced =>
        _wired?.Layout.Class is HomeLayoutClass.PhoneLandscape or HomeLayoutClass.PhonePortrait;

    private void ApplyPlacement()
    {
        if (IsBottomPlaced)
        {
            ToastCard.VerticalOptions = LayoutOptions.End;
            ToastCard.HorizontalOptions = LayoutOptions.Center;
        }
        else
        {
            ToastCard.VerticalOptions = LayoutOptions.Start;
            ToastCard.HorizontalOptions = LayoutOptions.End;
        }
    }

    /// <summary>
    /// One toast's full life: slide/fade in, wait out the show duration, then
    /// (token-guarded, so a superseded presentation can never dismiss the
    /// wrong toast) slide/fade out and advance the queue.
    /// </summary>
    private async void OnPresented(MatchEventToastItem item)
    {
        try
        {
            if (_wired is not { } vm)
            {
                return;
            }

            var token = vm.PresentationToken;
            var offset = IsBottomPlaced ? SlideDistance : -SlideDistance;

            ToastCard.CancelAnimations();
            if (IsLoaded)
            {
                ToastCard.Opacity = 0;
                ToastCard.TranslationY = offset;
                await Task.WhenAll(
                    ToastCard.FadeToAsync(1, EnterMs, Easing.CubicOut),
                    ToastCard.TranslateToAsync(0, 0, EnterMs, Easing.CubicOut));
            }
            else
            {
                ToastCard.Opacity = 1;
                ToastCard.TranslationY = 0;
            }

            // One-shot, finite; resumes on the UI thread's context.
            await Task.Delay(MatchEventToastViewModel.ShowDuration);

            if (!vm.TryBeginDismiss(token))
            {
                return;
            }

            if (IsLoaded)
            {
                await Task.WhenAll(
                    ToastCard.FadeToAsync(0, ExitMs, Easing.CubicIn),
                    ToastCard.TranslateToAsync(0, offset, ExitMs, Easing.CubicIn));
            }

            vm.CompleteDismiss(token);
        }
        catch
        {
            // Toast chrome must never take the homepage down; a failed
            // animation just leaves the binding-driven visibility in charge.
        }
    }
}
