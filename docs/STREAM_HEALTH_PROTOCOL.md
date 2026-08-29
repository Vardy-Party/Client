# Stream Health Protocol

**VERSION:** 1.0  
**AUDIENCE:** AI assistants working on client applications that consume the headless-m3u8 API  
**CLIENT PLATFORM:** C# / .NET MAUI (Multi-platform App UI)

---

## Overview

The Stream Health Protocol enables clients to participate in a collective intelligence system that tracks the real-time health and quality of sports streams. By reporting stream health data and receiving recommendations, clients can provide users with faster, more reliable stream selection.

**Update (client identity):** Crowd health does **not** key on ephemeral playback
URLs alone. See `VardyParty.Streaming` `StreamHealthIdentity`:

- Prefer the **catalog/page URL** via `ResolveReportUrl` — if `streamUrl` is an
  ephemeral `.m3u8`/`.mpd`, report the non-ephemeral `refererUrl` instead.
- Composite key is `BuildStreamKey(url, streamName)` where `streamName` comes from
  `PlayerStream` / `Channel` when v2 stream selection applies (`GetStreamName`).
- Recommendations matching uses URL **and** optional `streamName`
  (`MatchesRecommendation`).

Older examples below still show `streamUrl` fields for the HTTP API shape; treat
that as the **resolved report URL**, not necessarily the live M3U8.

### Key Benefits

- **Faster stream discovery**: Get recommended working streams instantly instead of testing all streams
- **Better user experience**: Start playback in ~2-5 seconds instead of 10-20 seconds
- **Reduced bandwidth**: Skip testing known-bad streams
- **Improved reliability**: Learn from other viewers' experiences in real-time

---

## Architecture

Each match has a dedicated backend service instance that tracks stream health across all viewers. This service:
- Collects health reports from active viewers
- Calculates health scores based on success rate, quality, recency, and active viewers
- Returns ranked stream recommendations
- Expires data automatically 2 hours after last report

**Note for API developers:** The backend uses Cloudflare Workers Durable Objects for stateful, per-match coordination.

---

## API Endpoints

### 1. Get Stream Recommendations

**Endpoint:** `GET /{leagueName}/{match}/recommendations`

**Purpose:** Get a ranked list of recommended streams based on real-time health data from other viewers.

**Authentication:** Requires valid Bearer token (same as other API endpoints)

**Request Example:**
```http
GET /premier-league/Arsenal%20vs%20Chelsea/recommendations HTTP/1.1
Host: your-worker-url.workers.dev
Authorization: Bearer YOUR_TOKEN
```

**Response Schema:**
```typescript
{
  recommended: RecommendationItem[];     // Array of recommended streams with metadata, ranked best to worst
  hasData: boolean;                      // Whether any health data exists
  confidence: 'high' | 'medium' | 'low' | 'none';
}

interface RecommendationItem {
  url: string;                           // Stream URL
  meta?: StreamMeta;                     // Optional metadata about this stream
}

interface StreamMeta {
  resolution?: string;                   // e.g., "1920x1080" from previous viewers
  framerate?: number;                    // e.g., 30, 60 from previous viewers
  videoCodec?: string;                   // e.g., "H.264", "H.265" from previous viewers
  audioCodec?: string;                   // e.g., "AAC", "AC-3" from previous viewers
  bitrate?: number;                      // Average bitrate in kbps from previous viewers
  lastMetaReportTime?: number;           // Timestamp when metadata was last reported
}
```

**Response Examples:**

*First viewer (no data yet):*
```json
{
  "recommended": [],
  "hasData": false,
  "confidence": "none"
}
```

*Sufficient data available with metadata:*
```json
{
  "recommended": [
    {
      "url": "https://live3.totalsportek777.com/stream-2",
      "meta": {
        "resolution": "1920x1080",
        "framerate": 60,
        "videoCodec": "H.264",
        "audioCodec": "AAC",
        "bitrate": 2500,
        "lastMetaReportTime": 1738713600000
      }
    },
    {
      "url": "https://live3.totalsportek777.com/stream-5",
      "meta": {
        "resolution": "1280x720",
        "framerate": 30,
        "videoCodec": "H.265",
        "audioCodec": "AAC",
        "bitrate": 1800,
        "lastMetaReportTime": 1738713500000
      }
    },
    {
      "url": "https://live3.totalsportek777.com/stream-1",
      "meta": null
    }
  ],
  "hasData": true,
  "confidence": "high"
}
```

**Confidence Levels:**
- `high`: Recent data (< 5 min), 3+ successful reports, active viewers, metadata available
- `medium`: Recent data + sufficient samples OR active viewers
- `low`: Either recent data OR sufficient samples
- `none`: No working streams found or data too old

---

### 2. Report Stream Health

**Endpoint:** `POST /{leagueName}/{match}/health`

**Purpose:** Report the health status of a stream you're testing or watching.

**Authentication:** Requires valid Bearer token

**Request Schema:**
```typescript
{
    streamUrl: string;            // Referrer URL for the stream being tested
    status: 'working' | 'failed' | 'buffering' | 'unknown';
    quality?: 'excellent' | 'good' | 'poor'; // Quality if playback was tested; omit if not yet played
    bitrate?: number;             // Measured bitrate in kbps; omit if quality not assessed
    buffering?: boolean;          // Whether buffering occurred
    error?: string;               // Error message if failed
    resolution?: string;          // Video resolution (e.g., "1920x1080"), sent once on first "working" report
    framerate?: number;           // Frames per second (e.g., 30, 60), sent once on first "working" report
    videoCodec?: string;          // Video codec name (e.g., "H.264", "H.265"), sent once on first "working" report
    audioCodec?: string;          // Audio codec name (e.g., "AAC", "AC-3"), sent once on first "working" report
    timestamp: number;            // Date.now() when measured
    sessionId: string;            // UUID for this client session (generate once per page load)
}
```

