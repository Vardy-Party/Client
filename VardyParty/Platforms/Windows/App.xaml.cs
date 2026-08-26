// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VardyParty.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // XAML-thread exceptions never reach AppDomain.UnhandledException or
            // TaskScheduler.UnobservedTaskException (both wired in the shared
            // App.xaml.cs): WinAppSDK 1.8 converts them into anonymous stowed
            // 0xc000027b crashes unless this hook observes them first.
            // Application.Current is assigned by the base Application constructor
            // before this body runs, so wiring here is the earliest safe point and
            // covers even InitializeComponent-time failures.
            UnhandledException += OnXamlUnhandledException;

            if (IsAuth0RedirectActivation())
            {
                Platforms.Windows.WindowsEventLogger.Info("WinUI.App", "Auth0 redirection activation handled; skipping main UI startup");
                return;
            }

            this.InitializeComponent();
        }

        /// <summary>
        /// Last-chance handler for exceptions thrown on the XAML UI thread.
        /// Marking them handled keeps the app alive where possible; either way the
        /// exception is logged instead of dying as an anonymous stowed exception.
        /// </summary>
        private static void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                // e.Exception can be marshaling-lossy for exceptions that crossed
                // the WinRT boundary; e.Message preserves the original text.
                Platforms.Windows.WindowsEventLogger.Fatal(
                    "WinUI.Xaml",
                    $"Unhandled XAML-thread exception: {e.Message}",
                    e.Exception);
                e.Handled = true;
            }
            catch
            {
                // The crash hook itself must never throw.
            }
        }

        /// <summary>
        /// Only a genuine protocol activation can be an Auth0 redirect; a normal launch
        /// must never be short-circuited (that would leave the app running with no window).
        /// Any failure in the Auth0 activator is logged and treated as "not a redirect".
        /// </summary>
        private static bool IsAuth0RedirectActivation()
        {
            try
            {
                try
                {
                    var activationKind = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind;
                    if (activationKind != Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol)
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Platforms.Windows.WindowsEventLogger.Warning("WinUI.App", "Could not determine activation kind; probing Auth0 redirection anyway", ex);
                }

                return Auth0.OidcClient.Platforms.Windows.Activator.Default.CheckRedirectionActivation();
            }
            catch (Exception ex)
            {
                Platforms.Windows.WindowsEventLogger.Error("WinUI.App", "Auth0 CheckRedirectionActivation failed; treating as a normal launch", ex);
                return false;
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
