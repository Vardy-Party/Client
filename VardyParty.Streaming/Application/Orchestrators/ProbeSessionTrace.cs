using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Ports;

namespace VardyParty.Streaming;

/// <summary>
/// The session-level <c>[probe-timing]</c> lines (docs/PROBE_SCHEDULING_RULES.md, "Probe
/// timing log"): when the picture opened, when a backup was ready, each switch, and a
/// summary when the session ends, with the tier of the playing row and of the backup.
/// One per <see cref="StreamResolutionOrchestrator"/> session.
/// </summary>
internal sealed class ProbeSessionTrace : IDisposable
{
    private readonly object _gate = new();
    private readonly ProbeTimingLog _timing;
    private readonly ILogger _logger;
    private readonly IStreamSwitchingService _switching;
    private readonly Func<StreamTiers?> _tiers;
    private readonly IDisposable? _subscription;
    private readonly IDisposable? _confirmedSubscription;
    private readonly bool _countsConfirmedOnly;
    private long? _pictureMs;
    private long? _backupMs;
    private int _switches;
    private EnrichedStream? _playing;
    private bool _ended;

    public ProbeSessionTrace(ProbeTimingLog timing, ILogger logger, IStreamSwitchingService switching, Func<StreamTiers?> tiers)
    {
        _timing = timing;
        _logger = logger;
        _switching = switching;
        _tiers = tiers;
        // A switch counts when the player shows video on the new row (PlayingConfirmed).
        // An index change is only logged as a request: a switch that fails and stays on
        // the playing stream is not a switch (TV log 13 Nov 2026, switches=3 with none).
        try
        {
            if (switching.PlayingConfirmed is { } confirmed)
            {
                _confirmedSubscription = confirmed.Subscribe(new Observer(this, confirmedOnly: true));
                _countsConfirmedOnly = true;
            }
        }
        catch (NotSupportedException)
        {
            _confirmedSubscription = null;
        }

        try
        {
            _subscription = switching.CurrentStreamChanged?.Subscribe(new Observer(this, confirmedOnly: false));
        }
        catch (NotSupportedException)
        {
            _subscription = null;
        }
    }

    public long? PictureMs => _pictureMs;

    public long? BackupMs => _backupMs;

    public int Switches => _switches;

    /// <summary>The first picture of the session opened on <paramref name="row"/>.</summary>
    public void Picture(EnrichedStream row)
    {
        lock (_gate)
        {
            if (_pictureMs is not null || _ended)
                return;
            _pictureMs = _timing.Ms;
            _playing = row;
        }

        _timing.Log(_logger, "picture",
            ("row", row.Stream?.Channel),
            ("chip", row.Stream?.PlayerStream),
            ("tier", Tier(row)),
            ("cdn", ProbeTimingLog.Host(row.ResolvedM3U8Url)));
        PoolChanged();
    }

    /// <summary>A row joined (or left) the pool: logs the first time a backup is ready while a picture plays.</summary>
    public void PoolChanged()
    {
        EnrichedStream? playing;
        lock (_gate)
        {
            if (_pictureMs is null || _backupMs is not null || _ended)
                return;
            playing = _playing;
        }

        if (_switching.GetHealthyStreams() is not { Count: >= 2 })
            return;
        var backup = Backup(playing);
        lock (_gate)
        {
            if (_backupMs is not null)
                return;
            _backupMs = _timing.Ms;
        }

        _timing.Log(_logger, "backup-ready",
            ("playing_tier", Tier(playing)),
            ("backup_tier", Tier(backup)),
            ("backup_row", backup?.Stream?.Channel),
            ("backup_chip", backup?.Stream?.PlayerStream),
            ("picture_ms", _pictureMs),
            ("since_picture_ms", _backupMs - _pictureMs));
    }

    private void OnRequested(EnrichedStream? row)
    {
        if (!_countsConfirmedOnly)
        {
            OnCurrent(row);
            return;
        }

        if (row?.Stream is null)
            return;
        EnrichedStream? from;
        lock (_gate)
        {
            if (_pictureMs is null || _ended || ReferenceEquals(_playing?.Stream, row.Stream))
                return;
            from = _playing;
        }

        _timing.Log(_logger, "switch-requested",
            ("from_row", from?.Stream?.Channel),
            ("to_tier", Tier(row)),
            ("to_row", row.Stream.Channel),
            ("to_chip", row.Stream.PlayerStream));
    }

    private void OnCurrent(EnrichedStream? row)
    {
        if (row?.Stream is null)
            return;
        EnrichedStream? from;
        lock (_gate)
        {
            if (_pictureMs is null || _ended || ReferenceEquals(_playing?.Stream, row.Stream))
                return;
            from = _playing;
            _playing = row;
            _switches++;
        }

        _timing.Log(_logger, "switch",
            ("from_tier", Tier(from)),
            ("to_tier", Tier(row)),
            ("to_row", row.Stream.Channel),
            ("to_chip", row.Stream.PlayerStream),
            ("switches", _switches));
    }

    public void Dispose()
    {
        EnrichedStream? playing;
        lock (_gate)
        {
            if (_ended)
                return;
            _ended = true;
            playing = _playing;
        }

        _subscription?.Dispose();
        _confirmedSubscription?.Dispose();
        var backup = _pictureMs is null ? null : Backup(playing);
        _timing.Log(_logger, "session-end",
            ("picture_ms", _pictureMs),
            ("backup_ms", _backupMs),
            ("switches", _switches),
            ("playing_tier", playing is null ? null : Tier(playing)),
            ("backup_tier", backup is null ? null : Tier(backup)),
            ("healthy", _switching.GetHealthyStreams()?.Count ?? 0));
    }

    private EnrichedStream? Backup(EnrichedStream? playing)
    {
        var upcoming = _switching.GetUpcoming(1);
        var next = upcoming?.FirstOrDefault();
        if (next is not null && !ReferenceEquals(next.Stream, playing?.Stream))
            return next;
        return _switching.GetHealthyStreams()?.FirstOrDefault(s => !ReferenceEquals(s.Stream, playing?.Stream));
    }

    private string Tier(EnrichedStream? row) => ProbeTimingLog.Tier(_tiers(), row?.Stream);

    private sealed class Observer(ProbeSessionTrace trace, bool confirmedOnly) : IObserver<EnrichedStream?>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(EnrichedStream? value)
        {
            if (confirmedOnly)
                trace.OnCurrent(value);
            else
                trace.OnRequested(value);
        }
    }
}
