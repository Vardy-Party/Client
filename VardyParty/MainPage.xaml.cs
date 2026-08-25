using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;
#if WINDOWS
using VardyParty.Platforms.Windows;
using WinUiWindow = Microsoft.UI.Xaml.Window;
#endif

namespace VardyParty
{
    public partial class MainPage : ContentPage
    {
        public static MainPage? Instance { get; private set; }

        /// <summary>
        /// True while the WinUI native player has replaced the MAUI/Blazor window content.
        /// Blazor must not render during this period or the WebView host can crash.
        /// </summary>
        public static bool IsNativePlayerActive { get; private set; }

        public static void SetNativePlayerActive(bool active) => IsNativePlayerActive = active;

        public BlazorWebView? BlazorWebView => blazorWebView;

        public MainPage()
        {
            Console.WriteLine("[MainPage] ctor start");
            Console.WriteLine($"[MainPage] IsTv={MauiProgram.IsTv}, IsWebViewAvailable={MauiProgram.IsWebViewAvailable}");

            try
            {
                InitializeComponent();
                Console.WriteLine("[MainPage] InitializeComponent completed");
                Instance = this;

                // Wire BlazorWebView lifecycle events when present
                try
                {
                    if (blazorWebView != null)
                    {
                        Console.WriteLine("[MainPage] BlazorWebView found, wiring events");
                        blazorWebView.BlazorWebViewInitializing += (s, e) =>
                        {
                            Console.WriteLine("[MainPage] BlazorWebViewInitializing");
                        };
                        blazorWebView.BlazorWebViewInitialized += (s, e) =>
                        {
                            Console.WriteLine("[MainPage] BlazorWebViewInitialized - SUCCESS!");
#if ANDROID
                            try
                            {
                                if (e.WebView is Android.Webkit.WebView web)
                                {
                                    web.Focusable = true;
                                    web.FocusableInTouchMode = true;
                                    web.RequestFocus();
                                    // Helps D-pad move between tabindex=0 game cards on Android TV.
                                    web.Settings.SetSupportMultipleWindows(false);
                                    Console.WriteLine($"[MainPage] WebView focusable (IsTv={MauiProgram.IsTv})");
                                }
                            }
                            catch (Exception focusEx)
                            {
                                Console.WriteLine($"[MainPage] WebView focus setup failed: {focusEx.Message}");
                            }
#endif
                        };
                        blazorWebView.UrlLoading += (s, e) =>
                        {
                            Console.WriteLine($"[MainPage] UrlLoading: {e.Url}");
                        };
                    }
                    else
                    {
                        Console.WriteLine("[MainPage] BlazorWebView is NULL!");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainPage] Failed wiring BlazorWebView events: {ex.Message}\n{ex.StackTrace}");
                }

                // On Android TV devices without a usable WebView, show a simple native placeholder
                // instead of the Blazor content to avoid a black screen.
                try
                {
                    if (MauiProgram.IsTv && !MauiProgram.IsWebViewAvailable)
                    {
                        Console.WriteLine("[MainPage] TV mode without WebView - showing fallback UI");

                        // Build a minimal native UI
                        var label = new Label
                        {
                            Text = "Vardy Party\n(TV mode - web UI unavailable)",
                            HorizontalTextAlignment = TextAlignment.Center,
                            VerticalTextAlignment = TextAlignment.Center,
                            FontSize = 20,
                            TextColor = Colors.White
                        };

                        var help = new Label
                        {
                            Text = "Use a mobile/desktop device for full UI.\nYou can still play streams via the native player when available.",
                            HorizontalTextAlignment = TextAlignment.Center,
                            VerticalTextAlignment = TextAlignment.Center,
                            FontSize = 14,
                            TextColor = Colors.LightGray
                        };

                        var refresh = new Button
                        {
                            Text = "Refresh",
                            HorizontalOptions = LayoutOptions.Center,
                            VerticalOptions = LayoutOptions.Center,
                        };

                        refresh.Clicked += async (_, __) =>
                        {
                            Console.WriteLine("[MainPage] Refresh clicked (fallback)");
                            await DisplayAlertAsync("Refresh", "Refreshing background data...", "OK");
                            // Do not perform heavy work on UI thread; any real refresh should call services asynchronously
                        };

                        var stack = new VerticalStackLayout
                        {
                            HorizontalOptions = LayoutOptions.Center,
                            VerticalOptions = LayoutOptions.Center,
                            Spacing = 12,
                            Children = { label, help, refresh }
                        };

                        this.BackgroundColor = Colors.Black;
                        this.Content = new Grid { Children = { stack } };
                    }
                    else
                    {
                        Console.WriteLine("[MainPage] WebView available or non-TV - using Blazor UI");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainPage] Fallback evaluation failed: {ex.Message}\n{ex.StackTrace}");
                    // Ignore any fallback errors and keep original Blazor view if something goes wrong
                }

                Console.WriteLine("[MainPage] ctor end");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainPage] CRITICAL ERROR in constructor: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            Console.WriteLine("[MainPage] OnAppearing");
#if WINDOWS
            if (Window?.Handler?.PlatformView is WinUiWindow nativeWindow)
            {
                WindowsWindowChrome.ApplyMainWindowChrome(nativeWindow, Window.Handler.MauiContext);
            }
#endif
        }

