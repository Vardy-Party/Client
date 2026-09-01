namespace VardyParty.Presentation;

public sealed record DesktopUpdateOffer(
    string Tag,
    string AssetName,
    string DownloadUrl,
    AppReleaseVersion Version,
    DateTimeOffset PublishedAt,
    string? SignatureUrl = null);
