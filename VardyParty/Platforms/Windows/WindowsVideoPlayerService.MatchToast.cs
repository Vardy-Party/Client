#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VardyParty.HomeUi;
using VardyParty.Presentation;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;

namespace VardyParty.Platforms.Windows
{
    public partial class WindowsVideoPlayerService
    {
        /// <summary>
        /// In-playback match-event banner for the Windows player session:
        /// goals / extra time / penalties shown over the video, consuming the
        /// same delivered-event bus as the homepage toast (all gating —
        /// visibility, foreground, "Goal notifications" toggle — is upstream).
        /// NO audio here by design ("playing → toast only"). Artwork comes
        /// from the shared <see cref="IBadgeImageLoader"/> byte cache, so the
        /// banner shows the SAME league logo and team badges as every other
        /// surface. Non-interactive (IsHitTestVisible=false throughout).
        /// </summary>
        private sealed partial class PlayerSession
        {
            private const int MatchToastDurationMs = 4000;
            private const int MatchToastMaxQueued = 3;

            private MatchEventBus? matchEventBus;
            private Action<MatchEvent>? matchEventHandler;
            private Microsoft.UI.Xaml.Controls.Border? matchToastBorder;
            private Microsoft.UI.Xaml.Controls.TextBlock? matchToastPrefix;
            private Microsoft.UI.Xaml.Controls.TextBlock? matchToastText;
            private Microsoft.UI.Xaml.Controls.Image? matchToastLeague;
            private Microsoft.UI.Xaml.Controls.Image? matchToastHome;
            private Microsoft.UI.Xaml.Controls.Image? matchToastAway;
            private Microsoft.UI.Dispatching.DispatcherQueueTimer? matchToastHideTimer;
            private readonly Queue<MatchEvent> matchToastQueue = new();
            private bool matchToastShowing;
            private int matchToastEpoch;

            private void InitializeMatchToastOverlay()
            {
                try
                {
                    matchEventBus = VardyParty.AppServiceProvider.ServiceProvider?
                        .GetService(typeof(MatchEventBus)) as MatchEventBus;
                    if (matchEventBus == null || playerGrid == null) return;

                    Microsoft.UI.Xaml.Controls.Image MakeIcon(double size, double rightMargin) => new()
                    {
                        Width = size,
                        Height = size,
                        Margin = new Microsoft.UI.Xaml.Thickness(0, 0, rightMargin, 0),
                        Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                        IsHitTestVisible = false,
                    };

                    Microsoft.UI.Xaml.Controls.TextBlock MakeText(byte r, byte g, byte b) => new()
                    {
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            global::Windows.UI.Color.FromArgb(0xFF, r, g, b)),
                        FontSize = 15,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = WinVerticalAlignment.Center,
                        TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false,
                    };

                    // [league logo] GOAL · League  [home badge] Home 2–1 Away [away badge]
                    // Each badge sits directly against its own team's name: the
                    // kind/league prefix is a separate text BEFORE the home badge.
                    // The prefix owns the gap (not the badge), so the separation
                    // holds while a badge is loading or missing.
                    matchToastLeague = MakeIcon(22, 8);
                    matchToastPrefix = MakeText(0xAE, 0xB6, 0xC6);
                    matchToastPrefix.Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 12, 0);
                    matchToastPrefix.TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap;
                    matchToastHome = MakeIcon(26, 8);
                    matchToastText = MakeText(0xF3, 0xF4, 0xF6);
                    matchToastText.TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap;
                    matchToastText.MaxLines = 2;
                    matchToastAway = MakeIcon(26, 0);
                    matchToastAway.Margin = new Microsoft.UI.Xaml.Thickness(8, 0, 0, 0);

                    var row = new Microsoft.UI.Xaml.Controls.StackPanel
                    {
                        Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                        VerticalAlignment = WinVerticalAlignment.Center,
                        // Pin LTR: an RTL flow would mirror the row while Latin
                        // team names still read LTR, swapping the badges' teams.
                        FlowDirection = Microsoft.UI.Xaml.FlowDirection.LeftToRight,
                        IsHitTestVisible = false,
                    };
                    row.Children.Add(matchToastLeague);
                    row.Children.Add(matchToastPrefix);
                    row.Children.Add(matchToastHome);
                    row.Children.Add(matchToastText);
                    row.Children.Add(matchToastAway);

