using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net.Http;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// The client <c>/play</c> timeout must outlast LocalService's own final wait
/// (page.goto 30s + settled-playlist 5s = 35s) plus a 30s margin, so the
/// client never gives up on a /play the service is still finishing. A stream
/// that hit our timeout gets double, capped at 120s.
/// </summary>
public class LocalServicePlayBudgetTests
{
    [Fact]
    public void Budget_IsServiceFinalWaitPlusMargin()
    {
        Assert.Equal(TimeSpan.FromSeconds(35), LocalServicePlayBudget.LocalServiceFinalWait);
        Assert.Equal(TimeSpan.FromSeconds(65), LocalServicePlayBudget.MinimumPlayCallTimeout);
        Assert.True(LocalServicePlayBudget.MinimumPlayCallTimeout
                    >= LocalServicePlayBudget.LocalServiceFinalWait + TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(null, 65)]
    [InlineData(5, 65)]
    [InlineData(20, 65)]
    [InlineData(90, 90)]
    public void BaseTimeout_NeverBelowTheServiceBudget(int? configuredSeconds, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            LocalServicePlayBudget.BaseTimeout(configuredSeconds is { } s ? TimeSpan.FromSeconds(s) : null));

    [Theory]
    [InlineData(65, 120)]
    [InlineData(50, 100)]
    [InlineData(90, 120)]
    public void ExtendedTimeout_DoublesWithCap(int baseSeconds, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            LocalServicePlayBudget.ExtendedTimeout(TimeSpan.FromSeconds(baseSeconds)));

    [Fact]
    public void LocalLanPlayService_UsesBudget_AndDoublesForASlowStream()
    {
        var service = new LocalLanPlayService(
            new HttpClient(),
            Options.Create(new GamesApiSettings { CallTimeoutSeconds = 5, MaxRetries = 0, RefreshSchedule = 60, M3U8CallTimeoutSeconds = 20 }),
            Array.Empty<IPlaybackTransportPlugin>(),
            NullLogger<LocalLanPlayService>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(65), service.PlayTimeoutFor("lshls-1"));

        service.MarkSlowForTests("lshls-1");

        Assert.Equal(TimeSpan.FromSeconds(120), service.PlayTimeoutFor("lshls-1"));
        Assert.Equal(TimeSpan.FromSeconds(65), service.PlayTimeoutFor("lshls-2"));
    }
}
