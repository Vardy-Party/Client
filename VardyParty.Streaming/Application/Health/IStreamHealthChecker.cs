namespace VardyParty.Health;

public interface IStreamHealthChecker
{
    Task<StreamHealth> CheckStreamHealthAsync(string m3u8Url, string refererUrl,
        CancellationToken cancellationToken = default);
}