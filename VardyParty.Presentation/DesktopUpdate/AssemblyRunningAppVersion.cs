using System.Reflection;

namespace VardyParty.Presentation;

public sealed class AssemblyRunningAppVersion : IRunningAppVersion
{
    public AssemblyRunningAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var assemblyVersion = assembly.GetName().Version;
        var build = assemblyVersion is null ? 0 : assemblyVersion.Major;
        Current = AppReleaseVersion.TryParseDisplay(informational, build, out var parsed)
            ? parsed
            : new AppReleaseVersion(0, 0, 0, build);
    }

    public AppReleaseVersion Current { get; }
}
