using VardyParty.Kernel;

namespace VardyParty.Streaming;

public interface IStreamHealthChecker
{
    Task<StreamHealth> CheckStreamHealthAsync(string m3u8Url, string refererUrl,
        CancellationToken cancellationToken = default);

    Task<StreamHealth> CheckStreamHealthAsync(
        string m3u8Url,
        string refererUrl,
        StreamHealthProbe? probe,
        CancellationToken cancellationToken = default);
}