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
}
