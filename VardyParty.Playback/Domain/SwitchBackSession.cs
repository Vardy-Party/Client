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

/// <summary>
/// What <see cref="SwitchBackLoop"/> should do next. <see cref="ArmId"/> and
/// <see cref="Candidate"/> are a snapshot of the arm the decision was made for,
/// so a probe result or a "switched back" cancel can never apply to a newer arm.
/// </summary>
public readonly record struct SwitchBackDecision(
    SwitchBackAction Action,
    TimeSpan? Wait,
    string? Reason,
    long ArmId = 0,
    SwitchBackCandidate? Candidate = null);

/// <summary>
/// One option 2 playlist probe. <see cref="Progress"/> fingerprints the live
/// window (<see cref="SwitchBackPlaylistProgress"/>); null when it could not be
/// read, which never blocks a recovery on its own.
/// </summary>
public readonly record struct SwitchBackProbeResult(bool Playable, int StatusCode, string? Progress = null)
{
    public static SwitchBackProbeResult Unreachable { get; } = new(false, 0);
}

/// <summary>
/// Option 2 policy: after option 1 gives up and we switch to keep video going,
/// re-probe the abandoned playlist and switch back when it recovers.
/// Fake-clock tests drive <see cref="Next"/> / <see cref="NoteProbe(long, SwitchBackProbeResult, DateTimeOffset)"/>.
/// </summary>
/// <remarks>
/// Thread-safe. The <see cref="SwitchBackLoop"/> task calls <see cref="Next"/> and
/// <see cref="NoteProbe(long, SwitchBackProbeResult, DateTimeOffset)"/>; the
/// player/UI thread calls <see cref="Arm"/> and <see cref="Cancel(string)"/>; the
/// picture-size callbacks call <see cref="NoteBackupHeight"/>. Every <see cref="Arm"/>
/// gets a new monotonic arm id, and results carrying an older id are ignored.
/// </remarks>
public sealed class SwitchBackSession
{
    public const int ConsecutiveGoodRequired = 2;

    private readonly object _gate = new();
    private SwitchBackCandidate? _candidate;
    private long _armId;
    private bool _cancelled;
    private string? _cancelReason;
    private DateTimeOffset? _abandonedAt;
    private DateTimeOffset? _backupAttachedAt;
    private DateTimeOffset? _nextProbeAt;
    private int _probeIndex;
    private int _consecutiveGood;
    private int? _backupHeight;
    private string? _lastGoodProgress;

    public bool IsArmed { get { lock (_gate) return _candidate is not null && !_cancelled; } }
    public bool IsCancelled { get { lock (_gate) return _cancelled; } }
    public SwitchBackCandidate? Candidate { get { lock (_gate) return _candidate; } }
    public long ArmId { get { lock (_gate) return _armId; } }
    public int ConsecutiveGood { get { lock (_gate) return _consecutiveGood; } }

    /// <summary>
    /// When the backup first showed a settled picture. The 30s dwell
    /// (<see cref="PlaybackPolicy.SwitchBackMinBackupDwell"/>) runs from here, not
    /// from when the old stream was abandoned. Null until the backup has a picture.
    /// </summary>
    public DateTimeOffset? BackupAttachedAt { get { lock (_gate) return _backupAttachedAt; } }

    public int? BackupHeight { get { lock (_gate) return _backupHeight; } }

