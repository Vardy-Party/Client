namespace VardyParty.HomeUi;

/// <summary>
/// Loads team badges and league logos as MAUI <see cref="ImageSource"/>s.
/// Implementations must be safe to call from any thread and should cache.
/// </summary>
public interface IBadgeImageLoader
{
    /// <summary>Fetch a remote badge (SVG or bitmap). Null on any failure.</summary>
    Task<ImageSource?> LoadRemoteAsync(string? url);

    /// <summary>Load a local asset by absolute path (SVG or bitmap). Null on any failure.</summary>
    Task<ImageSource?> LoadLocalAsync(string? path);

    /// <summary>
    /// The same cached artwork as raw encoded bytes (PNG for rasterised SVGs,
    /// original bytes otherwise) — for surfaces with no MAUI tree, e.g. the
    /// Android video activity's native match-event banner. Null on any failure.
    /// </summary>
    Task<byte[]?> LoadRemoteBytesAsync(string? url);

    /// <inheritdoc cref="LoadRemoteBytesAsync"/>
    Task<byte[]?> LoadLocalBytesAsync(string? path);
}
