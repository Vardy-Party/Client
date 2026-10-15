#if ANDROID
using System;
using Android.Widget;
using AndroidX.Media3.UI;
using Microsoft.Extensions.Logging;
using VardyParty.Playback;

namespace VardyParty.Platforms.Android
{
    public partial class NativeVideoActivity
    {
        public void SwipeToNextStream()
        {
            try
            {
                if (_switching is not null)
                {
                    bool wasVisible = _overlayContainer != null && _overlayContainer.Visibility == global::Android.Views.ViewStates.Visible;
                    if (!wasVisible)
                    {
                        _suppressOverlayShow = true;
                    }

                    var switched = DispatchEngine(MediaEngineEvent.UserNext());
                    ShowSwitchFeedbackToast(switched, next: true);

                    if (wasVisible)
                    {
                        ShowOverlayAnimated();
                        if (!_overlayLocked) ScheduleHideOverlay();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] SwipeToNextStream failed");
            }
        }

        public void SwipeToPreviousStream()
        {
            try
            {
                if (_switching != null)
                {
                    bool wasVisible = _overlayContainer != null && _overlayContainer.Visibility == global::Android.Views.ViewStates.Visible;
                    if (!wasVisible)
                    {
                        _suppressOverlayShow = true;
                    }

                    var switched = DispatchEngine(MediaEngineEvent.UserPrevious());
                    ShowSwitchFeedbackToast(switched, next: false);

                    if (wasVisible)
                    {
                        ShowOverlayAnimated();
                        if (!_overlayLocked) ScheduleHideOverlay();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] SwipeToPreviousStream failed");
            }
        }

        private void ShowSwitchFeedbackToast(bool switched, bool next)
        {
            try
            {
                var message = switched
                    ? (next ? "Next stream…" : "Previous stream…")
                    : "No other stream yet";
                Toast.MakeText(this, message, ToastLength.Short)?.Show();
            }
            catch (Exception ex) { LogIgnored("Toast.SwitchFeedback", ex); }
        }

        private void SetupPinchZoom(FrameLayout container, PlayerView playerView)
        {
            try
            {
                playerView.ResizeMode = AspectRatioFrameLayout.ResizeModeFit;
                var scaleGestureDetector = new global::Android.Views.ScaleGestureDetector(this, new PinchZoomListener(container));
                var gestureDetector = new global::Android.Views.GestureDetector(this, new DragGestureListener(this, container));
                container.SetOnTouchListener(new ZoomAndDragTouchListener(scaleGestureDetector, gestureDetector));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to setup pinch-zoom");
            }
        }

        private class ZoomAndDragTouchListener : Java.Lang.Object, global::Android.Views.View.IOnTouchListener
        {
            private readonly global::Android.Views.ScaleGestureDetector _scaleDetector;
            private readonly global::Android.Views.GestureDetector _gestureDetector;

            public ZoomAndDragTouchListener(
                global::Android.Views.ScaleGestureDetector scaleDetector,
                global::Android.Views.GestureDetector gestureDetector)
            {
                _scaleDetector = scaleDetector;
                _gestureDetector = gestureDetector;
            }

            public bool OnTouch(global::Android.Views.View? v, global::Android.Views.MotionEvent? e)
            {
                if (e is null)
                    return false;
                var scaleHandled = _scaleDetector.OnTouchEvent(e);
                var dragHandled = _gestureDetector.OnTouchEvent(e);
                return scaleHandled || dragHandled;
            }
        }

        private class DragGestureListener : global::Android.Views.GestureDetector.SimpleOnGestureListener
        {
            private readonly NativeVideoActivity _activity;
            private readonly FrameLayout _container;
            private float _translationX;
            private float _translationY;

            public DragGestureListener(NativeVideoActivity activity, FrameLayout container)
            {
                _activity = activity;
                _container = container;
            }

            public override bool OnScroll(global::Android.Views.MotionEvent? e1, global::Android.Views.MotionEvent? e2, float distanceX, float distanceY)
            {
                if (_container.ScaleX <= 1.0f) return false;

                _translationX -= distanceX;
                _translationY -= distanceY;

                var scale = _container.ScaleX;
                var maxTranslationX = (_container.Width * (scale - 1f)) / 2f;
                var maxTranslationY = (_container.Height * (scale - 1f)) / 2f;

                _translationX = Math.Max(-maxTranslationX, Math.Min(_translationX, maxTranslationX));
                _translationY = Math.Max(-maxTranslationY, Math.Min(_translationY, maxTranslationY));

                _container.TranslationX = _translationX;
                _container.TranslationY = _translationY;
                return true;
            }

            private const int SwipeThreshold = 100;
            private const int SwipeVelocityThreshold = 100;

            public override bool OnFling(global::Android.Views.MotionEvent? e1, global::Android.Views.MotionEvent? e2, float velocityX, float velocityY)
            {
                if (e1 == null || e2 == null) return false;
                if (_container.ScaleX > 1.0f) return false;

                float diffX = e2.GetX() - e1.GetX();
                float diffY = e2.GetY() - e1.GetY();

                if (Math.Abs(diffX) > Math.Abs(diffY))
                {
                    if (Math.Abs(diffX) > SwipeThreshold && Math.Abs(velocityX) > SwipeVelocityThreshold)
                    {
                        if (diffX > 0)
                        {
                            _activity.RunOnUiThread(() => _activity.SwipeToPreviousStream());
                        }
                        else
                        {
                            _activity.RunOnUiThread(() => _activity.SwipeToNextStream());
                        }
                        return true;
                    }
                }
                return false;
            }
        }

        private class PinchZoomListener : global::Android.Views.ScaleGestureDetector.SimpleOnScaleGestureListener
        {
            private readonly FrameLayout _container;
            private float _scaleFactor = 1.0f;
            private const float MinScale = 1.0f;
            private const float MaxScale = 4.0f;

            public PinchZoomListener(FrameLayout container)
            {
                _container = container;
            }

            public override bool OnScale(global::Android.Views.ScaleGestureDetector? detector)
            {
                if (detector == null) return false;

                _scaleFactor *= detector.ScaleFactor;
                _scaleFactor = Math.Max(MinScale, Math.Min(_scaleFactor, MaxScale));

                _container.PivotX = detector.FocusX;
                _container.PivotY = detector.FocusY;
                _container.ScaleX = _scaleFactor;
                _container.ScaleY = _scaleFactor;

                if (_scaleFactor <= 1.01f)
                {
                    _container.TranslationX = 0f;
                    _container.TranslationY = 0f;
                }

                return true;
            }
        }

        public override bool OnKeyDown(global::Android.Views.Keycode keyCode, global::Android.Views.KeyEvent? e)
        {
            try
            {
                switch (keyCode)
                {
                    case global::Android.Views.Keycode.DpadCenter:
                    case global::Android.Views.Keycode.Enter:
                        if (_isTvDevice)
                        {
                            if (_isMenuVisible)
                            {
                                if (TryActivateFocusedMenuItem())
                                {
                                    return true;
                                }

                                HideMenu();
                                return true;
                            }

                            if (_isInfoVisible)
                            {
                                HideInfoOverlay();
                                return true;
                            }

                            ShowMenu();
                            return true;
                        }

                        if (_isInfoVisible)
                        {
                            HideInfoOverlay();
                        }
                        else
                        {
                            ShowInfoOverlay();
                        }
                        return true;

                    case global::Android.Views.Keycode.DpadRight:
                        try
                        {
                            bool wasVisible = _overlayContainer != null && _overlayContainer.Visibility == global::Android.Views.ViewStates.Visible;
                            if (!wasVisible)
                            {
                                _suppressOverlayShow = true;
                            }

                            var switched = false;
                            if (_switching != null)
                            {
                                switched = DispatchEngine(MediaEngineEvent.UserNext());
                            }

                            ShowSwitchFeedbackToast(switched, next: true);

                            if (wasVisible)
                            {
                                ShowOverlayAnimated();
                                if (!_overlayLocked) ScheduleHideOverlay();
                            }
                        }
                        catch (Exception ex) { LogIgnored("DpadRightSwitch", ex); }
                        return true;

                    case global::Android.Views.Keycode.DpadDown:
                        if (_isScoresTickerVisible)
                        {
                            CycleScoresTickerMode();
                            return true;
                        }
                        break;

                    case global::Android.Views.Keycode.Back:
                        return HandleBackKey();
                }
            }
            catch (Exception ex) { LogIgnored("OnKeyDown", ex); }

            return base.OnKeyDown(keyCode, e);
        }

        long _backHandledAtMs;

        /// <summary>
        /// TV remotes often deliver Back via <see cref="OnBackPressed"/> rather
        /// than <see cref="OnKeyDown"/>. Default Activity.OnBackPressed finishes
        /// the activity — so an open menu never got HideMenu() and the player
        /// (or whole task) looked like it "closed". Same layered dismiss as
        /// OnKeyDown Back.
        /// </summary>
#pragma warning disable CA1422 // OnBackPressed obsolete on API 33+; Activity base still needs this for TV API 28
        public override void OnBackPressed()
        {
            try
            {
                if (HandleBackKey())
                    return;
            }
            catch (Exception ex)
            {
                LogIgnored("OnBackPressed", ex);
                try { base.OnBackPressed(); } catch { }
            }
        }
#pragma warning restore CA1422

        public override bool DispatchKeyEvent(global::Android.Views.KeyEvent? e)
        {
            // Own Back at dispatch time so a focused menu button / PlayerView
            // cannot let the system finish the activity before OnKeyDown runs.
            if (e is
                {
                    Action: global::Android.Views.KeyEventActions.Down,
                    KeyCode: global::Android.Views.Keycode.Back,
                    RepeatCount: 0
                })
            {
                return HandleBackKey();
            }

            return base.DispatchKeyEvent(e);
        }

        /// <summary>
        /// Closes menu / info / ticker, else requests session close.
        /// Debounces duplicate Back delivery (dispatch + OnBackPressed) so one
        /// press cannot close the menu and then exit the player.
        /// </summary>
        private bool HandleBackKey()
        {
            var now = Java.Lang.JavaSystem.CurrentTimeMillis();
            if (now - _backHandledAtMs < 400)
                return true;
            _backHandledAtMs = now;

            if (TryConsumeBackLayer())
                return true;

            try { _switching?.Cleanup(); } catch { }
            DispatchEngine(MediaEngineEvent.UserClose());
            return true;
        }

        /// <summary>
        /// Dismiss in-player overlays first (menu → info → scores ticker).
        /// Returns true when Back was consumed without closing the player.
        /// Presenter owns scores visibility; ApplyChromeState syncs the host ticker.
        /// </summary>
        private bool TryConsumeBackLayer() => EnsureChrome().TryDismissLayer();
    }
}
#endif
