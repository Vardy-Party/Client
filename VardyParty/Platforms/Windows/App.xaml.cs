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
            if (IsAuth0RedirectActivation())
            {
                Platforms.Windows.WindowsEventLogger.Info("WinUI.App", "Auth0 redirection activation handled; skipping main UI startup");
                return;
            }

            this.InitializeComponent();
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
