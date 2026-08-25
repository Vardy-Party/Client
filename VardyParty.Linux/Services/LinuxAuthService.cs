using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Auth;
using VardyParty.Configuration;

namespace VardyParty.Linux.Services;

public class LinuxAuthService : Auth0TokenSession
{
    private readonly string _tokenFilePath;
    private readonly string _tokenKeyPath;

    public LinuxAuthService(
        ILogger<LinuxAuthService> logger,
        IOptions<Auth0Settings> auth0Settings,
        IAuth0OAuthClient oauth)
        : base(logger, auth0Settings, oauth)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VardyParty");
        _tokenFilePath = Path.Combine(configDir, "auth0-tokens.bin");
        _tokenKeyPath = Path.Combine(configDir, ".token-key");
    }

    protected override bool ThrowOnMissingDeviceConfig => false;

    public override async Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTokenReadyAsync(cancellationToken, forceRefresh: false);
        if (HasValidToken)
            return new AuthLoginResult(true, AccessToken, null);

        if (!Auth0Pkce.TryGetLoopbackRedirectUri(Settings.RedirectUri, out var redirectUri))
        {
            Logger.LogWarning("[Auth0/Linux] RedirectUri is not loopback; falling back to device login");
            var deviceLogin = await StartDeviceLoginAsync(cancellationToken);
            if (deviceLogin?.DeviceCode == null)
            {
                return new AuthLoginResult(false, null,
                    "Unable to start interactive login (invalid redirect URI) or device login.");
            }

            Logger.LogInformation("[Auth0/Linux] Complete sign-in at {Uri} with code {Code}",
                deviceLogin.DeviceCode.VerificationUri, deviceLogin.DeviceCode.UserCode);
            return await PollDeviceLoginAsync(deviceLogin.DeviceCode, cancellationToken);
        }

        var pkce = Auth0Pkce.Start(Settings, redirectUri);

        try
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(Auth0Pkce.BuildListenerPrefix(redirectUri));
            listener.Start();

            if (!OpenBrowser(pkce.AuthorizeUrl))
            {
                listener.Stop();
                return new AuthLoginResult(false, null,
                    "Could not open browser for Auth0 login. Please install xdg-open/gio or use device login.");
            }

            var callback = await WaitForCallbackAsync(listener, TimeSpan.FromMinutes(3), cancellationToken);
            if (callback == null)
                return new AuthLoginResult(false, null, "Timed out waiting for Auth0 login callback.");

            var callbackFailure = Auth0Pkce.DescribeCallbackFailure(
                pkce.State, callback.State, callback.Code, callback.Error, callback.ErrorDescription);
            if (callbackFailure != null)
                return new AuthLoginResult(false, null, callbackFailure);

            return await CompleteAuthorizationCodeAsync(
                callback.Code!, redirectUri.ToString(), pkce.CodeVerifier, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Auth0/Linux] Interactive login failed");
            return new AuthLoginResult(false, null, ex.Message);
        }
    }

    public override async Task LogoutAsync()
    {
        await base.LogoutAsync();
        Logger.LogInformation("[Auth0/Linux] Logged out and cleared local tokens");
    }

    protected override async Task LoadPersistedTokensAsync()
    {
        try
        {
            var json = await TryReadTokenJsonAsync();
            if (json == null)
                return;

            var payload = JsonSerializer.Deserialize<StoredTokenPayload>(json);
            if (payload == null)
                return;

            AccessToken = payload.AccessToken;
            RefreshToken = payload.RefreshToken;
            ExpiresAt = payload.ExpiresAtUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds)
                : DateTimeOffset.MinValue;
            LastRefreshedAt = payload.LastRefreshedAtUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(payload.LastRefreshedAtUnixSeconds)
                : DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0/Linux] Failed to load token cache from disk");
        }
    }

    protected override async Task PersistTokensAsync()
    {
        try
        {
            var payload = new StoredTokenPayload
            {
                AccessToken = AccessToken,
                RefreshToken = RefreshToken,
                ExpiresAtUnixSeconds = ExpiresAt.ToUnixTimeSeconds(),
                LastRefreshedAtUnixSeconds = LastRefreshedAt.ToUnixTimeSeconds()
            };

            await SaveEncryptedJsonAsync(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0/Linux] Failed to persist token cache to disk");
        }
    }

    protected override Task ClearPersistedTokensAsync()
    {
        try
        {
            if (File.Exists(_tokenFilePath))
                File.Delete(_tokenFilePath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0/Linux] Failed to delete token cache file");
        }

        return Task.CompletedTask;
    }

    private async Task<string?> TryReadTokenJsonAsync()
    {
        if (File.Exists(_tokenFilePath))
        {
            var bytes = await File.ReadAllBytesAsync(_tokenFilePath);
            return UnprotectTokens(bytes);
        }

        var legacyPath = Path.Combine(
            Path.GetDirectoryName(_tokenFilePath) ?? string.Empty,
            "auth0-tokens.json");
        if (!File.Exists(legacyPath))
            return null;

        var json = await File.ReadAllTextAsync(legacyPath);
        try
        {
            await SaveEncryptedJsonAsync(json);
            File.Delete(legacyPath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Auth0/Linux] Failed to migrate plaintext token cache");
        }

        return json;
    }

    private async Task SaveEncryptedJsonAsync(string json)
    {
        var directory = Path.GetDirectoryName(_tokenFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllBytesAsync(_tokenFilePath, ProtectTokens(json));
        RestrictUserOnly(_tokenFilePath);
        RestrictUserOnly(_tokenKeyPath);
    }

    private byte[] ProtectTokens(string plaintext)
    {
        var key = GetOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag);

        var output = new byte[3 + nonce.Length + tag.Length + cipher.Length];
        output[0] = (byte)'V';
        output[1] = (byte)'P';
        output[2] = (byte)'1';
        Buffer.BlockCopy(nonce, 0, output, 3, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, 15, tag.Length);
        Buffer.BlockCopy(cipher, 0, output, 31, cipher.Length);
        return output;
    }

    private string UnprotectTokens(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == (byte)'V' && bytes[1] == (byte)'P' && bytes[2] == (byte)'1')
        {
            var key = GetOrCreateKey();
            var nonce = bytes.AsSpan(3, 12);
            var tag = bytes.AsSpan(15, 16);
            var cipher = bytes.AsSpan(31);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private byte[] GetOrCreateKey()
    {
        var directory = Path.GetDirectoryName(_tokenKeyPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(_tokenKeyPath))
        {
            var existing = File.ReadAllBytes(_tokenKeyPath);
            if (existing.Length == 32)
                return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_tokenKeyPath, key);
        RestrictUserOnly(_tokenKeyPath);
        return key;
    }

    private static void RestrictUserOnly(string path)
    {
        if (!File.Exists(path) || OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private bool OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", url)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("gio", $"open {url}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[Auth0/Linux] Failed to open browser for interactive login");
                return false;
            }
        }
    }

    private async Task<AuthCallbackResult?> WaitForCallbackAsync(
        HttpListener listener,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (!cts.IsCancellationRequested)
        {
            var getContextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed != getContextTask)
                break;

            var context = await getContextTask;
            var query = context.Request.QueryString;
            var html = string.IsNullOrWhiteSpace(query["error"])
                ? "<html><body><h2>Login complete</h2><p>You can close this tab and return to VardyParty.</p></body></html>"
                : "<html><body><h2>Login failed</h2><p>You can close this tab and return to VardyParty.</p></body></html>";

            var responseBuffer = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = string.IsNullOrWhiteSpace(query["error"]) ? 200 : 400;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = responseBuffer.Length;
            await context.Response.OutputStream.WriteAsync(responseBuffer);
            context.Response.OutputStream.Close();

            return new AuthCallbackResult
            {
                Code = query["code"],
                State = query["state"],
                Error = query["error"],
                ErrorDescription = query["error_description"]
            };
        }

        return null;
    }

    private sealed class StoredTokenPayload
    {
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public long ExpiresAtUnixSeconds { get; init; }
        public long LastRefreshedAtUnixSeconds { get; init; }
    }

    private sealed class AuthCallbackResult
    {
        public string? Code { get; init; }
        public string? State { get; init; }
        public string? Error { get; init; }
        public string? ErrorDescription { get; init; }
    }
}
