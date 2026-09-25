using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging;
using VardyParty.Ports;
using VardyParty.Presentation;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class DnsOverHttpsPreferenceTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task SetEnabled_OutOfOrderNotifiers_ObserveOnlyTheLastValue()
    {
        // Arrange
        var first = new DelayedNotifier();
        var second = new DelayedNotifier();
        var preferences = _fixture.GetMock<IDnsPreferencesStore>();
        var sut = new DnsOverHttpsPreference(preferences.Object, new ILocalLanDnsNotifier[] { first, second });

        // Act
        sut.SetEnabled(true);
        sut.SetEnabled(false);
        await first.SawDisabled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await second.SawDisabled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);

        // Assert
        Assert.Equal(false, first.Last);
        Assert.Equal(false, second.Last);
        Assert.DoesNotContain(true, first.AppliedAfterDisabled);
        Assert.DoesNotContain(true, second.AppliedAfterDisabled);
    }

    [Fact]
    public void SetEnabled_WhenNotifierThrows_LogsWarning()
    {
        // Arrange
        var preferences = _fixture.GetMock<IDnsPreferencesStore>();
        var logger = new WarningLogger();
        var sut = new DnsOverHttpsPreference(
            preferences.Object,
            new ILocalLanDnsNotifier[] { new ThrowingNotifier() },
            logger);

        // Act
        sut.SetEnabled(false);

        // Assert
        Assert.Contains(logger.Warnings, message => message.Contains("DoH", StringComparison.Ordinal));
    }

    private sealed class DelayedNotifier : ILocalLanDnsNotifier
    {
        private readonly List<bool> _appliedAfterDisabled = new();
        private bool _sawDisabled;

        public TaskCompletionSource SawDisabled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool? Last { get; private set; }

        public IReadOnlyList<bool> AppliedAfterDisabled
        {
            get
            {
                lock (_appliedAfterDisabled)
                    return _appliedAfterDisabled.ToArray();
            }
        }

        public async Task NotifyAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            await Task.Delay(enabled ? 400 : 30, cancellationToken).ConfigureAwait(false);
            lock (_appliedAfterDisabled)
            {
                Last = enabled;
                if (_sawDisabled)
                    _appliedAfterDisabled.Add(enabled);
                if (!enabled)
                {
                    _sawDisabled = true;
                    SawDisabled.TrySetResult();
                }
            }
        }
    }

    private sealed class ThrowingNotifier : ILocalLanDnsNotifier
    {
        public Task NotifyAsync(bool enabled, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("discovery failed");
    }

    private sealed class WarningLogger : ILogger<DnsOverHttpsPreference>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
