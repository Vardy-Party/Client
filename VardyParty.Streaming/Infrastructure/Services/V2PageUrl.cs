using VardyParty.LocalService.V2;
using Stream = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Catalog-driven V2 (= mp) helpers. Strategy comes from
/// <see cref="Stream.ResolutionStrategy"/> / <see cref="Stream.Source"/> — not page hosts.
/// Shared rewrite logic lives in NuGet <c>VardyParty.LocalService.V2</c>.
/// </summary>
public static class V2PageUrl
{
    public static bool IsV2Stream(Stream stream) =>
        StreamStrategy.IsV2(stream.ResolutionStrategy, stream.Source);

    public static bool IsV2(string? resolutionStrategy, string? source = null) =>
        StreamStrategy.IsV2(resolutionStrategy, source);

    public static bool UseV2Endpoint(
        string? resolutionStrategy,
        string? source,
        IEnumerable<string>? capabilities) =>
        IsV2(resolutionStrategy, source)
        && capabilities?.Any(c => string.Equals(c, "v2.chrome", StringComparison.OrdinalIgnoreCase)) == true;
}
