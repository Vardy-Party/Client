namespace VardyParty.Services;

public interface ICastService
{
    bool IsConnected { get; }
    string? ConnectedDeviceName { get; }

    Task<bool> InitializeAsync();
    void Cast(string mediaUrl, string title, string subtitle, string? imageUrl = null);
    void Play();
    void Pause();
    void Stop();
    void Seek(long positionMs);

    event EventHandler<bool>? ConnectionChanged;
    event EventHandler<string>? DeviceNameChanged;
}
