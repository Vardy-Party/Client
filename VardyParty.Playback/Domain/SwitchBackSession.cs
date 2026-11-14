namespace VardyParty.Playback;

/// <summary>
/// The stream we left mid-play so the user still has video, and may automatically
/// return to once its playlist is healthy again (option 2).
/// </summary>
public sealed record SwitchBackCandidate(
    string PlaylistUrl,
    string? RowName,
    string? IdentityKey,
    int? LastHeight,
    IReadOnlyDictionary<string, string>? RequestHeaders,
    string? Referer);

public enum SwitchBackAction
{
    Wait,
    Probe,
    SwitchBack,
    Stop
}

public readonly record struct SwitchBackDecision(
    SwitchBackAction Action,
    TimeSpan? Wait,
    string? Reason);

/// <summary>
/// Option 2 policy: after option 1 gives up and we switch to keep video going,
/// re-probe the abandoned playlist and switch back when it recovers.
/// Fake-clock tests drive <see cref="Next"/> / <see cref="NoteProbe"/>.
/// </summary>
public sealed class SwitchBackSession
{
    public const int ConsecutiveGoodRequired = 2;

    public bool IsArmed => _candidate is not null && !_cancelled;
    public bool IsCancelled => _cancelled;
    public SwitchBackCandidate? Candidate => _candidate;
    public int ConsecutiveGood => _consecutiveGood;
    public DateTimeOffset? BackupAttachedAt => _backupAttachedAt;
    public int? BackupHeight => _backupHeight;

    private SwitchBackCandidate? _candidate;
    private bool _cancelled;
    private string? _cancelReason;
    private DateTimeOffset? _abandonedAt;
    private DateTimeOffset? _backupAttachedAt;
    private DateTimeOffset? _nextProbeAt;
    private int _probeIndex;
    private int _consecutiveGood;
    private int? _backupHeight;

    public void Arm(SwitchBackCandidate candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.PlaylistUrl))
            throw new ArgumentException("A playlist URL is required.", nameof(candidate));

        _candidate = candidate;
        _cancelled = false;
        _cancelReason = null;
        _abandonedAt = now;
        _backupAttachedAt = now;
        _backupHeight = null;
        _consecutiveGood = 0;
        _probeIndex = 0;
        _nextProbeAt = now + PlaybackPolicy.SwitchBackProbeSchedule[0];
    }

    public void Cancel(string reason)
    {
        if (_candidate is null)
            return;
        _cancelled = true;
        _cancelReason = reason;
    }

    public void NoteBackupHeight(int? height, DateTimeOffset now)
    {
        if (_candidate is null || _cancelled)
            return;
        if (height is > 0)
            _backupHeight = height;
        _backupAttachedAt ??= now;
    }

    public SwitchBackDecision Next(DateTimeOffset now)
    {
        if (_candidate is null)
            return new SwitchBackDecision(SwitchBackAction.Stop, null, "no abandoned stream");
        if (_cancelled)
            return new SwitchBackDecision(SwitchBackAction.Stop, null, _cancelReason ?? "cancelled");

        if (_consecutiveGood >= ConsecutiveGoodRequired)
        {
            if (ShouldSwitchBack(now, out var why))
                return new SwitchBackDecision(SwitchBackAction.SwitchBack, null, why);

            var wait = RemainingDwell(now);
            return new SwitchBackDecision(SwitchBackAction.Wait, wait, "min dwell on backup");
        }

        var due = _nextProbeAt ?? now;
        if (now < due)
            return new SwitchBackDecision(SwitchBackAction.Wait, due - now, "until next playlist probe");

        return new SwitchBackDecision(SwitchBackAction.Probe, null, "playlist probe");
    }

    public SwitchBackDecision NoteProbe(bool playable, int statusCode, DateTimeOffset now)
    {
        if (_candidate is null)
            return new SwitchBackDecision(SwitchBackAction.Stop, null, "no abandoned stream");
        if (_cancelled)
            return new SwitchBackDecision(SwitchBackAction.Stop, null, _cancelReason ?? "cancelled");

        if (PlaylistReloadEvaluation.IsAuthRejection(statusCode)
            || statusCode is 404 or 410)
        {
            Cancel($"playlist probe HTTP {statusCode}");
            return new SwitchBackDecision(SwitchBackAction.Stop, null, _cancelReason);
        }

        if (playable)
        {
            _consecutiveGood++;
            if (_consecutiveGood >= ConsecutiveGoodRequired && ShouldSwitchBack(now, out var why))
                return new SwitchBackDecision(SwitchBackAction.SwitchBack, null, why);
        }
        else
        {
            _consecutiveGood = 0;
        }

        _probeIndex++;
        var delay = ProbeDelay(_probeIndex);
        _nextProbeAt = now + delay;
        if (_consecutiveGood >= ConsecutiveGoodRequired)
            return new SwitchBackDecision(SwitchBackAction.Wait, RemainingDwell(now), "min dwell on backup");

        return new SwitchBackDecision(SwitchBackAction.Wait, delay, playable ? "need another good playlist" : "playlist not healthy");
    }

    public bool IsBetterHeightThanBackup()
        => _candidate?.LastHeight is { } left
           && left > 0
           && left > (_backupHeight ?? 0);

    private bool ShouldSwitchBack(DateTimeOffset now, out string reason)
    {
        if (IsBetterHeightThanBackup())
        {
            reason = $"better height {_candidate!.LastHeight}p>{_backupHeight ?? 0}p";
            return true;
        }

        if (RemainingDwell(now) <= TimeSpan.Zero)
        {
            reason = "user-chosen stream recovered";
            return true;
        }

        reason = "min dwell on backup";
        return false;
    }

    private TimeSpan RemainingDwell(DateTimeOffset now)
    {
        var started = _backupAttachedAt ?? _abandonedAt ?? now;
        var elapsed = now - started;
        var remain = PlaybackPolicy.SwitchBackMinBackupDwell - elapsed;
        return remain > TimeSpan.Zero ? remain : TimeSpan.Zero;
    }

    private static TimeSpan ProbeDelay(int probeIndex)
    {
        var schedule = PlaybackPolicy.SwitchBackProbeSchedule;
        if (probeIndex < 0)
            return schedule[0];
        if (probeIndex >= schedule.Length)
            return schedule[^1];
        return schedule[probeIndex];
    }
}