        public bool TryGoBackInWebView()
        {
#if ANDROID
            return false; // explicit navigation overrides history
#else
            return false;
#endif
        }

#if ANDROID
        /// <summary>
        /// Clicks the currently focused DOM element in the Blazor WebView.
        /// Used for Android TV remotes where OK/Enter focuses a control but does not fire click.
        /// Returns true when the click script was scheduled (caller should consume the key).
        /// Must not block the UI thread waiting for EvaluateJavascript — that deadlocks the
        /// callback and made every TV OK time out after 250ms with no click.
        /// </summary>
        public bool TryClickFocusedWebElement()
        {
            try
            {
                if (blazorWebView?.Handler?.PlatformView is not Android.Webkit.WebView web)
                {
                    return false;
                }

                // closest() covers focus on children inside buttons / role=button game cards.
                const string js =
                    "(function(){var el=document.activeElement;" +
                    "if(!el)return 'none';" +
                    "var t=el.closest('button,a,[role=\"button\"]');" +
                    "if(!t)return 'skip';" +
                    "t.click();" +
                    "try{t.scrollIntoView({block:'nearest',inline:'nearest'});}catch(e){}" +
                    "return (t.tagName||'').toLowerCase();})();";

                web.EvaluateJavascript(js, JsLogCallback.Instance);
                Console.WriteLine("[MainPage] TryClickFocusedWebElement scheduled");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainPage] TryClickFocusedWebElement failed: {ex.Message}");
                return false;
            }
        }

        public void RestoreWebViewFocus()
        {
            try
            {
                if (blazorWebView?.Handler?.PlatformView is not Android.Webkit.WebView web)
                {
                    return;
                }

                web.Focusable = true;
                web.FocusableInTouchMode = true;
                web.RequestFocus();
                Console.WriteLine("[MainPage] RestoreWebViewFocus requested");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainPage] RestoreWebViewFocus failed: {ex.Message}");
            }
        }

        private sealed class JsLogCallback : Java.Lang.Object, Android.Webkit.IValueCallback
        {
            public static readonly JsLogCallback Instance = new();

            public void OnReceiveValue(Java.Lang.Object? value)
            {
                var normalized = (value?.ToString() ?? string.Empty).Trim().Trim('"');
                Console.WriteLine($"[MainPage] TryClickFocusedWebElement result={normalized}");
            }
        }
#endif

        public void NavigateToRoute(string route)
        {
#if ANDROID
            if (string.IsNullOrWhiteSpace(route)) return;
            Console.WriteLine($"[MainPage] NavigateToRoute {route}");
            if (blazorWebView?.Handler?.PlatformView is Android.Webkit.WebView web)
            {
                var target = route.StartsWith("/") ? $"https://0.0.0.0{route}" : route;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Console.WriteLine($"[MainPage] Loading URL {target}");
                    web.LoadUrl(target);
                });
            }
            else
            {
                Console.WriteLine("[MainPage] NavigateToRoute called but WebView not ready");
            }
#endif
        }
    }
}
