using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;
using System.Threading;
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
                            await DisplayAlert("Refresh", "Refreshing background data...", "OK");
                            // Do not perform heavy work on UI thread; any real refresh should call services asynchronously
                        };

                        var stack = new StackLayout
                        {
                            VerticalOptions = LayoutOptions.CenterAndExpand,
                            HorizontalOptions = LayoutOptions.FillAndExpand,
                            Spacing = 12,
                            Children = { label, help, refresh }
                        };

                        this.BackgroundColor = Colors.Black;
                        this.Content = stack;
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
        /// Returns true when a real control was clicked so the key can be consumed (avoids double-toggle).
        /// </summary>
        public bool TryClickFocusedWebElement()
        {
            try
            {
                if (blazorWebView?.Handler?.PlatformView is not Android.Webkit.WebView web)
                {
                    return false;
                }

                // Only activate real controls — never treat a random focused node as success.
                const string js =
                    "(function(){var el=document.activeElement;" +
                    "if(!el)return 'none';" +
                    "var tag=(el.tagName||'').toLowerCase();" +
                    "var role=(el.getAttribute('role')||'').toLowerCase();" +
                    "if(tag==='button'||tag==='a'||role==='button')" +
                    "{el.click();return tag;}" +
                    "return 'skip:'+tag;})();";

                string? jsResult = null;
                using var done = new ManualResetEventSlim(false);
                web.EvaluateJavascript(js, new JsStringCallback(value =>
                {
                    jsResult = value;
                    done.Set();
                }));

                if (!done.Wait(TimeSpan.FromMilliseconds(250)))
                {
                    Console.WriteLine("[MainPage] TryClickFocusedWebElement timed out waiting for JS");
                    return false;
                }

                // EvaluateJavascript string results are JSON-encoded (e.g. "\"button\"").
                var normalized = (jsResult ?? string.Empty).Trim().Trim('"');
                var clicked = normalized is "button" or "a";
                Console.WriteLine($"[MainPage] TryClickFocusedWebElement result={normalized} clicked={clicked}");
                return clicked;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainPage] TryClickFocusedWebElement failed: {ex.Message}");
                return false;
            }
        }

        private sealed class JsStringCallback : Java.Lang.Object, Android.Webkit.IValueCallback
        {
            private readonly Action<string?> _onResult;

            public JsStringCallback(Action<string?> onResult) => _onResult = onResult;

            public void OnReceiveValue(Java.Lang.Object? value) => _onResult(value?.ToString());
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
