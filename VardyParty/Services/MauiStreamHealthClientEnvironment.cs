using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.MauiServices;

/// <summary>MAUI AppInfo version plus discovered LocalService version for health reports.</summary>
public sealed class MauiStreamHealthClientEnvironment(ILocalLanPlayService? localLanPlay = null)
    : IStreamHealthClientEnvironment
{
    private readonly RuntimeStreamHealthClientEnvironment _runtime = new(
        localServiceVersion: () => localLanPlay?.DiscoveredServiceVersion);

    public string? ClientOs =>
#if ANDROID
        "android";
#elif WINDOWS
        "windows";
#elif MACCATALYST || IOS
        "ios";
#else
        _runtime.ClientOs;
#endif

    public string? ClientVersion
    {
        get
        {
            try
            {
                var version = AppInfo.Current.VersionString;
                return string.IsNullOrWhiteSpace(version) ? _runtime.ClientVersion : version.Trim();
            }
            catch
            {
                return _runtime.ClientVersion;
            }
        }
    }

    public string? LocalServiceOs => null;

    public string? LocalServiceVersion => _runtime.LocalServiceVersion;
}