                    matchToastBorder = new Microsoft.UI.Xaml.Controls.Border
                    {
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            global::Windows.UI.Color.FromArgb(0xE6, 0x10, 0x15, 0x21)),
                        BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            global::Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
                        CornerRadius = new Microsoft.UI.Xaml.CornerRadius(12),
                        Padding = new Microsoft.UI.Xaml.Thickness(14, 10, 14, 10),
                        HorizontalAlignment = WinHorizontalAlignment.Right,
                        VerticalAlignment = WinVerticalAlignment.Top,
                        Margin = new Microsoft.UI.Xaml.Thickness(0, 24, 32, 0),
                        Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                        IsHitTestVisible = false,
                        Child = row,
                    };

                    playerGrid.Children.Add(matchToastBorder);
                    Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(matchToastBorder, 120);

                    matchEventHandler = OnMatchEventForOverlay;
                    matchEventBus.Published += matchEventHandler;
                }
                catch (Exception ex)
                {
                    _host.LogIgnored("InitializeMatchToastOverlay", ex);
                }
            }

            private void TeardownMatchToastOverlay()
            {
                try
                {
                    if (matchEventBus != null && matchEventHandler != null)
                    {
                        matchEventBus.Published -= matchEventHandler;
                        matchEventHandler = null;
                    }

                    matchToastHideTimer?.Stop();
                    matchToastQueue.Clear();
                    matchToastShowing = false;
                }
                catch (Exception ex)
                {
                    _host.LogIgnored("TeardownMatchToastOverlay", ex);
                }
            }

            /// <summary>Bus publishes on the MAUI UI thread — same dispatcher as this window.</summary>
            private void OnMatchEventForOverlay(MatchEvent matchEvent)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        if (matchToastBorder == null || !playerOverlayAttached) return;
                        if (matchToastQueue.Count >= MatchToastMaxQueued)
                        {
                            matchToastQueue.Dequeue(); // drop-oldest, same as the homepage toast
                        }

                        matchToastQueue.Enqueue(matchEvent);
                        if (!matchToastShowing) ShowNextMatchToast();
                    }
                    catch (Exception ex) { _host.LogIgnored("OnMatchEventForOverlay", ex); }
                });
            }

            private void ShowNextMatchToast()
            {
                if (matchToastBorder == null || matchToastText == null || matchToastPrefix == null || matchToastQueue.Count == 0)
                {
                    matchToastShowing = false;
                    return;
                }

                var matchEvent = matchToastQueue.Dequeue();
                matchToastShowing = true;
                var epoch = ++matchToastEpoch;
                var game = matchEvent.Game;

                matchToastPrefix.Text = $"{MatchEventPresenter.Kind(matchEvent)} · {game.DisplayLeague}";
                matchToastText.Text = MatchEventPresenter.Scoreline(matchEvent);
                ApplyMatchToastTextWidths();

                var loader = VardyParty.AppServiceProvider.ServiceProvider?
                    .GetService(typeof(IBadgeImageLoader)) as IBadgeImageLoader;
                var locator = VardyParty.AppServiceProvider.ServiceProvider?
                    .GetService(typeof(IHomeAssetLocator)) as IHomeAssetLocator;
                // Team badges keep their slot while loading or missing, so the
                // fixture never jumps and the row stays symmetric.
                SetToastImageAsync(matchToastLeague, ResolveLeagueBytesAsync(loader, locator, game), epoch, keepSlot: false);
                SetToastImageAsync(matchToastHome, loader?.LoadRemoteBytesAsync(game.HomeBadgeUrl), epoch, keepSlot: true);
                SetToastImageAsync(matchToastAway, loader?.LoadRemoteBytesAsync(game.AwayBadgeUrl), epoch, keepSlot: true);

                matchToastBorder.Opacity = 0;
                matchToastBorder.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                FadeMatchToast(1, 180);

                matchToastHideTimer ??= CreateMatchToastTimer();
                matchToastHideTimer.Stop();
                matchToastHideTimer.Interval = TimeSpan.FromMilliseconds(MatchToastDurationMs);
                matchToastHideTimer.Start();
            }

            /// <summary>
            /// Cap both texts against the player's current width so the
            /// fixed-size badges are always laid out: the prefix ellipsizes,
            /// the fixture wraps to 2 lines then ellipsizes, and the away badge
            /// is never pushed out of a narrow window.
            /// </summary>
            private void ApplyMatchToastTextWidths()
            {
                if (matchToastPrefix == null || matchToastText == null) return;
                var width = playerGrid?.ActualWidth ?? 0;
                if (width <= 0) width = 1280;

                // Width minus toast margin, padding, three icons and their gaps.
                const double chrome = 32 + 28 + (22 + 26 + 26) + (8 + 12 + 8 + 8);
                var budget = Math.Min(Math.Max(160, width - chrome), 720);
                matchToastPrefix.MaxWidth = budget * 0.4;
                matchToastText.MaxWidth = budget * 0.6;
            }

            private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateMatchToastTimer()
            {
                var timer = nativeWindow!.DispatcherQueue.CreateTimer();
                timer.IsRepeating = false;
                timer.Tick += (_, _) =>
                {
                    if (matchToastBorder == null) return;
                    FadeMatchToast(0, 150);
                    // Give the fade time to finish, then advance the queue.
                    var advance = nativeWindow!.DispatcherQueue.CreateTimer();
                    advance.IsRepeating = false;
                    advance.Interval = TimeSpan.FromMilliseconds(170);
                    advance.Tick += (_, _) =>
                    {
                        if (matchToastBorder != null)
                        {
                            matchToastBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        }

                        ShowNextMatchToast();
                    };
                    advance.Start();
                };
                return timer;
            }

            private void FadeMatchToast(double to, int ms)
            {
                if (matchToastBorder == null) return;
                var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    To = to,
                    Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(ms)),
                    EnableDependentAnimation = true,
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, matchToastBorder);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                storyboard.Children.Add(animation);
                storyboard.Begin();
            }

            private static async Task<byte[]?> ResolveLeagueBytesAsync(
                IBadgeImageLoader? loader, IHomeAssetLocator? locator, global::VardyParty.Kernel.Game game)
            {
                try
                {
                    if (loader == null || locator == null) return null;
                    var path = await locator.ResolveLeagueLogoPathAsync(game).ConfigureAwait(false);
                    return path == null ? null : await loader.LoadLocalBytesAsync(path).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            }

            private void SetToastImageAsync(
                Microsoft.UI.Xaml.Controls.Image? target, Task<byte[]?>? bytesTask, int epoch, bool keepSlot)
            {
                if (target == null) return;
                if (keepSlot)
                {
                    // Reserve the slot (transparent) instead of collapsing it.
                    target.Source = null;
                    target.Opacity = 0;
                    target.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                else
                {
                    target.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }

                if (bytesTask == null) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var bytes = await bytesTask.ConfigureAwait(false);
                        if (bytes == null) return;

                        MainThread.BeginInvokeOnMainThread(async () =>
                        {
                            try
                            {
                                if (epoch != matchToastEpoch) return; // superseded banner
                                var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                                using var stream = new InMemoryRandomAccessStreamOverBytes(bytes);
                                await image.SetSourceAsync(stream.Stream);
                                target.Source = image;
                                target.Opacity = 1;
                                target.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                            }
                            catch (Exception ex) { _host.LogIgnored("SetToastImage", ex); }
                        });
                    }
                    catch (Exception ex)
                    {
                        _host.LogIgnored("SetToastImageAsync", ex);
                    }
                });
            }

            /// <summary>Tiny adapter: encoded bytes → WinRT random-access stream.</summary>
            private sealed class InMemoryRandomAccessStreamOverBytes : IDisposable
            {
                private readonly MemoryStream _memory;

                public InMemoryRandomAccessStreamOverBytes(byte[] bytes) => _memory = new MemoryStream(bytes);

                public global::Windows.Storage.Streams.IRandomAccessStream Stream =>
                    _memory.AsRandomAccessStream();

                public void Dispose() => _memory.Dispose();
            }
        }
    }
}
#endif