**Status Values:**
- `"working"` - Stream has been tested by attempting playback and is confirmed working
- `"failed"` - Playback attempt failed (connection error, timeout, invalid URL, etc.)
- `"buffering"` - Currently playing but experiencing buffering issues
- `"unknown"` - Stream exists but has not been tested yet (no playback attempt made)

**Video Metadata (Resolution, Framerate, Codecs):**

The optional video metadata fields should be populated **only on the first "working" status report** after playback begins. These details don't change during a streaming session, so they should be sent once to establish baseline quality information:

- `resolution`: Get from player's video track (e.g., "1920x1080", "1280x720")
- `framerate`: Get from player's video track (e.g., 30, 60)
- `videoCodec`: Get from player's video track and map to friendly name (e.g., "H.264" not "avc1")
- `audioCodec`: Get from player's audio track and map to friendly name (e.g., "AAC" not "mp4a")

**Platform-Specific Metadata Extraction:**
- **Android (Media3/ExoPlayer):** Access via `player.GetVideoFormat()` and `player.GetAudioFormat()`
- **iOS (AVFoundation):** Access via `AVPlayerItem.tracks` and media format properties
- **Windows (MediaElement/WinUI):** Access via `MediaPlayerState` or `MediaPlaybackSession` properties
- **macOS (AVKit):** Access via similar AVFoundation APIs as iOS

