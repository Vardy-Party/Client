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
    }
}
