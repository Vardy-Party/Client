using System.Text.Json;
using Microsoft.Extensions.Logging;
using VardyParty.Presentation;

namespace VardyParty.Hosting;

public sealed class GitHubDesktopUpdateService : IDesktopUpdateService, IDisposable
{
    public const string HttpClientName = "GitHubReleases";
    public const string AssetHttpClientName = "GitHubReleaseAssets";
    public const string ReleasesPath = "repos/Vardy-Party/Client/releases";

    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(4);

    private readonly IHttpClientFactory _http;
    private readonly IRunningAppVersion _running;
    private readonly IDesktopPackageApplier _applier;
    private readonly IDesktopPendingUpdateStore _pending;
    private readonly IDesktopAppQuitter _quitter;
    private readonly ILogger<GitHubDesktopUpdateService> _logger;
    private readonly DesktopUpdatePlatform? _platform;
    private Timer? _timer;
    private int _started;
    private int _checking;
    private int _failedApplyLogged;

    public GitHubDesktopUpdateService(
        IHttpClientFactory http,
        IRunningAppVersion running,
        IDesktopPackageApplier applier,
        IDesktopPendingUpdateStore pending,
        IDesktopAppQuitter quitter,
        ILogger<GitHubDesktopUpdateService> logger)
    {
        _http = http;
        _running = running;
        _applier = applier;
        _pending = pending;
        _quitter = quitter;
        _logger = logger;
        _platform = DesktopUpdatePolicy.DetectPlatform();
    }

    public DesktopUpdateOffer? Offer { get; private set; }

    public event Action<DesktopUpdateOffer?>? OfferChanged;

    public event Action<string>? ApplyFailed;

    public void Start()
    {
        if (_platform is null || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        ReconcilePending();
        _timer = new Timer(OnTick, null, FirstCheckDelay, PollInterval);
    }

    public async Task InstallAsync(DesktopUpdateOffer offer, CancellationToken cancellationToken)
    {
        if (!DesktopUpdateDownload.IsAllowedDownloadUrl(offer.DownloadUrl))
        {
            throw new InvalidOperationException("Update download URL is not a GitHub HTTPS asset.");
        }

        var destDir = Path.Combine(Path.GetTempPath(), "VardyParty");
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, DesktopUpdateDownload.FileNameFromAsset(offer.AssetName));
        _logger.LogInformation("Downloading desktop update {Tag} to {Path}", offer.Tag, dest);

        var client = _http.CreateClient(AssetHttpClientName);
        await using (var remote = await client.GetStreamAsync(offer.DownloadUrl, cancellationToken)
            .ConfigureAwait(false))
        await using (var local = File.Create(dest))
        {
            await CopyCappedAsync(remote, local, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(offer.SignatureUrl))
        {
            if (!DesktopUpdateDownload.IsAllowedDownloadUrl(offer.SignatureUrl))
            {
                throw new InvalidOperationException("Update signature URL is not a GitHub HTTPS asset.");
            }

            var sigPath = dest + MinisignHashed.SignatureSuffix;
            await using (var remoteSig = await client.GetStreamAsync(offer.SignatureUrl, cancellationToken)
                .ConfigureAwait(false))
            await using (var localSig = File.Create(sigPath))
            {
                await CopyCappedAsync(remoteSig, localSig, cancellationToken).ConfigureAwait(false);
            }

            MinisignHashed.VerifyFile(dest, File.ReadAllText(sigPath), MinisignPublicKeys.Linux);
        }
        else if (_platform is DesktopUpdatePlatform.LinuxX64 or DesktopUpdatePlatform.LinuxArm64)
        {
            throw new InvalidOperationException("Linux snaps must include a minisign signature.");
        }

        _pending.Write(offer.Version);
        try
        {
            var applied = await _applier.ApplyAsync(dest, offer, cancellationToken).ConfigureAwait(false);
            if (applied.CallerShouldQuit)
            {
                _quitter.RequestQuit();
            }
        }
        catch
        {
            _pending.Clear();
            throw;
        }
    }

    public void Dispose() => _timer?.Dispose();

    private async void OnTick(object? _)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return;
        }

        try
        {
            await CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub release check failed");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    internal async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_platform is null)
        {
            return;
        }

        ReconcilePending();

        var client = _http.CreateClient(HttpClientName);
        await using var stream = await client.GetStreamAsync(ReleasesPath, cancellationToken)
            .ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var snapshots = new List<GitHubReleaseSnapshot>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            snapshots.Add(ReadRelease(item));
        }
        var offer = DesktopUpdatePolicy.SelectOffer(
            snapshots,
            _running.Current,
            _platform.Value,
            DateTimeOffset.UtcNow);
        if (Equals(Offer, offer))
        {
            return;
        }

        Offer = offer;
        OfferChanged?.Invoke(offer);
        if (offer is not null)
        {
            _logger.LogInformation("Desktop update available: {Tag} ({Asset})", offer.Tag, offer.AssetName);
        }
    }

    private void ReconcilePending()
    {
        var pending = _pending.Read();
        var state = DesktopPendingUpdatePolicy.Evaluate(_running.Current, pending);
        if (state == DesktopPendingUpdateState.Applied)
        {
            _pending.Clear();
            Interlocked.Exchange(ref _failedApplyLogged, 0);
            _logger.LogInformation("Desktop update applied; now running {Version}", _running.Current);
        }
        else if (state == DesktopPendingUpdateState.FailedToApply
            && Interlocked.Exchange(ref _failedApplyLogged, 1) == 0)
        {
            var message = $"Update to {pending} did not land; still running {_running.Current}.";
            _logger.LogWarning("{Message}", message);
            ApplyFailed?.Invoke(message);
        }
    }

    private static async Task CopyCappedAsync(Stream remote, Stream local, CancellationToken cancellationToken)
    {
        var remaining = DesktopUpdateDownload.MaxAssetBytes;
        var buffer = new byte[81920];
        while (true)
        {
            var read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            remaining -= read;
            if (remaining < 0)
            {
                throw new InvalidOperationException("Update asset exceeds the download size cap.");
            }

            await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static GitHubReleaseSnapshot ReadRelease(JsonElement item)
    {
        var tag = item.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        var draft = item.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean();
        var pre = item.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean();
        DateTimeOffset? published = null;
        if (item.TryGetProperty("published_at", out var publishedEl)
            && publishedEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(publishedEl.GetString(), out var parsed))
        {
            published = parsed;
        }

        var assets = new List<GitHubReleaseAssetSnapshot>();
        if (item.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var urlEl)
                    ? urlEl.GetString() ?? ""
                    : "";
                assets.Add(new GitHubReleaseAssetSnapshot(name, url));
            }
        }

        return new GitHubReleaseSnapshot(tag, draft, pre, published, assets);
    }
}
