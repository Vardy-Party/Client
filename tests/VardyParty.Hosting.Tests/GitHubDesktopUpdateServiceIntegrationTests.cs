using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Hosting;
using VardyParty.Presentation;
using Xunit;
using Xunit.Abstractions;

namespace VardyParty.Hosting.Tests;

/// <summary>
/// Hits the same GitHub Releases URL the desktop updater uses
/// (<c>https://api.github.com/repos/Vardy-Party/Client/releases</c>).
/// </summary>
public class GitHubDesktopUpdateServiceIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public GitHubDesktopUpdateServiceIntegrationTests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public async Task CheckAsync_LiveGitHubReleasesUrl_AppliesTwoDayPolicy()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddVardyPartyHttpClients();
        using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>();
        var client = http.CreateClient(GitHubDesktopUpdateService.HttpClientName);
        var url = new Uri(client.BaseAddress!, GitHubDesktopUpdateService.ReleasesPath);
        _output.WriteLine($"GET {url}");

        using var probe = await client.GetAsync(GitHubDesktopUpdateService.ReleasesPath);
        if (probe.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
        {
            _output.WriteLine($"GitHub rate-limited: {(int)probe.StatusCode}");
            return;
        }

        Assert.True(probe.IsSuccessStatusCode, $"GitHub releases GET failed: {(int)probe.StatusCode} {probe.ReasonPhrase}");

        await using var stream = await probe.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() > 0, "Expected at least one GitHub release.");

        var now = DateTimeOffset.UtcNow;
        var twoDays = DesktopUpdatePolicy.Maturity;
        var running = new AppReleaseVersion(0, 0, 0, 0);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var tag = item.GetProperty("tag_name").GetString();
            var publishedRaw = item.TryGetProperty("published_at", out var publishedEl)
                ? publishedEl.GetString()
                : null;
            DateTimeOffset.TryParse(publishedRaw, out var published);
            var age = published == default ? (TimeSpan?)null : now - published;
            var mature = published != default && DesktopUpdatePolicy.IsMature(published, now);
            _output.WriteLine(
                $"{tag} published {publishedRaw} age={age} mature(>={twoDays})={mature}");
        }

        var pending = new Mock<IDesktopPendingUpdateStore>();
        pending.Setup(s => s.Read()).Returns((AppReleaseVersion?)null);
        var sut = new GitHubDesktopUpdateService(
            http,
            new FixedVersion(running),
            Mock.Of<IDesktopPackageApplier>(),
            pending.Object,
            Mock.Of<IDesktopAppQuitter>(),
            NullLogger<GitHubDesktopUpdateService>.Instance);

        // Act
        await sut.CheckAsync();

        // Assert
        var platform = DesktopUpdatePolicy.DetectPlatform();
        Assert.NotNull(platform);
        if (sut.Offer is { } offer)
        {
            _output.WriteLine($"Offer {offer.Tag} {offer.AssetName} published {offer.PublishedAt:u}");
            Assert.True(offer.Version.IsNewerThan(running));
            Assert.True(now - offer.PublishedAt >= twoDays);
            Assert.True(DesktopUpdatePolicy.AssetMatches(offer.AssetName, platform.Value));
            Assert.False(string.IsNullOrWhiteSpace(offer.DownloadUrl));
        }
        else
        {
            _output.WriteLine("No offer: every newer release is under two days old or has no installer for this OS.");
        }
    }

    private sealed class FixedVersion(AppReleaseVersion current) : IRunningAppVersion
    {
        public AppReleaseVersion Current { get; } = current;
    }
}