After initial "working" report with metadata, subsequent periodic reports can omit these fields (they don't change).

**Request Examples:**

Working stream with full video metadata (first report after playback starts):
```json
{
    "streamUrl": "https://live3.totalsportek777.com/Al-Nassr-vs-Al-Ittihad/62183",
    "status": "working",
    "quality": "excellent",
    "bitrate": 2500,
    "buffering": false,
    "resolution": "1920x1080",
    "framerate": 60,
    "videoCodec": "H.264",
    "audioCodec": "AAC",
    "timestamp": 1738713600000,
    "sessionId": "a1b2c3d4-e5f6-7890-abcd-1234567890ab"
}
```

Working stream subsequent periodic report (no metadata needed):
```json
{
    "streamUrl": "https://live3.totalsportek777.com/Al-Nassr-vs-Al-Ittihad/62183",
    "status": "working",
    "quality": "good",
    "bitrate": 1800,
    "buffering": false,
    "timestamp": 1738713660000,
    "sessionId": "a1b2c3d4-e5f6-7890-abcd-1234567890ab"
}
```

Working stream without quality metrics yet:
```json
{
    "streamUrl": "https://live3.totalsportek777.com/Al-Nassr-vs-Al-Ittihad/62183",
    "status": "working",
    "timestamp": 1738713600000,
    "sessionId": "a1b2c3d4-e5f6-7890-abcd-1234567890ab"
}
```

Stream not yet tested:
```json
{
    "streamUrl": "https://live3.totalsportek777.com/Al-Nassr-vs-Al-Ittihad/62183",
    "status": "unknown",
    "timestamp": 1738713600000,
    "sessionId": "a1b2c3d4-e5f6-7890-abcd-1234567890ab"
}
```

Failed stream:
```json
{
    "streamUrl": "https://live3.totalsportek777.com/Al-Nassr-vs-Al-Ittihad/62183",
    "status": "failed",
    "error": "Connection timeout after 5 seconds",
    "timestamp": 1738713600000,
    "sessionId": "a1b2c3d4-e5f6-7890-abcd-1234567890ab"
}
```

**Response:**
```json
{
  "success": true
}
```

**Error Responses:**
- `400`: Invalid report format
- `401`: Missing or invalid authentication
- `500`: Server error

---

### 3. Get Stream Statistics (Debug)

**Endpoint:** `GET /{leagueName}/{match}/stats`

**Purpose:** Detailed statistics for debugging and monitoring.

**Response Schema:**
```typescript
{
  streams: Array<{
        streamUrl: string;
    successCount: number;
    failureCount: number;
    successRate: number;        // 0-1
    lastSuccess: number | null; // Timestamp
    lastFailure: number | null; // Timestamp
    avgQuality: number;         // 0-5 scale
    avgBitrate: number;         // kbps
    activeViewers: number;
    lastReportTime: number;     // Timestamp
        meta?: {
            resolution?: string;
            framerate?: number;
            videoCodec?: string;
            audioCodec?: string;
            bitrate?: number;
            lastMetaReportTime?: number;
        };
  }>
}
```

---

## Client Implementation Guide

### .NET MAUI Specific Considerations

**Threading:**
- UI updates must be on main thread: Use `MainThread.BeginInvokeOnMainThread()` or `MainThread.InvokeOnMainThreadAsync()`
- Health reporting runs in background tasks: Use `Task.Run()` with `CancellationToken`
- HttpClient should be reused: Create singleton or use dependency injection

**Lifecycle:**
- Generate `SessionId` when navigating to match page (in ViewModel constructor or `OnNavigatedTo`)
- Cancel monitoring tasks in `OnDisappearing` or when disposing ViewModel
- Persist backup stream state across configuration changes

**HTTP Client:**
- Use `System.Net.Http.HttpClient` with singleton pattern
- Use `System.Text.Json.JsonSerializer` for serialization
- Configure timeout for recommendations (5-10 seconds recommended)

**Media Player Integration:**
- LibVLCSharp, MediaElement, or platform-specific players all work
- Hook into player events for error handling and metrics
- Report quality based on actual playback metrics when available

### Required Model Classes

```csharp
public class StreamHealthReport
{
    [JsonPropertyName("streamUrl")]
    public string StreamUrl { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } // "working" | "failed" | "buffering" | "unknown"
    
    [JsonPropertyName("quality")]
    public string? Quality { get; set; } // "excellent" | "good" | "poor"
    
    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }
    
    [JsonPropertyName("buffering")]
    public bool? Buffering { get; set; }
    
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("framerate")]
    public int? Framerate { get; set; }

    [JsonPropertyName("videoCodec")]
    public string? VideoCodec { get; set; }

    [JsonPropertyName("audioCodec")]
    public string? AudioCodec { get; set; }
    
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
    
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; }
}

public class RecommendationResponse
{
    [JsonPropertyName("recommended")]
    public List<RecommendationItem> Recommended { get; set; } = new();
    
    [JsonPropertyName("hasData")]
    public bool HasData { get; set; }
    
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } // "high" | "medium" | "low" | "none"
}

public class RecommendationItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("meta")]
    public StreamMeta? Meta { get; set; }
}

public class StreamStatsResponse
{
    [JsonPropertyName("streams")]
    public List<StreamStats> Streams { get; set; } = new();
}

public class StreamStats
{
    [JsonPropertyName("streamUrl")]
    public string StreamUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("successCount")]
    public int SuccessCount { get; set; }
    
    [JsonPropertyName("failureCount")]
    public int FailureCount { get; set; }
    
    [JsonPropertyName("successRate")]
    public double SuccessRate { get; set; }
    
    [JsonPropertyName("lastSuccess")]
    public long? LastSuccess { get; set; }
    
    [JsonPropertyName("lastFailure")]
    public long? LastFailure { get; set; }
    
    [JsonPropertyName("avgQuality")]
    public double AvgQuality { get; set; }
    
    [JsonPropertyName("avgBitrate")]
    public double AvgBitrate { get; set; }
    
    [JsonPropertyName("activeViewers")]
    public int ActiveViewers { get; set; }
    
    [JsonPropertyName("lastReportTime")]
    public long LastReportTime { get; set; }

    [JsonPropertyName("meta")]
    public StreamMeta? Meta { get; set; }
}

public class StreamMeta
{
    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("framerate")]
    public int? Framerate { get; set; }

    [JsonPropertyName("videoCodec")]
    public string? VideoCodec { get; set; }

    [JsonPropertyName("audioCodec")]
    public string? AudioCodec { get; set; }

    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }

    [JsonPropertyName("lastMetaReportTime")]
    public long? LastMetaReportTime { get; set; }
}
```

### Recommended Workflow

```
1. User selects a match
2. Fetch match streams: GET /{league}/{match}
3. Fetch recommendations: GET /{league}/{match}/recommendations
4. IF recommendations.confidence is 'high' or 'medium':
     Test recommended streams first
   ELSE:
     Test streams in original order
5. Test streams sequentially until finding 2 working streams:
     - Load stream into player
     - Wait 3-5 seconds for playback confirmation
     - Extract video metadata (resolution, framerate, videoCodec, audioCodec)
     - Report result: POST /{league}/{match}/health
       * On first "working" report: include video metadata
       * On subsequent reports: omit metadata (it doesn't change)
     - If working: keep as primary or backup
     - If failed: try next stream
     - AUTOMATICALLY STOP TESTING once 2 working streams found (pause testing)
6. Keep untested streams list for later backup discovery
7. Play primary stream, keep secondary as backup
8. During playback (every 30-60 seconds):
     Report health with quality/bitrate metrics
     Monitor for health decline (see thresholds below)
9. On primary stream failure OR health decline:
     - Report failure/degradation
     - Switch to backup stream
     - Resume testing untested streams to find new backup
10. On user stream selection change:
     - Report which stream user selected
     - Pause testing of other streams
     - Resume normal monitoring
```

---

## Important: M3U8 URL Validity

**Critical Understanding:**

The `/play` endpoint returns an m3u8 URL from the source stream, but **this URL is NOT validated as working**. Several factors affect stream URL validity:

1. **URL expiry** - Source streams often have time-limited URLs (30 min - 24 hours). A URL valid 5 minutes ago may be expired now.
2. **Source availability** - The streaming source may have gone offline since URL was extracted
3. **Geographic/IP blocks** - URL may be blocked in certain regions or from certain IPs
4. **CDN failures** - Source CDN may be experiencing issues
5. **Authentication changes** - Source may require re-authentication

**What this means for clients:**

- **Don't cache m3u8 URLs for long-term use** - Refresh before switching to backup stream
- **Test streams by attempting playback** - This is the only validation that matters
- **Pre-fetch fresh URLs** - When about to switch to backup, request fresh m3u8 from `/play` first
- **Report actual playback results** - Health reports validate whether a previously-working URL still works
- **Handle URL refresh on backup switch** - Get fresh URL before switching, not from cache

**Architecture implication:**

When switching from primary to backup stream:
```
Old approach (INCORRECT):
  Primary fails → Switch to cached backup URL → Backup also expired → User sees failure

Correct approach:
  Primary fails → Request fresh m3u8 for backup → Switch to fresh URL → Likely works
```

**Code pattern:**

```csharp
// When primary fails
private async Task OnPrimaryStreamFailedAsync()
{
    if (_backupStream != null)
    {
        // Request FRESH m3u8 URL before switching
        var freshM3u8 = await GetFreshStreamUrlAsync(_backupStream);
        
        // Then switch
        await SwitchToStreamAsync(_backupStream, freshM3u8);
        
        // Report the switch
        await ReportHealthAsync(_league, _match, new StreamHealthReport
        {
            StreamUrl = _backupStream.Url,
            Status = "working", // Will be validated by actual playback
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionId = _sessionId
        });
    }
}

// Pre-test a backup stream before switching
private async Task<bool> PreTestBackupStreamAsync(Stream stream)
{
    // Get fresh m3u8 URL
    var m3u8Url = await GetFreshStreamUrlAsync(stream);
    
    // Load into player without displaying yet
    var testResult = await TestStreamPlaybackAsync(m3u8Url, timeoutSeconds: 5);
    
    // Report result (validates URL is working)
    await ReportHealthAsync(_league, _match, new StreamHealthReport
    {
        StreamUrl = stream.Url,
        Status = testResult ? "working" : "failed",
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SessionId = _sessionId
    });
    
    return testResult;
}
```

---

### Stream Testing Policy

**Initial Discovery Phase:**
- Test streams **sequentially** (one at a time) in recommended order
- For each stream to test:
  1. Call `/play` endpoint to get m3u8 URL for that stream
  2. Attempt playback with that URL (3-5 second validation window)
  3. Report result immediately:
     - If playback starts successfully → report `status: 'working'` (include quality metrics if available, AND video metadata: resolution, framerate, videoCodec, audioCodec)
     - If playback fails → report `status: 'failed'` with error
- **STOP testing immediately** after finding 2 working streams
  - Pause stream testing via `PauseTesting()` call
  - Remaining streams are marked as untested for later use
  - Do NOT test remaining streams during initial discovery phase
    - Record untested stream URLs for future backup discovery
- Do NOT cache m3u8 URLs - they expire and need refreshing

**Resume Testing Triggers:**

Only resume testing untested streams in these scenarios:

1. **Primary stream fails** (playback error, connection drops)
   - Switch to backup immediately (with fresh m3u8 URL)
   - Start background task testing remaining untested streams
   - For each untested: call `/play` for fresh URL, test, report result
   - Find next working stream to replace backup
   
2. **Backup stream fails** (while being watched)
   - Get fresh m3u8 URL from `/play` for best untested stream
   - Switch to it and start playback
   - Keep testing other untested for replacement backup
   
3. **User explicitly switches streams**
   - Get fresh m3u8 URL from `/play` for selected stream
   - New stream becomes primary
   - Clean up monitoring of previous primary
   - Resume testing untested for backup if needed

**Do NOT resume testing if:**
- Both primary and backup are healthy
- User is actively watching and happy with current stream
- Recommendations are providing good data

**Code Example:**
```csharp
private List<Stream> _untestedStreams;
private bool _isTestingUntested = false;

public async Task StartTestingUntestedStreamsAsync()
{
    if (_isTestingUntested || _untestedStreams.Count == 0)
        return;
    
    _isTestingUntested = true;
    
    while (_untestedStreams.Count > 0 && _backupStream == null)
    {
        var nextStream = _untestedStreams[0];
        var result = await TestStreamAsync(nextStream);
        
        await ReportHealthAsync(_league, _match, new StreamHealthReport
        {
            StreamUrl = nextStream.Url,
            Status = result.IsWorking ? "working" : "failed",
            // ... other fields
        });
        
        _untestedStreams.RemoveAt(0);
        
        if (result.IsWorking)
        {
            _backupStream = nextStream;
            break;
        }
    }
    
    _isTestingUntested = false;
}
```

### Backup Management Strategy

**Primary Stream Monitoring:**
- Monitor continuously during playback
- Report health every 30-60 seconds
- Detect failures immediately (error events)
- Detect degradation using thresholds (see below)

**Backup Stream Maintenance:**
- Keep second working stream URL stored (not the m3u8 URL - URLs expire)
- On primary failure, request fresh m3u8 URL from `/play` before switching
- Switch to backup only after getting fresh, validated URL
- Test untested streams to find replacement for backup
- Pre-fetch fresh URLs before backup switch to minimize latency

**Automatic Failover Logic:**
```csharp
private async void OnPrimaryStreamDegrading()
{
    // Immediate switch to backup
    if (_backupStream != null)
    {
        await SwitchToStreamAsync(_backupStream);
        
        // Report the switch
        await ReportHealthAsync(_league, _match, new StreamHealthReport
        {
            StreamUrl = _currentStream.Url,
            Status = "failed",
            Error = "Primary stream degraded",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionId = _sessionId
        });
        
        // Find new backup in background
        _ = StartTestingUntestedStreamsAsync();
    }
    else
    {
        // No backup, show user warning
        await DisplayAlert(
            "Stream Quality Degraded",
            "No backup stream available. Trying to recover...",
            "OK");
    }
}
```

**User-Initiated Stream Switching:**
- Stop monitoring current stream
- Switch to user-selected stream
- Start monitoring new stream
- Keep backup ready if one exists
- Report stream change:

```csharp
public async Task SwitchToUserSelectedStreamAsync(Stream stream)
{
    // Stop monitoring current
    StopHealthMonitoring();
    
    // Switch
    await SwitchToStreamAsync(stream);
    _currentStream = stream;
    
    // Report user preference
    await ReportHealthAsync(_league, _match, new StreamHealthReport
    {
        StreamUrl = stream.Url,
        Status = "working",
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SessionId = _sessionId
    });
    
    // Start monitoring new stream
    StartHealthMonitoring(stream.Url);
}
```

### Health Decline Thresholds

**Quality Degradation Detection:**

Switch to backup if ANY of these occur during primary playback:

| Metric | Threshold | Action |
|--------|-----------|--------|
| **Buffering events** | 2+ in 60 seconds | Degrade quality rating |
| **Buffering events** | 4+ in 60 seconds | TRIGGER FAILOVER |
| **Bitrate drop** | Below 500 kbps sustained | Check other metrics |
| **Bitrate drop** | Below 300 kbps sustained | TRIGGER FAILOVER |
| **Resolution drop** | <480p sustained | Check bitrate, other metrics |
| **Error rate** | 1 error = immediate report | Track for pattern |
| **Error rate** | 3+ errors in 5 min | TRIGGER FAILOVER |
| **Connection timeouts** | 1 timeout = immediate report | Track for pattern |
| **Connection timeouts** | 2+ in 5 min | TRIGGER FAILOVER |

**Monitoring Implementation:**

```csharp
private class StreamMetricsWindow
{
    public int BufferingEvents { get; set; }
    public List<int> BitrateReadings { get; set; } = new();
    public List<DateTime> ErrorTimes { get; set; } = new();
    public DateTime WindowStart { get; set; } = DateTime.UtcNow;
    
    public void AddBufferingEvent() => BufferingEvents++;
    
    public void AddBitrate(int bitrate) => BitrateReadings.Add(bitrate);
    
    public void AddError() => ErrorTimes.Add(DateTime.UtcNow);
    
    public void ResetIfExpired(int windowSeconds = 60)
    {
        if ((DateTime.UtcNow - WindowStart).TotalSeconds > windowSeconds)
        {
            BufferingEvents = 0;
            BitrateReadings.Clear();
            WindowStart = DateTime.UtcNow;
        }
        
        // Clean old errors (older than 5 minutes)
        ErrorTimes.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 300);
    }
    
    public bool IsHealthDeclined()
    {
        ResetIfExpired();
        
        // 4+ buffering events in window = degraded
        if (BufferingEvents >= 4) return true;
        
        // Check bitrate trend
        if (BitrateReadings.Count >= 3)
        {
            var lastThree = BitrateReadings.TakeLast(3).ToList();
            var avgBitrate = lastThree.Average();
            
            // Sustained below 300 kbps = degraded
            if (avgBitrate < 300) return true;
            
            // All below 500 for extended period = degraded
            if (lastThree.All(b => b < 500) && BitrateReadings.Count >= 10) return true;
        }
        
        // 3+ errors in 5 minutes = degraded
        if (ErrorTimes.Count >= 3) return true;
        
        return false;
    }
}

public class MatchStreamViewModel : INotifyPropertyChanged, IDisposable
{
    private StreamMetricsWindow _metricsWindow = new();
    
    private void UpdateStreamMetrics()
    {
        var metrics = GetCurrentPlayerMetrics();
        
        if (metrics.IsBuffering)
            _metricsWindow.AddBufferingEvent();
        
        if (metrics.Bitrate.HasValue)
            _metricsWindow.AddBitrate(metrics.Bitrate.Value);
        
        // Check if health declined
        if (_metricsWindow.IsHealthDeclined())
        {
            _ = OnPrimaryStreamDegradingAsync();
        }
    }
    
    private void OnPlayerError(object sender, MediaErrorEventArgs e)
    {
        _metricsWindow.AddError();
        
        if (_metricsWindow.IsHealthDeclined())
        {
            _ = OnPrimaryStreamDegradingAsync();
        }
    }
}
```

**Health Recovery:**

If primary stream recovers:
- Continue monitoring
- Do NOT switch back unless user requests
- Keep backup ready
- Report recovery to backend

---

### Implementation Checklist

**Initial Stream Discovery:**
- [ ] Generate `SessionId` using `Guid.NewGuid().ToString()` per page navigation
- [ ] Create model classes with `[JsonPropertyName]` attributes
- [ ] Set up singleton `HttpClient` instance (via DI or static)
- [ ] Request recommendations before testing streams
- [ ] For each stream test: call `/play` to get fresh m3u8, attempt playback, report result
- [ ] Test streams sequentially (not in parallel) until finding 2 working
- [ ] **STOP testing after finding 2 working streams**
- [ ] Store untested stream URLs for later
- [ ] Report both successes and failures immediately
- [ ] **DO NOT cache m3u8 URLs** - they expire and need refreshing before use

**Ongoing Monitoring:**
- [ ] Report ongoing health during playback (every 30-60s) using background Task
- [ ] Implement `StreamMetricsWindow` or equivalent to track health metrics
- [ ] Detect health decline using defined thresholds (buffering, bitrate, errors)
- [ ] Trigger failover to backup on health decline (not just hard failures)
- [ ] Report failures and degradation to backend

**Backup Management:**
- [ ] Keep backup pre-loaded for instant failover
- [ ] On primary failure/degradation: switch to backup immediately
- [ ] Resume testing untested streams to find replacement backup
- [ ] Support user-initiated stream switches
- [ ] Resume testing only when: primary fails, backup fails, user switches, or explicitly requested

**Cleanup & Edge Cases:**
- [ ] Report final status when user navigates away (in `OnDisappearing`)
- [ ] Handle recommendation endpoint being unavailable (fallback to testing all)
- [ ] Use stream URL from match response, not array position
- [ ] Cancel background tasks properly using `CancellationTokenSource`
- [ ] Use `MainThread` for UI updates after async operations
- [ ] Configure HttpClient timeout (5-10 seconds for recommendations)

### Session ID Generation

```csharp
// Generate once per app session/page navigation, store in field or property
private string _sessionId = Guid.NewGuid().ToString();
// Example: "a1b2c3d4-e5f6-7890-abcd-1234567890ab"

// Or in a view model:
public string SessionId { get; } = Guid.NewGuid().ToString();
```

### Quality Detection Logic

Map video player metrics to quality levels. Return `null` if playback hasn't been assessed:

```csharp
public string? DetectQuality(int? bitrate, (int width, int height)? resolution, int bufferingEvents)
{
    // If playback metrics not available, quality is unknown
    if (!bitrate.HasValue || !resolution.HasValue)
        return null;
    
    if (bufferingEvents > 3) return "poor";
    if (bitrate >= 2000 && resolution.Value.height >= 720) return "excellent";
    if (bitrate >= 1000 || resolution.Value.height >= 480) return "good";
    return "poor";
}
```

Usage:
```csharp
var quality = DetectQuality(metrics.Bitrate, metrics.Resolution, metrics.BufferingEvents);

await ReportHealthAsync(league, match, new StreamHealthReport
{
    StreamUrl = stream.Url,
    Status = "working",
    Quality = quality, // May be null if playback not assessed
    Bitrate = metrics.Bitrate,
    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    SessionId = sessionId
});
```

### Example: Testing Streams

```csharp
public async Task<List<WorkingStream>> FindWorkingStreamsAsync(
    string league, 
    string match, 
    List<Stream> streams, 
    string sessionId)
{
    var recommendations = await FetchRecommendationsAsync(league, match);
    
    // Prioritize recommended streams if confidence is sufficient
    var recommendedUrls = recommendations.Recommended.Select(r => r.Url).ToList();
    var recommendedStreams = streams
        .Where(s => recommendedUrls.Contains(s.Url))
        .OrderBy(s => recommendedUrls.IndexOf(s.Url))
        .ToList();
    var fallbackStreams = streams
        .Where(s => !recommendedUrls.Contains(s.Url))
        .ToList();
    var testOrder = recommendations.Confidence != "none"
        ? recommendedStreams.Concat(fallbackStreams).ToList()
        : streams.ToList();
    
    var workingStreams = new List<WorkingStream>();
    
    foreach (var stream in testOrder)
    {
        if (workingStreams.Count >= 2) break;
        
        var result = await TestStreamAsync(stream);
        
        await ReportHealthAsync(league, match, new StreamHealthReport
        {
            StreamUrl = stream.Url,
            Status = result.Working ? "working" : "failed",
            Quality = result.Quality,
            Bitrate = result.Bitrate,
            Error = result.Error,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionId = sessionId
        });
        
        if (result.Working)
        {
            workingStreams.Add(new WorkingStream { StreamUrl = stream.Url, Stream = stream });
        }
    }
    
    return workingStreams;
}
```

### Example: Ongoing Health Reporting

```csharp
private CancellationTokenSource _monitoringCts;

public void StartHealthMonitoring(
    string league, 
    string match, 
    string streamUrl, 
    string sessionId, 
    IMediaPlayer player)
{
    _monitoringCts = new CancellationTokenSource();
    
    Task.Run(async () =>
    {
        while (!_monitoringCts.Token.IsCancellationRequested)
        {
            try
            {
                var metrics = player.GetMetrics(); // Get from your video player
                
                await ReportHealthAsync(league, match, new StreamHealthReport
                {
                    StreamUrl = streamUrl,
                    Status = player.IsPlaying ? "working" : "buffering",
                    Quality = DetectQuality(metrics.Bitrate, metrics.Resolution, metrics.BufferEvents),
                    Bitrate = metrics.Bitrate,
                    Buffering = metrics.IsBuffering,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SessionId = sessionId
                });
                
                await Task.Delay(30000, _monitoringCts.Token); // Every 30 seconds
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Health monitoring error: {ex.Message}");
            }
        }
    }, _monitoringCts.Token);
}

public void StopHealthMonitoring()
{
    _monitoringCts?.Cancel();
    _monitoringCts?.Dispose();
}
```

### Example: Failure Handling

```csharp
private async void OnPlayerError(object sender, MediaErrorEventArgs e)
{
    // Report failure
    await ReportHealthAsync(_currentLeague, _currentMatch, new StreamHealthReport
    {
        StreamUrl = _currentStream.Url,
        Status = "failed",
        Error = e.Exception?.Message ?? e.ErrorMessage,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SessionId = _sessionId
    });
    
    // Switch to backup
    if (_backupStream != null)
    {
        await SwitchToStreamAsync(_backupStream);
        // Find new backup from untested streams
        _backupStream = await FindNextWorkingStreamAsync(_untestedStreams);
    }
    else
    {
        // No backup available, show error to user
        await DisplayAlert("Stream Error", "Stream failed and no backup available", "OK");
    }
}
```

---

## Error Handling

### Recommendation Endpoint Unavailable

If the recommendations endpoint fails or times out, fall back to testing streams in original order:

```csharp
List<Stream> testOrder;
try
{
    var recs = await FetchRecommendationsAsync(league, match);
    var recommendedUrls = recs.Recommended.Select(r => r.Url).ToHashSet();
    testOrder = recs.Confidence != "none"
        ? streams.Where(s => recommendedUrls.Contains(s.Url)).ToList()
        : null;
}
catch (Exception ex)
{
    Debug.WriteLine($"Recommendations unavailable: {ex.Message}, testing all streams");
    testOrder = null;
}

// Use testOrder if available, otherwise test all streams
var streamsToTest = testOrder ?? streams.ToList();
```

### Health Report Failures

Health report failures should be logged but not block the user experience:

```csharp
public async Task ReportHealthAsync(string league, string match, StreamHealthReport report)
{
    try
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _authToken);
        
        var json = JsonSerializer.Serialize(report);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        var response = await client.PostAsync(
            $"{ApiBaseUrl}/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(match)}/health",
            content);
        
        if (!response.IsSuccessStatusCode)
        {
            Debug.WriteLine($"Health report failed with status: {response.StatusCode}");
        }
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"Failed to report health: {ex.Message}");
        // Don't throw - this is non-blocking
    }
}
```

---

## Testing Considerations

### Sample ViewModel Implementation

Here's a complete example of how to integrate this into a MAUI ViewModel:

```csharp
public class MatchStreamViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IStreamHealthService _healthService;
    private readonly string _sessionId;
    private readonly string _league;
    private readonly string _match;
    private CancellationTokenSource _monitoringCts;
    
    private Stream _primaryStream;
    private Stream _backupStream;
    private List<Stream> _untestedStreams;
    
    public MatchStreamViewModel(
        IStreamHealthService healthService,
        string league,
        string match)
    {
        _healthService = healthService;
        _league = league;
        _match = match;
        _sessionId = Guid.NewGuid().ToString();
    }
    
    public async Task InitializeAsync(List<Stream> allStreams)
    {
        try
        {
            // Get recommendations
            var recommendations = await _healthService
                .GetRecommendationsAsync(_league, _match)
                .ConfigureAwait(false);
            
            // Determine test order
            var recommendedUrls = recommendations.Recommended.Select(r => r.Url).ToList();
            var recommendedStreams = allStreams
                .Where(s => recommendedUrls.Contains(s.Url))
                .OrderBy(s => recommendedUrls.IndexOf(s.Url))
                .ToList();
            var fallbackStreams = allStreams
                .Where(s => !recommendedUrls.Contains(s.Url))
                .ToList();
            var testOrder = recommendations.Confidence != "none"
                ? recommendedStreams.Concat(fallbackStreams).ToList()
                : allStreams.ToList();
            
            // Find 2 working streams
            var workingStreams = new List<Stream>();
            
            foreach (var stream in testOrder)
            {
                if (workingStreams.Count >= 2) break;
                
                var result = await TestStreamAsync(stream);
                
                // Report result (fire and forget)
                _ = _healthService.ReportHealthAsync(_league, _match, new StreamHealthReport
                {
                    StreamUrl = stream.Url,
                    Status = result.IsWorking ? "working" : "failed",
                    Quality = result.Quality,
                    Bitrate = result.Bitrate,
                    Error = result.Error,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SessionId = _sessionId
                });
                
                if (result.IsWorking)
                {
                    workingStreams.Add(stream);
                }
            }
            
            if (workingStreams.Count == 0)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                    await Application.Current.MainPage.DisplayAlert(
                        "Error", "No working streams found", "OK"));
                return;
            }
            
            // Set primary and backup
            _primaryStream = workingStreams[0];
            _backupStream = workingStreams.Count > 1 ? workingStreams[1] : null;
            
            // Track untested for future backup finding
            _untestedStreams = testOrder
                .Skip(workingStreams.Count)
                .ToList();
            
            // Start playing and monitoring
            await MainThread.InvokeOnMainThreadAsync(() => PlayStream(_primaryStream));
            StartHealthMonitoring(_primaryStream.Url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Initialize failed: {ex.Message}");
            // Fall back to testing all streams without recommendations
        }
    }
    
    private void StartHealthMonitoring(string streamUrl)
    {
        _monitoringCts = new CancellationTokenSource();
        
        Task.Run(async () =>
        {
            while (!_monitoringCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(30000, _monitoringCts.Token);
                    
                    var metrics = GetCurrentPlayerMetrics();
                    
                    await _healthService.ReportHealthAsync(league, match, new StreamHealthReport
                    {
                        StreamUrl = streamUrl,
                        Status = metrics.IsPlaying ? "working" : "buffering",
                        Quality = DetectQuality(metrics.Bitrate, metrics.Resolution, metrics.BufferEvents),
                        Bitrate = metrics.Bitrate,
                        Buffering = metrics.IsBuffering,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SessionId = sessionId
                    });
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Health monitoring error: {ex.Message}");
                }
            }
        }, _monitoringCts.Token);
    }
    
    public void Dispose()
    {
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
    }
    
    // Implement INotifyPropertyChanged and other methods...
}
```

### Dependency Injection Pattern (Recommended)

For better testability and maintainability, wrap API calls in a service:

```csharp
public interface IStreamHealthService
{
    Task<RecommendationResponse> GetRecommendationsAsync(string league, string match);
    Task ReportHealthAsync(string league, string match, StreamHealthReport report);
    Task<StreamStatsResponse> GetStatsAsync(string league, string match);
}

public class StreamHealthService : IStreamHealthService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private readonly string _authToken;

    public StreamHealthService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _apiBaseUrl = config["ApiBaseUrl"];
        _authToken = config["AuthToken"];
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _authToken);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<RecommendationResponse> GetRecommendationsAsync(string league, string match)
    {
        var url = $"{_apiBaseUrl}/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(match)}/recommendations";
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<RecommendationResponse>(json);
    }

    public async Task ReportHealthAsync(string league, string match, StreamHealthReport report)
    {
        try
        {
            var url = $"{_apiBaseUrl}/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(match)}/health";
            var json = JsonSerializer.Serialize(report);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            await _httpClient.PostAsync(url, content);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to report health: {ex.Message}");
            // Non-blocking
        }
    }

    public async Task<StreamStatsResponse> GetStatsAsync(string league, string match)
    {
        var url = $"{_apiBaseUrl}/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(match)}/stats";
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<StreamStatsResponse>(json);
    }
}

// Register in MauiProgram.cs:
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IStreamHealthService, StreamHealthService>();
```

### Local Development

When testing locally, recommendations will initially return no data. To build up test data:
1. Test streams manually and report results
2. Use multiple browser sessions to simulate different viewers
3. Check `/stats` endpoint to verify data is being collected

### Integration Tests

Mock the recommendations endpoint to test different scenarios:
- No data (first viewer)
- Low confidence recommendations
- High confidence recommendations
- Endpoint unavailable (fallback behavior)

---

## Performance Characteristics

- **Recommendations latency**: < 50ms (Durable Object access)
- **Health report latency**: < 50ms (fire-and-forget recommended)
- **Data expiry**: 2 hours after last report
- **Recommendation updates**: Real-time (no caching)
- **Concurrent viewers**: Unlimited (DO handles coordination)

---

## Best Practices

### General Stream Management

1. **Stop testing after 2 working streams found** - Do NOT test all streams upfront
2. **Fresh m3u8 URLs on every use** - Always call `/play` before switching streams, URLs expire
3. **Test by attempting playback** - Only actual playback validates a stream works
4. **Only resume testing when necessary** - On primary/backup failure or user switch
5. **Monitor for health decline, not just hard failures** - Use bitrate, buffering, and error thresholds
6. **Switch to backup proactively** - Don't wait for complete failure, switch on degradation
7. **Always keep a backup ready** - Critical for seamless user experience
8. **Report immediately** - All health changes (success, failure, degradation)
9. **Report periodically** - Every 30-60 seconds during playback
10. **Handle failures gracefully** - Automatic backup failover without user disruption

### URL Management Strategy

1. **Never cache m3u8 URLs** - They expire (30 min to 24 hours depending on source)
2. **Get fresh URL before every use** - Call `/play` right before attempting playback
3. **Pre-fetch on backup switch** - Request fresh URL as soon as primary fails, minimize wait
4. **Include URL in backup pre-test** - Get fresh URL, test playback, validate before switching
5. **Store stream URL, not m3u8 URL** - Persistent storage should keep stream URLs only
6. **Handle "URL expired" errors** - If playback fails, request fresh URL and retry once
7. **Optimize for low latency** - Pre-fetch backup URL before switching if possible

### Health Metrics Tracking

1. **Track buffering events** - 4+ per minute = unacceptable
2. **Monitor bitrate trends** - Sustained <500 kbps = degraded, <300 kbps = critical
3. **Count errors** - 3+ in 5 minutes = failing stream
4. **Use sliding windows** - Keep metrics localized to recent history
5. **Report all degradation** - Let backend build intelligence on stream reliability
6. **Factor in resolution changes** - 720p→480p without bitrate drop may be acceptable

### Backup Failover Strategy

1. **Keep backup pre-loaded** - Minimize switch latency
2. **Switch immediately on detection** - Don't give user buffering/errors
3. **Continue monitoring degraded stream** - May recover, helps backend data
4. **Find replacement backup asynchronously** - Don't block user experience
5. **Test untested streams, not re-test known failures** - Efficient backup finding
6. **Cache backup for unavailability windows** - Network hiccups shouldn't require new test

### Network Considerations

1. **Handle temporary network blips** - Single error ≠ stream failure
2. **Implement backoff for testing** - Don't hammer untested streams
3. **Consider platform network constraints** - Mobile vs desktop vary
4. **Be resilient to recommendation endpoint downtime** - Fallback to sequential testing
5. **Use exponential backoff for failed streams** - 5s, 10s, 30s delays before retesting

### .NET MAUI Specific

1. **Use CancellationToken** for all background tasks and cancel on page navigation
2. **Reuse HttpClient** via singleton or DI - don't create new instances per request
3. **Use MainThread** for UI updates after async operations
4. **Implement IDisposable** on ViewModels that start background tasks
5. **Handle platform differences** - iOS may have different network constraints than Android
6. **Test on real devices** - emulators may have different network characteristics
7. **Use Debug.WriteLine** for logging - it works across all platforms

---

## Changelog

### v1.2 (2026-02-06)
- **Added critical section on M3U8 URL Validity**
- Clarified that `/play` endpoint returns unvalidated URLs
- Emphasized client must test by attempting playback
- Added requirement to get fresh URLs before switching streams
- Explained URL expiry and when refreshing is necessary
- Updated Stream Testing Policy to clarify `/play` workflow
- Updated Resume Testing Triggers to include URL refresh
- Added "URL Management Strategy" best practices section
- Clarified storage should keep stream URL, not m3u8 URL
- Added code examples for fresh URL fetching before switch
- **Added "unknown" status** - allows reporting streams that haven't been tested yet
- **Clarified quality field is optional** - `status: "working"` always means playback tested; quality optional if metrics not available yet
- Updated Request Schema with 4 status values: working, failed, buffering, unknown
- Added example for reporting untested streams with `status: "unknown"`
- Updated Confidence Levels with "none" description

### v1.1 (2026-02-06)
- Added detailed Stream Lifecycle Management section
- Defined explicit Stream Testing Policy with resume triggers
- Specified Backup Management Strategy with failover logic
- Added Health Decline Thresholds table with specific metrics
- Added StreamMetricsWindow code example for health tracking
- Enhanced Implementation Checklist with categorized tasks
- Expanded Best Practices with health metrics and network considerations
- Clarified when to stop testing and resume testing untested streams
- Added automatic failover guidance based on health degradation

### v1.0 (2026-02-05)
- Initial protocol specification
- Three endpoints: recommendations, health reporting, statistics
- Confidence-based recommendation system
- 2-hour data expiry window
