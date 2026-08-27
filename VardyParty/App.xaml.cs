namespace VardyParty
{
    public partial class App : Application
    {
        private readonly IServiceProvider _services;

        public App(IServiceProvider services)
        {
            _services = services;
            Console.WriteLine("[App] Constructor start");
            try
            {
                InitializeComponent();
                Console.WriteLine("[App] InitializeComponent completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] CRITICAL ERROR in InitializeComponent: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[App] StackTrace: {ex.StackTrace}");
#if WINDOWS
                Platforms.Windows.WindowsEventLogger.Fatal("App", "InitializeComponent failed", ex);
#endif
                throw;
            }

            Console.WriteLine("[App] Constructor end");

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.WriteLine($"[AppDomain Exception] {e.ExceptionObject}");
                if (e.ExceptionObject is Exception ex)
                {
                    Console.WriteLine($"[AppDomain] StackTrace: {ex.StackTrace}");
#if WINDOWS
                    Platforms.Windows.WindowsEventLogger.Fatal("AppDomain", "Unhandled exception", ex);
#endif
                }
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Console.WriteLine($"[Task Exception] {e.Exception}");
                Console.WriteLine($"[Task] StackTrace: {e.Exception.StackTrace}");
#if WINDOWS
                Platforms.Windows.WindowsEventLogger.Error("TaskScheduler", "Unobserved task exception", e.Exception);
#endif
                e.SetObserved();
            };
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            try
            {
                Console.WriteLine("[App] CreateWindow - start");
                // Every platform boots the shared MAUI XAML homepage (the
                // Blazor UI was deleted on this branch).
                Page mainPage = _services.GetRequiredService<HomeHostPage>();
                Console.WriteLine("[App] CreateWindow - HomeHostPage created");
                var window = new Window(mainPage)
                {
                    Title = string.Empty,
                };
                WireForegroundState(window);
                Console.WriteLine("[App] CreateWindow - window created successfully");
                return window;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] CRITICAL ERROR in CreateWindow: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[App] StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Match-event notifications follow the window lifecycle: a
        /// backgrounded app delivers NOTHING (no audio, no toast catch-up on
        /// resume). Foreground means the window is visible — Stopped (hidden/
        /// minimized; Android fires it when another activity, including the
        /// native video player, covers us) clears it, Activated/Resumed set
        /// it. Deactivated (focus lost while still visible) deliberately does
        /// not count as background, so the "playing → toast only" policy row
        /// can still deliver while a native player window holds focus.
        /// </summary>
        private void WireForegroundState(Window window)
        {
            var notifications = _services.GetRequiredService<VardyParty.Presentation.MatchEventNotificationPolicy>();
            window.Activated += (_, _) => notifications.IsAppForegrounded = true;
            window.Resumed += (_, _) => notifications.IsAppForegrounded = true;
            window.Stopped += (_, _) => notifications.IsAppForegrounded = false;
            window.Destroying += (_, _) => notifications.IsAppForegrounded = false;
        }
    }
}
