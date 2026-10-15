using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Streaming;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Streaming.Tests;

public sealed class LocalLanServiceAvailabilityMonitorTests
{
    [Fact]
    public async Task FoundStream_StartsFalseUntilDiscoveryConfirmsACapableService()
    {
        // Arrange
        var fixture = AutoMoqFixture.Create();
        var lan = fixture.GetMock<ILocalLanPlayService>();
        lan.Setup(service => service.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        lan.Setup(service => service.SupportsComputeHostAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        lan.SetupGet(service => service.UsesRemoteCompute).Returns(false);
        using var sut = new LocalLanServiceAvailabilityMonitor(
            lan.Object,
            NullLogger<LocalLanServiceAvailabilityMonitor>.Instance);
        var seen = new Collector();
        using var subscription = sut.FoundStream.Subscribe(seen);

        // Act
        sut.Start();
        for (var attempt = 0; attempt < 50 && seen.Values.Count < 2; attempt++)
        {
            await Task.Delay(20);
        }

        // Assert
        Assert.False(seen.Values[0]);
        Assert.Contains(true, seen.Values);
    }

    [Fact]
    public async Task FoundStream_StaysFalseWhenAvailableServiceLacksHostShare()
    {
        // Arrange
        var fixture = AutoMoqFixture.Create();
        var lan = fixture.GetMock<ILocalLanPlayService>();
        lan.Setup(service => service.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        lan.Setup(service => service.SupportsComputeHostAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        lan.SetupGet(service => service.UsesRemoteCompute).Returns(false);
        using var sut = new LocalLanServiceAvailabilityMonitor(
            lan.Object,
            NullLogger<LocalLanServiceAvailabilityMonitor>.Instance);
        var seen = new Collector();
        using var subscription = sut.FoundStream.Subscribe(seen);

        // Act
        sut.Start();
        for (var attempt = 0; attempt < 50 && seen.Values.Count < 2; attempt++)
        {
            await Task.Delay(20);
        }

        // Assert
        Assert.False(seen.Values[0]);
        Assert.DoesNotContain(true, seen.Values);
    }

    [Fact]
    public async Task FoundStream_StaysFalseWhenOnlyUsesRemoteCompute()
    {
        // Arrange
        var fixture = AutoMoqFixture.Create();
        var lan = fixture.GetMock<ILocalLanPlayService>();
        lan.Setup(service => service.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        lan.Setup(service => service.SupportsComputeHostAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        lan.SetupGet(service => service.UsesRemoteCompute).Returns(true);
        using var sut = new LocalLanServiceAvailabilityMonitor(
            lan.Object,
            NullLogger<LocalLanServiceAvailabilityMonitor>.Instance);
        var seen = new Collector();
        using var subscription = sut.FoundStream.Subscribe(seen);

        // Act
        sut.Start();
        for (var attempt = 0; attempt < 50 && seen.Values.Count < 2; attempt++)
        {
            await Task.Delay(20);
        }

        // Assert
        Assert.False(seen.Values[0]);
        Assert.DoesNotContain(true, seen.Values);
    }

    [Fact]
    public async Task FoundStream_PublishesFalseWhenVerificationThrows()
    {
        // Arrange
        var fixture = AutoMoqFixture.Create();
        var lan = fixture.GetMock<ILocalLanPlayService>();
        lan.Setup(service => service.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe failed"));
        lan.SetupGet(service => service.UsesRemoteCompute).Returns(false);
        using var sut = new LocalLanServiceAvailabilityMonitor(
            lan.Object,
            NullLogger<LocalLanServiceAvailabilityMonitor>.Instance);
        var seen = new Collector();
        using var subscription = sut.FoundStream.Subscribe(seen);

        // Act
        sut.Start();
        for (var attempt = 0; attempt < 50 && seen.Values.Count < 2; attempt++)
        {
            await Task.Delay(20);
        }

        // Assert
        Assert.False(seen.Values[0]);
        Assert.All(seen.Values, value => Assert.False(value));
    }

    private sealed class Collector : IObserver<bool>
    {
        public List<bool> Values { get; } = new();

        public void OnNext(bool value) => Values.Add(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
