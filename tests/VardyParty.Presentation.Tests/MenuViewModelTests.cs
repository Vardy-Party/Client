using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using Moq;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Presentation.Tests;

public class MenuViewModelTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void RefreshKnownLeagues_UsesFilterService()
    {
        // Arrange
        var leagues = new List<string> { "League Alpha", "League Beta" };
        var games = new Dictionary<string, List<Game>>
        {
            ["League Alpha"] = new List<Game>
            {
                _fixture.Build<Game>().With(g => g.Home, "Home United").With(g => g.Away, "Away City").Create()
            }
        };
        _fixture.GetMock<ILeagueFilterService>()
            .Setup(f => f.GetKnownLeagues(games))
            .Returns(leagues);
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.RefreshKnownLeagues(games);

        // Assert
        Assert.Equal(leagues, sut.KnownLeagues);
    }

    [Fact]
    public void ToggleLeague_FlipsVisibility()
    {
        // Arrange
        var filter = _fixture.GetMock<ILeagueFilterService>();
        filter.Setup(f => f.IsLeagueVisible("League Alpha")).Returns(false);
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ToggleLeague("League Alpha");

        // Assert
        filter.Verify(f => f.SetLeagueVisible("League Alpha", true), Times.Once);
    }

    [Fact]
    public void ShowAllLeagues_MakesKnownLeaguesVisible()
    {
        // Arrange
        var filter = _fixture.GetMock<ILeagueFilterService>();
        filter.Setup(f => f.GetKnownLeagues(It.IsAny<IDictionary<string, List<Game>>?>()))
            .Returns(new List<string> { "League Alpha", "League Beta" });
        var sut = _fixture.Create<MenuViewModel>();
        sut.RefreshKnownLeagues(new Dictionary<string, List<Game>>());

        // Act
        sut.ShowAllLeagues();

        // Assert
        filter.Verify(
            f => f.SetLeaguesVisible(It.Is<IEnumerable<string>>(l => l.Contains("League Alpha") && l.Contains("League Beta")), true),
            Times.Once);
    }

    [Fact]
    public void ResetToDefaults_DelegatesToFilter()
    {
        // Arrange
        var filter = _fixture.GetMock<ILeagueFilterService>();
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ResetToDefaults();

        // Assert
        filter.Verify(f => f.ResetToDefaults(), Times.Once);
    }

    /// <summary>
    /// UiSoundService and MatchEventNotificationPolicy are concrete: inject
    /// hand-built instances so AutoFixture's auto-property population can't
    /// randomly flip SuppressAll / the foreground and playback flags.
    /// </summary>
    private (Mock<VardyParty.Ports.IUiSoundPlayer> Player, Mock<VardyParty.Ports.ISoundPreferencesStore> Prefs) InjectUiSounds()
    {
        var player = _fixture.GetMock<VardyParty.Ports.IUiSoundPlayer>();
        var prefs = _fixture.GetMock<VardyParty.Ports.ISoundPreferencesStore>();
        var dnsPrefs = _fixture.GetMock<VardyParty.Ports.IDnsPreferencesStore>();
        dnsPrefs.Setup(p => p.LoadDnsOverHttpsFallbackEnabled()).Returns(true);
        _fixture.Inject(new UiSoundService(player.Object, prefs.Object));
        _fixture.Inject(new MatchEventNotificationPolicy(prefs.Object));
        _fixture.Inject(new DnsOverHttpsPreference(dnsPrefs.Object));
        return (player, prefs);
    }

    [Fact]
    public void UiSoundsEnabled_DefaultsOn_WhenNothingSaved()
    {
        // Arrange
        var (_, prefs) = InjectUiSounds();
        prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = _fixture.Create<MenuViewModel>();

        // Act & Assert
        Assert.True(sut.UiSoundsEnabled);
    }

    [Fact]
    public void ToggleUiSounds_TurnsOff_AndPersists()
    {
        // Arrange
        var (_, prefs) = InjectUiSounds();
        prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ToggleUiSounds();

        // Assert
        prefs.Verify(p => p.SaveUiSoundsEnabled(false), Times.Once);
        Assert.False(sut.UiSoundsEnabled);
    }

    [Fact]
    public void ToggleUiSounds_TurnsOn_PlaysSelectConfirmation()
    {
        // Arrange
        var (player, prefs) = InjectUiSounds();
        prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(false);
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ToggleUiSounds();

        // Assert
        prefs.Verify(p => p.SaveUiSoundsEnabled(true), Times.Once);
        player.Verify(p => p.Play(VardyParty.Ports.UiSound.Select), Times.Once);
        Assert.True(sut.UiSoundsEnabled);
    }

    [Fact]
    public void GoalNotificationsEnabled_DefaultsOn_WhenNothingSaved()
    {
        // Arrange
        var (_, prefs) = InjectUiSounds();
        prefs.Setup(p => p.LoadGoalNotificationsEnabled()).Returns(true);
        var sut = _fixture.Create<MenuViewModel>();

        // Act & Assert
        Assert.True(sut.GoalNotificationsEnabled);
    }

    [Fact]
    public void ToggleGoalNotifications_TurnsOff_AndPersists()
    {
        // Arrange
        var (_, prefs) = InjectUiSounds();
        prefs.Setup(p => p.LoadGoalNotificationsEnabled()).Returns(true);
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ToggleGoalNotifications();

        // Assert
        prefs.Verify(p => p.SaveGoalNotificationsEnabled(false), Times.Once);
        Assert.False(sut.GoalNotificationsEnabled);
    }

    [Fact]
    public void ToggleGoalNotifications_TurnsBackOn_AndPersists()
    {
        // Arrange
        var (_, prefs) = InjectUiSounds();
        prefs.Setup(p => p.LoadGoalNotificationsEnabled()).Returns(false);
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ToggleGoalNotifications();

        // Assert
        prefs.Verify(p => p.SaveGoalNotificationsEnabled(true), Times.Once);
        Assert.True(sut.GoalNotificationsEnabled);
    }

    [Fact]
    public void DnsOverHttpsFallbackEnabled_DefaultsOn_WhenNothingSaved()
    {
        // Arrange
        InjectUiSounds();
        var sut = _fixture.Create<MenuViewModel>();

        // Act & Assert
        Assert.True(sut.DnsOverHttpsFallbackEnabled);
    }

    [Fact]
    public void ToggleDnsOverHttpsFallback_TurnsOff_AndPersists()
    {
        // Arrange
        InjectUiSounds();
        var dnsPrefs = _fixture.GetMock<VardyParty.Ports.IDnsPreferencesStore>();
        dnsPrefs.Setup(p => p.LoadDnsOverHttpsFallbackEnabled()).Returns(true);
        _fixture.Inject(new DnsOverHttpsPreference(dnsPrefs.Object));
        var sut = _fixture.Create<MenuViewModel>();

        // Act
        sut.ToggleDnsOverHttpsFallback();

        // Assert
        dnsPrefs.Verify(p => p.SaveDnsOverHttpsFallbackEnabled(false), Times.Once);
        Assert.False(sut.DnsOverHttpsFallbackEnabled);
    }
}
