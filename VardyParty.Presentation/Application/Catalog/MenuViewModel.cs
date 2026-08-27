using VardyParty.Catalog;
using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Shared flyout/menu presentation for the app menu across heads.
/// </summary>
public sealed class MenuViewModel
{
    private readonly ILeagueFilterService _leagueFilter;
    private readonly UiSoundService _uiSounds;
    private readonly MatchEventNotificationPolicy _notifications;
    private List<string> _knownLeagues = new();

    public MenuViewModel(
        ILeagueFilterService leagueFilter,
        UiSoundService uiSounds,
        MatchEventNotificationPolicy notifications)
    {
        _leagueFilter = leagueFilter ?? throw new ArgumentNullException(nameof(leagueFilter));
        _uiSounds = uiSounds ?? throw new ArgumentNullException(nameof(uiSounds));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    }

    public IReadOnlyList<string> KnownLeagues => _knownLeagues;

    public void RefreshKnownLeagues(IDictionary<string, List<Game>>? gamesByLeague)
    {
        _knownLeagues = _leagueFilter.GetKnownLeagues(gamesByLeague).ToList();
    }

    public bool IsLeagueVisible(string league) => _leagueFilter.IsLeagueVisible(league);

    public void ToggleLeague(string league) =>
        _leagueFilter.SetLeagueVisible(league, !_leagueFilter.IsLeagueVisible(league));

    public void ShowAllLeagues() => _leagueFilter.SetLeaguesVisible(_knownLeagues, true);

    public void ResetToDefaults() => _leagueFilter.ResetToDefaults();

    /// <summary>Settings: the persisted "UI sounds" switch (default ON).</summary>
    public bool UiSoundsEnabled => _uiSounds.Enabled;

    /// <summary>Flips the switch; turning ON plays the Select sound as confirmation.</summary>
    public void ToggleUiSounds() => _uiSounds.SetEnabled(!_uiSounds.Enabled);

    /// <summary>Settings: the persisted "Goal notifications" switch (default ON).</summary>
    public bool GoalNotificationsEnabled => _notifications.NotificationsEnabled;

    /// <summary>Flips the switch. OFF suppresses sting, toast AND card flash.</summary>
    public void ToggleGoalNotifications() =>
        _notifications.SetNotificationsEnabled(!_notifications.NotificationsEnabled);
}
