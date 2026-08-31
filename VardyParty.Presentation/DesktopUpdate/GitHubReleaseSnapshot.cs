namespace VardyParty.Presentation;

public sealed record GitHubReleaseSnapshot(
    string TagName,
    bool Draft,
    bool Prerelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<GitHubReleaseAssetSnapshot> Assets);

public sealed record GitHubReleaseAssetSnapshot(string Name, string BrowserDownloadUrl);
