using System.Runtime.InteropServices;

namespace VardyParty.Presentation;

/// <summary>
/// Which GitHub release (if any) the desktop heads should offer. Releases
/// younger than two days stay hidden so a bad package can be yanked first.
/// </summary>
public static class DesktopUpdatePolicy
{
    public static readonly TimeSpan Maturity = TimeSpan.FromDays(2);

    public static bool IsMature(DateTimeOffset publishedAt, DateTimeOffset utcNow) =>
        utcNow - publishedAt >= Maturity;

    public static bool AssetMatches(string? assetName, DesktopUpdatePlatform platform)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return false;
        }

        return platform switch
        {
            DesktopUpdatePlatform.Windows =>
                assetName.Contains("VardyParty-windows-", StringComparison.OrdinalIgnoreCase)
                && assetName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase),
            DesktopUpdatePlatform.LinuxX64 =>
                assetName.Contains("linux-x64", StringComparison.OrdinalIgnoreCase)
                && assetName.EndsWith(".snap", StringComparison.OrdinalIgnoreCase),
            DesktopUpdatePlatform.LinuxArm64 =>
                assetName.Contains("linux-arm64", StringComparison.OrdinalIgnoreCase)
                && assetName.EndsWith(".snap", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static DesktopUpdatePlatform? DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return DesktopUpdatePlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => DesktopUpdatePlatform.LinuxArm64,
                Architecture.X64 => DesktopUpdatePlatform.LinuxX64,
                _ => null,
            };
        }

        return null;
    }

    public static DesktopUpdateOffer? SelectOffer(
        IReadOnlyList<GitHubReleaseSnapshot> releases,
        AppReleaseVersion running,
        DesktopUpdatePlatform platform,
        DateTimeOffset utcNow)
    {
        DesktopUpdateOffer? best = null;
        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease || release.PublishedAt is null)
            {
                continue;
            }

            if (!IsMature(release.PublishedAt.Value, utcNow))
            {
                continue;
            }

            if (!AppReleaseVersion.TryParseTag(release.TagName, out var version)
                || !version.IsNewerThan(running))
            {
                continue;
            }

            GitHubReleaseAssetSnapshot? asset = null;
            GitHubReleaseAssetSnapshot? signature = null;
            foreach (var candidate in release.Assets)
            {
                if (AssetMatches(candidate.Name, platform)
                    && !string.IsNullOrWhiteSpace(candidate.BrowserDownloadUrl))
                {
                    asset = candidate;
                    var sigName = candidate.Name + MinisignHashed.SignatureSuffix;
                    foreach (var maybeSig in release.Assets)
                    {
                        if (string.Equals(maybeSig.Name, sigName, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(maybeSig.BrowserDownloadUrl))
                        {
                            signature = maybeSig;
                            break;
                        }
                    }

                    break;
                }
            }

            if (asset is null)
            {
                continue;
            }

            if (RequiresMinisign(platform) && signature is null)
            {
                continue;
            }

            var offer = new DesktopUpdateOffer(
                release.TagName,
                asset.Name,
                asset.BrowserDownloadUrl,
                version,
                release.PublishedAt.Value,
                signature?.BrowserDownloadUrl);
            if (best is null || offer.Version.CompareTo(best.Version) > 0)
            {
                best = offer;
            }
        }

        return best;
    }

    public static bool RequiresMinisign(DesktopUpdatePlatform platform) =>
        platform is DesktopUpdatePlatform.LinuxX64 or DesktopUpdatePlatform.LinuxArm64;
}
