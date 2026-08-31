namespace VardyParty.Presentation;

/// <summary>
/// User-facing semver plus the internal build counter from a GitHub tag
/// (<c>2.1.0-b160</c>) or from the running assembly.
/// </summary>
public readonly record struct AppReleaseVersion(int Major, int Minor, int Patch, int Build)
    : IComparable<AppReleaseVersion>
{
    public int CompareTo(AppReleaseVersion other)
    {
        var display = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        return display != 0 ? display : Build.CompareTo(other.Build);
    }

    public bool IsNewerThan(AppReleaseVersion running) => CompareTo(running) > 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}-b{Build}";

    public static bool TryParseTag(string? tag, out AppReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var dashB = text.LastIndexOf("-b", StringComparison.OrdinalIgnoreCase);
        var display = dashB < 0 ? text : text[..dashB];
        var buildText = dashB < 0 ? "0" : text[(dashB + 2)..];
        if (!int.TryParse(buildText, out var build) || build < 0)
        {
            return false;
        }

        return TryParseDisplay(display, build, out version);
    }

    public static bool TryParseDisplay(string? informational, int build, out AppReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(informational) || build < 0)
        {
            return false;
        }

        var display = informational.Trim();
        var plus = display.IndexOf('+');
        if (plus >= 0)
        {
            display = display[..plus];
        }

        var parts = display.Split('.');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch))
        {
            return false;
        }

        version = new AppReleaseVersion(major, minor, patch, build);
        return true;
    }
}
