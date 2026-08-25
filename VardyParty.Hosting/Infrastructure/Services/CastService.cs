#pragma warning disable CS0067
using Microsoft.Extensions.Logging;

namespace VardyParty.Hosting;

/// <summary>
/// Cast service for Chromecast support
/// Note: Full Chromecast integration requires Google Cast SDK which is not available for .NET 10
/// This is a stub implementation for future integration
/// </summary>
public class CastService(ILogger<CastService> logger) : ICastService
{
    public bool IsConnected => false;
    public string? ConnectedDeviceName => null;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? DeviceNameChanged;

    public Task<bool> InitializeAsync()
    {
        logger.LogInformation("Cast Service: Chromecast not available in .NET 10 MAUI");
        logger.LogInformation("For Chromecast support, users can: \n  1. Use external Chromecast app to cast entire screen\n  2. Cast from Chrome browser on mobile device");
        return Task.FromResult(false);
    }

    public void Cast(string mediaUrl, string title, string subtitle, string? imageUrl = null)
    {
        logger.LogInformation("Cast requested: {Title} - {MediaUrl}", title, mediaUrl);
        logger.LogInformation("Chromecast SDK not available. Use screen mirroring or browser casting.");
    }

    public void Play()
    {
        logger.LogInformation("Play command - Chromecast not available");
    }

    public void Pause()
    {
        logger.LogInformation("Pause command - Chromecast not available");
    }

    public void Stop()
    {
        logger.LogInformation("Stop command - Chromecast not available");
    }

    public void Seek(long positionMs)
    {
        logger.LogInformation("Seek command ({PositionMs}ms) - Chromecast not available", positionMs);
    }
}
#pragma warning restore CS0067
