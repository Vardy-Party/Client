using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;

namespace VardyParty.Services;

/// <summary>
/// JavaScript interop service for Google Cast Web Sender API
/// </summary>
public class CastJsInterop(IJSRuntime jsRuntime, ILogger<CastJsInterop> logger) : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime = jsRuntime;
    private readonly ILogger<CastJsInterop> _logger = logger;
    private IJSObjectReference? _castModule;
    private DotNetObjectReference<CastJsInterop>? _dotNetReference;

    public event EventHandler<bool>? CastStateChanged;
    public event EventHandler<string>? DeviceNameChanged;
    public event EventHandler<string>? MediaStatusChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _dotNetReference = DotNetObjectReference.Create(this);

            // Load the Cast JavaScript module
            _castModule = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./js/cast-interop.js");

            // Initialize Cast SDK
            await _castModule.InvokeVoidAsync("initializeCast", _dotNetReference);

            _logger.LogInformation("Cast JS Interop initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Cast JS Interop");
        }
    }

    public async Task<bool> IsCastAvailableAsync()
    {
        if (_castModule == null) return false;

        try
        {
            return await _castModule.InvokeAsync<bool>("isCastAvailable");
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsCastConnectedAsync()
    {
        if (_castModule == null) return false;

        try
        {
            return await _castModule.InvokeAsync<bool>("isCastConnected");
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetDeviceNameAsync()
    {
        if (_castModule == null) return null;

        try
        {
            return await _castModule.InvokeAsync<string>("getDeviceName");
        }
        catch
        {
            return null;
        }
    }

    public async Task LoadMediaAsync(string mediaUrl, string title, string subtitle, string? imageUrl = null)
    {
        if (_castModule == null) return;

        try
        {
            await _castModule.InvokeVoidAsync("loadMedia", mediaUrl, title, subtitle, imageUrl);
            _logger.LogInformation("Cast: Loading media - {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading media");
        }
    }

    public async Task PlayAsync()
    {
        if (_castModule == null) return;

        try
        {
            await _castModule.InvokeVoidAsync("play");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing");
        }
    }

    public async Task PauseAsync()
    {
        if (_castModule == null) return;

        try
        {
            await _castModule.InvokeVoidAsync("pause");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing");
        }
    }

    public async Task StopAsync()
    {
        if (_castModule == null) return;

        try
        {
            await _castModule.InvokeVoidAsync("stop");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping");
        }
    }

    public async Task SeekAsync(long positionSeconds)
    {
        if (_castModule == null) return;

        try
        {
            await _castModule.InvokeVoidAsync("seek", positionSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeking");
        }
    }

    public async Task RequestCastSessionAsync()
    {
        if (_castModule == null) return;

        try
        {
            await _castModule.InvokeVoidAsync("requestCastSession");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting cast session");
        }
    }

    // Called from JavaScript when cast state changes
    [JSInvokable]
    public void OnCastStateChanged(bool isConnected)
    {
        _logger.LogInformation("Cast state changed: {State}", isConnected ? "Connected" : "Disconnected");
        CastStateChanged?.Invoke(this, isConnected);
    }

    // Called from JavaScript when device name is available
    [JSInvokable]
    public void OnDeviceNameChanged(string deviceName)
    {
        _logger.LogInformation("Cast device: {Device}", deviceName);
        DeviceNameChanged?.Invoke(this, deviceName);
    }

    // Called from JavaScript when media status changes
    [JSInvokable]
    public void OnMediaStatusChanged(string status)
    {
        _logger.LogInformation("Media status: {Status}", status);
        MediaStatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_castModule != null)
        {
            await _castModule.DisposeAsync();
        }

        _dotNetReference?.Dispose();
    }
}