    /// <summary>Starts watching <paramref name="candidate"/>. Returns the new arm id.</summary>
    public long Arm(SwitchBackCandidate candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.PlaylistUrl))
            throw new ArgumentException("A playlist URL is required.", nameof(candidate));

        lock (_gate)
        {
            _armId++;
            _candidate = candidate;
            _cancelled = false;
            _cancelReason = null;
            _abandonedAt = now;
            _backupAttachedAt = null;
            _backupHeight = null;
            _consecutiveGood = 0;
            _probeIndex = 0;
            _lastGoodProgress = null;
            _nextProbeAt = now + PlaybackPolicy.SwitchBackProbeSchedule[0];
            return _armId;
        }
    }

    /// <summary>Cancels whatever is armed now (user navigation, a new pick).</summary>
    public void Cancel(string reason)
    {
        lock (_gate)
            CancelLocked(reason);
    }

    /// <summary>
    /// Cancels only when <paramref name="armId"/> is still the current arm. Returns
    /// false (and changes nothing) for a stale arm.
    /// </summary>
    public bool Cancel(long armId, string reason)
    {
        lock (_gate)
        {
            if (armId != _armId)
                return false;
            CancelLocked(reason);
            return true;
        }
    }

    /// <summary>The backup published a settled picture height.</summary>
    public void NoteBackupHeight(int? height, DateTimeOffset now)
    {
        if (height is not > 0)
            return;

        lock (_gate)
        {
            if (_candidate is null || _cancelled)
                return;
            _backupHeight = height;
            _backupAttachedAt ??= now;
        }
    }

    public SwitchBackDecision Next(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_candidate is null)
                return Decide(SwitchBackAction.Stop, null, "no abandoned stream");
            if (_cancelled)
                return Decide(SwitchBackAction.Stop, null, _cancelReason ?? "cancelled");

            if (_consecutiveGood >= ConsecutiveGoodRequired)
            {
                if (ShouldSwitchBack(now, out var why))
                    return Decide(SwitchBackAction.SwitchBack, null, why);

                return Decide(SwitchBackAction.Wait, RemainingDwell(now), "min dwell on backup");
            }

            var due = _nextProbeAt ?? now;
            if (now < due)
                return Decide(SwitchBackAction.Wait, due - now, "until next playlist probe");

            return Decide(SwitchBackAction.Probe, null, "playlist probe");
        }
    }

    /// <summary>Records a probe of the current arm (convenience for tests and callers without an arm id).</summary>
    public SwitchBackDecision NoteProbe(bool playable, int statusCode, DateTimeOffset now, string? progress = null)
        => NoteProbe(ArmId, new SwitchBackProbeResult(playable, statusCode, progress), now);

    /// <summary>
    /// Records a probe made for <paramref name="armId"/>. A result for an older arm
    /// is ignored (<see cref="SwitchBackAction.Wait"/> of zero, so the loop asks
    /// <see cref="Next"/> again for the current arm). A playable playlist counts as
    /// good only when its live window moved on since the previous good probe, so a
    /// CDN serving a frozen cached playlist is not "recovered".
    /// </summary>
    public SwitchBackDecision NoteProbe(long armId, SwitchBackProbeResult result, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (armId != _armId)
                return Decide(SwitchBackAction.Wait, TimeSpan.Zero, "stale probe ignored");
            if (_candidate is null)
                return Decide(SwitchBackAction.Stop, null, "no abandoned stream");
            if (_cancelled)
                return Decide(SwitchBackAction.Stop, null, _cancelReason ?? "cancelled");

            var statusCode = result.StatusCode;
            if (PlaylistReloadEvaluation.IsAuthRejection(statusCode)
                || statusCode is 404 or 410)
            {
                CancelLocked($"playlist probe HTTP {statusCode}");
                return Decide(SwitchBackAction.Stop, null, _cancelReason);
            }

            var frozen = result.Playable
                         && result.Progress is not null
                         && _lastGoodProgress is not null
                         && string.Equals(result.Progress, _lastGoodProgress, StringComparison.Ordinal);
            var good = result.Playable && !frozen;

            if (good)
            {
                _lastGoodProgress = result.Progress ?? _lastGoodProgress;
                _consecutiveGood++;
                if (_consecutiveGood >= ConsecutiveGoodRequired && ShouldSwitchBack(now, out var why))
                    return Decide(SwitchBackAction.SwitchBack, null, why);
            }
            else
            {
                _consecutiveGood = 0;
            }

            _probeIndex++;
            var delay = ProbeDelay(_probeIndex);
            _nextProbeAt = now + delay;
            if (_consecutiveGood >= ConsecutiveGoodRequired)
                return Decide(SwitchBackAction.Wait, RemainingDwell(now), "min dwell on backup");

            var reason = good
                ? "need another good playlist"
                : frozen ? "playlist not moving" : "playlist not healthy";
            return Decide(SwitchBackAction.Wait, delay, reason);
        }
    }

    public bool IsBetterHeightThanBackup()
    {
        lock (_gate)
            return IsBetterHeightThanBackupLocked();
    }

    private SwitchBackDecision Decide(SwitchBackAction action, TimeSpan? wait, string? reason)
        => new(action, wait, reason, _armId, _candidate);

    private void CancelLocked(string reason)
    {
        if (_candidate is null)
            return;
        _cancelled = true;
        _cancelReason = reason;
    }

    private bool IsBetterHeightThanBackupLocked()
        => _candidate?.LastHeight is { } left
           && left > 0
           && left > (_backupHeight ?? 0);

    private bool ShouldSwitchBack(DateTimeOffset now, out string reason)
    {
        if (IsBetterHeightThanBackupLocked())
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

    /// <summary>
    /// Dwell left on the backup. Runs from the backup's first settled picture; if
    /// the backup never shows one, from when the old stream was abandoned (a
    /// backup with no picture is no reason to stay away from a recovered stream).
    /// </summary>
    private TimeSpan RemainingDwell(DateTimeOffset now)
    {
        var started = _backupAttachedAt ?? _abandonedAt ?? now;
        var remain = PlaybackPolicy.SwitchBackMinBackupDwell - (now - started);
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
