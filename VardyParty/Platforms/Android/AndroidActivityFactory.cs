#if ANDROID
using System;
using Android.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VardyParty.Platforms.Android
{
    // Simple activity factory that injects services into activities after creation.
    // Register this in MauiProgram for Android only.
    public static class AndroidActivityFactory
    {
        public static void Inject(Activity activity)
        {
            try
            {
                var provider = VardyParty.AppServiceProvider.ServiceProvider;
                if (provider == null) return;

                var switching = provider.GetService<IStreamSwitchingService>();
                var lf = provider.GetService<ILoggerFactory>();
                ILogger<NativeVideoActivity>? logger = lf?.CreateLogger<NativeVideoActivity>();

                if (activity is NativeVideoActivity nva)
                {
                    var health = provider.GetService<IStreamHealthReporter>();
                    var enriched = provider.GetService<IEnrichedGameService>();
                    var api = provider.GetService<IApiService>();
                    var orchestrator = provider.GetService<IStreamResolutionOrchestrator>();
                    nva.InjectServices(switching, logger, health, enriched, api, orchestrator);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AndroidActivityFactory] Inject failed: {ex.Message}");
            }
        }
    }
}
#endif
