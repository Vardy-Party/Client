using System.Reflection;
using System.Runtime.InteropServices;

namespace VardyParty.Ports;

/// <summary>
/// Non-MAUI environment labels for health reports. Heads can wrap this and
/// overlay a discovered LocalService version.
/// </summary>
public sealed class RuntimeStreamHealthClientEnvironment(
    Func<string?>? localServiceVersion = null,
    Func<string?>? localServiceOs = null) : IStreamHealthClientEnvironment
{
    public string? ClientOs =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsAndroid() ? "android"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? "ios"
        : RuntimeInformation.OSDescription;

    public string? ClientVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
    }

    public string? LocalServiceOs => localServiceOs?.Invoke();

    public string? LocalServiceVersion => localServiceVersion?.Invoke();
}
