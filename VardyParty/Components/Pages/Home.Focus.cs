using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using VardyParty.Presentation;

namespace VardyParty.Components.Pages;

public partial class Home
{
    private GameCard?[] _cardRefs = [];
    private int _focusedCardIndex = -1;
    private bool focusPending = true;
    private bool _tvGridHasBeenShown;
    private bool signInContinueFocusPending;
    private bool authCancelFocusPending;
    private ElementReference signInContinueButtonRef;
    private ElementReference authCancelButtonRef;

    private bool ShouldFocusCard(int idx, bool anySelected, bool isSelected)
    {
        if (!focusPending)
        {
            return false;
        }

        if (MauiProgram.IsTv)
        {
            return !anySelected && (_focusedCardIndex >= 0 ? idx == _focusedCardIndex : idx == 0);
        }

        return !anySelected && idx == 0;
    }

    private async Task EscapeGridToMenu(int fromIndex = 0)
    {
        // Record which card triggered the escape so ArrowRight from menu returns to it.
        // Clear pending autofocus so the grid cannot steal D-pad while the menu is focused.
        _focusedCardIndex = fromIndex;
        focusPending = false;
        if (appMenu != null)
            await appMenu.FocusMenuButtonAsync();
    }

    private async Task FocusCardAsync(int index)
    {
        if (index < 0 || index >= games.Count) return;
        _focusedCardIndex = index;
        if (index < _cardRefs.Length && _cardRefs[index] != null)
        {
            await _cardRefs[index]!.FocusAsync();
        }
        else
        {
            // Ref not available yet (e.g. after a games update) — use focusPending
            // to trigger ShouldFocus on the next render.
            focusPending = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    public async Task ReturnFocusToGridAsync()
    {
        // Menu round-trip is a one-shot FocusAsync. Do not leave ShouldFocus
        // armed — that is the path that lets D-pad move after visiting the menu.
        var target = _focusedCardIndex >= 0 && _focusedCardIndex < games.Count
            ? _focusedCardIndex : 0;
        focusPending = false;
        await FocusCardAsync(target);
    }

    private void ApplyCatalogRefreshFocus()
    {
        if (!MauiProgram.IsTv || isResolvingStreams || selectedGame != null)
            return;

        var count = games.Count;
        if (count == 0)
        {
            _tvGridHasBeenShown = false;
            return;
        }

        if (TvGridFocusPolicy.ShouldArmAutofocusOnCatalogRefresh(
                _tvGridHasBeenShown, _focusedCardIndex, count))
        {
            _focusedCardIndex = TvGridFocusPolicy.ClampFocusedIndex(_focusedCardIndex, count);
            focusPending = true;
        }

        _tvGridHasBeenShown = true;
    }

    private void ResizeCardRefs()
    {
        if (_cardRefs.Length == games.Count) return;
        // Preserve existing refs for indices that are still valid.
        var next = new GameCard?[games.Count];
        Array.Copy(_cardRefs, next, Math.Min(_cardRefs.Length, next.Length));
        _cardRefs = next;
    }

    private void OnCardFocusDelivered()
    {
        focusPending = false;
    }

    private async Task ApplyPendingAuthFocusAsync()
    {
        // Android TV / D-pad: put focus on Sign in — Continue when that CTA is visible.
        // Without this, the header menu button typically owns first focus.
        if (signInContinueFocusPending
            && !isAuthenticated
            && !isAuthenticating
            && deviceCode == null)
        {
            try
            {
                await signInContinueButtonRef.FocusAsync();
                signInContinueFocusPending = false;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[Home] Sign-in continue focus not ready yet");
            }
        }

        if (authCancelFocusPending && !isAuthenticated && deviceCode != null)
        {
            try
            {
                await authCancelButtonRef.FocusAsync();
                authCancelFocusPending = false;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[Home] Auth cancel focus not ready yet");
            }
        }
    }
}
