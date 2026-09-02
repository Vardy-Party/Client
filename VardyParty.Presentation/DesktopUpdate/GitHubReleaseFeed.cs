using System.Text.Json;

namespace VardyParty.Presentation;

/// <summary>
/// Maps GitHub Releases JSON (the array the desktop updater GETs) into
/// <see cref="GitHubReleaseSnapshot"/> values. Only the fields the updater
/// reads are required: tag, draft/prerelease, published_at, asset name + URL.
/// </summary>
public static class GitHubReleaseFeed
{
    public static IReadOnlyList<GitHubReleaseSnapshot> ReadArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var snapshots = new List<GitHubReleaseSnapshot>();
        foreach (var item in root.EnumerateArray())
        {
            snapshots.Add(ReadRelease(item));
        }

        return snapshots;
    }

    public static GitHubReleaseSnapshot ReadRelease(JsonElement item)
    {
        var tag = item.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        var draft = item.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean();
        var pre = item.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean();
        DateTimeOffset? published = null;
        if (item.TryGetProperty("published_at", out var publishedEl)
            && publishedEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(publishedEl.GetString(), out var parsed))
        {
            published = parsed;
        }

        var assets = new List<GitHubReleaseAssetSnapshot>();
        if (item.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var urlEl)
                    ? urlEl.GetString() ?? ""
                    : "";
                assets.Add(new GitHubReleaseAssetSnapshot(name, url));
            }
        }

        return new GitHubReleaseSnapshot(tag, draft, pre, published, assets);
    }
}
