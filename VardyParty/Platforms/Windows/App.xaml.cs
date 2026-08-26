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
            // Auth0 records that this ran; Sign in later throws
            // "redirection check on app activation was not detected" if it did not.
            // Always call it — including on a normal Launch — then only skip the
            // main UI when THIS instance is the protocol-redirect helper.
            var redirected = IsAuth0RedirectActivation();
            Platforms.Windows.WindowsEventLogger.Info("WinUI.App",
                redirected
                    ? "Auth0 redirection activation handled; skipping main UI startup"
                    : "Auth0 CheckRedirectionActivation completed (normal launch)");
            if (redirected)
            {
                return;
            }

            this.UnhandledException += OnXamlUnhandledException;
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            this.InitializeComponent();
        }

        /// <summary>
        /// WinAppSDK 1.8 converts unhandled XAML-thread exceptions into
        /// 0xc000027b stowed crashes (CoreMessagingXP / combase 0x800710DD)
        /// with no managed stack. Log first, then mark handled so a managed
        /// fault cannot take the process down before we see it.
        /// </summary>
        private static void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                Platforms.Windows.WindowsEventLogger.Fatal("WinUI", "XAML UnhandledException", e.Exception);
            }
            catch
            {
            }

            e.Handled = true;
        }

        private static int _firstChanceLogged;

        /// <summary>
        /// 0xc000027b is WinUI stowing a managed exception. Capture it here
        /// before CoreMessaging swallows the stack.
        /// </summary>
        private static void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            var ex = e.Exception;
            if (ex is OperationCanceledException or TaskCanceledException)
            {
                return;
            }

            var stack = ex.StackTrace ?? string.Empty;
            if (stack.IndexOf("VardyParty", StringComparison.OrdinalIgnoreCase) < 0
                && stack.IndexOf("Microsoft.Maui", StringComparison.OrdinalIgnoreCase) < 0
                && stack.IndexOf("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            if (System.Threading.Interlocked.Increment(ref _firstChanceLogged) > 25)
            {
                return;
            }

            try
            {
                Platforms.Windows.WindowsEventLogger.Error(
                    "FirstChance",
                    $"{ex.GetType().FullName}: {ex.Message}",
                    ex);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Must run on every activation. Returning true means this process is the
        /// Auth0 protocol helper and must not show a window. Returning false (or
        /// catching) is a normal launch — CheckRedirectionActivation has still
        /// been recorded so a later Sign in can succeed.
        /// </summary>
        private static bool IsAuth0RedirectActivation()
        {
            try
            {
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
