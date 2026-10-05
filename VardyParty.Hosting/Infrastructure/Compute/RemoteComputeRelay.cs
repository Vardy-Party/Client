using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Auth;
using VardyParty.Kernel;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Hosting;

/// <summary>
/// Guest side of the compute relay. Browser CONNECT targets are resolved
/// here with DoH first (carrier DNS on 4G often answers and would skip DoH).
/// </summary>
public sealed class RemoteComputePlayClient(
    IRemoteComputePreferences preferences,
    IAuthTokenProvider tokens,
    IOptions<APISettings> apiSettings,
    IHostNameResolver resolver,
    IDnsPreferencesStore dnsPreferences,
    IDnsOverHttpsClient doh,
    ILogger<RemoteComputePlayClient> logger,
    IDnsOverHttpsEndpoint? dnsEndpoint = null,
    LocalServiceConnection? connection = null) : IRemoteComputePlay
{
    internal const string DirectConnectionDroppedMessage = "direct connection dropped";

    private static readonly TimeSpan MpTimeout = RemoteComputeHttp.FindingStreamsTimeout;

    /// <summary>
    /// How long to wait for the data channel before telling the host ICE failed.
    /// On 4G the channel needs the answer to travel back through the relay,
    /// STUN on both sides, ICE connectivity checks, then the DTLS handshake and
    /// SCTP open; 2 s never succeeded. The host waits 12 s for "ready", so this
    /// stays inside that budget and the phone's "failed" signal arrives first.
    /// </summary>
    private static readonly TimeSpan DirectOpenBudget = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public bool IsPaired => !string.IsNullOrWhiteSpace(preferences.LoadPairedHostSub());

    public RemoteComputeFault? LastFault { get; private set; }

    public event Action<RemoteComputeFault>? Faulted;

    public async Task<M3U8Response?> ResolveAsync(
        bool useMp,
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var apiBase = apiSettings.Value.HeadlessBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            return Fail(correlationId, "This device", "API address is not configured");
        }

        var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Fail(correlationId, "This device", "Not signed in");
        }
        logger.LogInformation("[RemoteCompute] {CorrelationId} resolving {Url}", correlationId, streamUrl);
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(RelayUri(apiBase, correlationId), cancellationToken).ConfigureAwait(false);
            var session = new GuestSession(
                socket,
                resolver,
                doh,
                dnsPreferences.LoadDnsOverHttpsFallbackEnabled(),
                logger,
                correlationId);
            var receive = session.ReceiveAsync(cancellationToken);
            try
            {
                var id = Guid.NewGuid().ToString("N");
                var headers = BuildHeaders(token, correlationId);
                string path;
                string? body = null;
                string method;
                if (useMp)
                {
                    method = "POST";
                    path = "/mp";
                    body = JsonSerializer.Serialize(new
                    {
                        pageUrl = streamUrl,
                        stream = string.IsNullOrWhiteSpace(playerStreamName) ? null : playerStreamName.Trim()
                    });
                }
                else
                {
                    method = "GET";
                    path = "/play/" + Uri.EscapeDataString(streamUrl);
                    if (!string.IsNullOrWhiteSpace(playerStreamName))
                    {
                        path += "?stream=" + Uri.EscapeDataString(playerStreamName.Trim());
                    }
                }

                var pending = session.Arm(id);
                await session.SendAsync(new { t = "rpc", id, cid = correlationId, method, path, headers, body }, cancellationToken)
                    .ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(useMp ? MpTimeout : TimeSpan.FromSeconds(30));
                try
                {
                    var result = await pending.WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (result.Status is < 200 or >= 300)
                    {
                        await session.SendAsync(new
                        {
                            t = "log",
                            cid = correlationId,
                            level = "warning",
                            source = "guest",
                            message = $"Guest received HTTP {result.Status}"
                        }, CancellationToken.None).ConfigureAwait(false);
                    }

                    return Interpret(result.Status, result.Body, correlationId, session.EgressProblem);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await session.SendAsync(new
                    {
                        t = "log",
                        cid = correlationId,
                        level = "error",
                        source = "guest",
                        message = useMp
                            ? $"Guest timed out after {(int)MpTimeout.TotalSeconds}s"
                            : "Guest timed out after 30s"
                    }, CancellationToken.None).ConfigureAwait(false);
                    return Fail(correlationId, "Local service", "Timed out waiting for the remote PC");
                }
                catch (DirectConnectionDroppedException)
                {
                    return Fail(correlationId, "This phone", DirectConnectionDroppedMessage);
                }
            }
            finally
            {
                session.Stop();
                try
                {
                    await receive.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // session ended
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[RemoteCompute] {CorrelationId} relay resolve failed for {Url}", correlationId, streamUrl);
            return Fail(correlationId, "Relay", "Could not reach the compute relay");
        }
    }

    private Dictionary<string, string> BuildHeaders(string token, string correlationId)
    {
        var enabled = dnsPreferences.LoadDnsOverHttpsFallbackEnabled();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + token,
            ["X-Correlation-Id"] = correlationId,
            ["X-Vardy-Doh"] = enabled ? "1" : "0"
        };
        if (!string.IsNullOrWhiteSpace(connection?.Id))
        {
            headers[LocalServiceConnection.HeaderName] = connection.Id;
        }
        if (enabled && dnsEndpoint?.Address is not null)
        {
            headers["X-Vardy-Doh-Resolver"] = dnsEndpoint.Address.ToString();
        }

        return headers;
    }

    private M3U8Response? Interpret(int status, string body, string correlationId, string? egressProblem)
    {
        M3U8Response? result = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                result = JsonSerializer.Deserialize<M3U8Response>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "[RemoteCompute] Response was not JSON");
            }
        }

        if (status is >= 200 and < 300)
        {
            if (!string.IsNullOrWhiteSpace(result?.Url) || result?.Streams is { Count: > 0 })
            {
                LastFault = null;
                return result;
            }

            return Fail(correlationId, "Local service", "The remote PC returned no playlist");
        }

        if (!string.IsNullOrWhiteSpace(egressProblem))
        {
            return Fail(correlationId, "This phone", egressProblem);
        }

        return Fail(correlationId, "Local service", RemoteComputeHttp.LocalServiceMessage(status, ErrorText(body)));
    }

    private M3U8Response? Fail(string correlationId, string component, string message)
    {
        var fault = new RemoteComputeFault(component, message, correlationId);
        LastFault = fault;
        logger.LogWarning("[RemoteCompute] {Display}", fault.Display);
        Faulted?.Invoke(fault);
        return null;
    }

    private static string? ErrorText(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                var text = error.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static Uri RelayUri(string apiBaseUrl, string correlationId)
    {
        var baseUri = new Uri(apiBaseUrl.TrimEnd('/') + "/");
        return new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/compute/relay",
            Query = "role=guest&cid=" + Uri.EscapeDataString(correlationId)
        }.Uri;
    }

    private sealed class DirectConnectionDroppedException : Exception;

    private sealed class GuestSession(
        ClientWebSocket socket,
        IHostNameResolver resolver,
        IDnsOverHttpsClient doh,
        bool dohEnabled,
        ILogger logger,
        string correlationId)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<(int Status, string Body)>> _pending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly object _directGate = new();
        private readonly List<byte[]> _earlyDirect = new();
        private readonly List<(string Candidate, string? SdpMid, ushort Line)> _earlyIce = new();
        private WebRtcComputePeer? _peer;
        private volatile string? _transportMode;
        private volatile bool _useDirect;
        private int _announcedReady;
        private int _resultSeen;
        private int _failedSent;
        private int _dropArmed;
        private int _didNotOpenLogged;
        private CancellationTokenSource? _dropGrace;

        public string? EgressProblem { get; private set; }

        public bool DirectDropped { get; private set; }

        public void Stop() => _stop.Cancel();

        private void NoteEgress(string problem)
        {
            EgressProblem ??= problem;
        }

        public Task<(int Status, string Body)> Arm(string id)
        {
            var pending = new TaskCompletionSource<(int Status, string Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = pending;
            return pending.Task;
        }

        public async Task ReceiveAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            var buffer = new byte[RelayWire.MaxPayload + 64];
            try
            {
                while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var bytes = message.ToArray();
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        if (!_useDirect
                            && RelayWire.TryDecode(bytes, out var streamId, out var payload)
                            && _clients.TryGetValue(streamId, out var tcp))
                        {
                            try
                            {
                                await tcp.GetStream().WriteAsync(payload, linked.Token).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "[RemoteCompute] Write to {StreamId} failed", streamId);
                            }
                        }

                        continue;
                    }

                    HandleText(Encoding.UTF8.GetString(bytes), linked.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // ended
            }
            finally
            {
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetCanceled();
                }

                foreach (var client in _clients.Values)
                {
                    client.Dispose();
                }

                var peer = _peer;
                _peer = null;
                if (peer is not null)
                {
                    try
                    {
                        peer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[RemoteCompute] Direct channel dispose failed");
                    }
                }
            }
        }

        public Task SendAsync(object payload, CancellationToken cancellationToken) =>
            SendRawAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), WebSocketMessageType.Text, cancellationToken);

        private void HandleText(string json, CancellationToken cancellationToken, bool fromSocket = true)
        {
            JsonElement frame;
            try
            {
                using var doc = JsonDocument.Parse(json);
                frame = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return;
            }

            var kind = frame.TryGetProperty("t", out var kindEl) ? kindEl.GetString() : null;
            if (kind == "rpc-result")
            {
                Interlocked.Exchange(ref _resultSeen, 1);
                _dropGrace?.Cancel();
                var id = frame.TryGetProperty("id", out var idEl) ? idEl.ToString().Trim('"') : "";
                var status = frame.TryGetProperty("status", out var statusEl) ? statusEl.GetInt32() : 500;
                var body = frame.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
                if (_pending.TryGetValue(id, out var pending))
                {
                    pending.TrySetResult((status, body));
                }

                return;
            }

            if (kind == "signal")
            {
                _ = HandleSignalAsync(frame);
                return;
            }

            if (kind == "transport")
            {
                ApplyTransport(frame.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : null, cancellationToken);
                return;
            }

            if ((kind == "open" || kind == "close") && fromSocket && _useDirect)
            {
                return;
            }

            if (kind == "open" && frame.TryGetProperty("id", out var openId) && openId.TryGetInt32(out var streamId))
            {
                var host = frame.TryGetProperty("host", out var hostEl) ? hostEl.GetString() ?? "" : "";
                var port = frame.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
                _ = OpenAsync(streamId, host, port, cancellationToken);
            }
            else if (kind == "close" && frame.TryGetProperty("id", out var closeId) && closeId.TryGetInt32(out var closeStream)
                     && _clients.TryRemove(closeStream, out var closing))
            {
                closing.Dispose();
            }
        }

        private async Task OpenAsync(int streamId, string host, int port, CancellationToken cancellationToken)
        {
            if (port is not (80 or 443) || string.IsNullOrWhiteSpace(host))
            {
                logger.LogInformation("[RemoteCompute] {CorrelationId} open-failed reason={Reason}", correlationId, "port not allowed");
                NoteEgress("Could not connect");
                await SendEgressAsync(new { t = "open-failed", id = streamId, cid = correlationId, host, port, error = "port not allowed" }, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var lookup = await PhonePathDns.ResolveAsync(
                    host,
                    dohEnabled,
                    doh.ResolveAsync,
                    resolver.ResolveAsync,
                    cancellationToken).ConfigureAwait(false);
                TcpClient? connected = null;
                foreach (var address in lookup.Addresses)
                {
                    var tcp = new TcpClient();
                    try
                    {
                        await tcp.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                        connected = tcp;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        tcp.Dispose();
                        logger.LogDebug(ex, "[RemoteCompute] Connect failed reason={Reason}", "connect failed");
                    }
                }

                if (connected is null)
                {
                    var dnsMiss = lookup.Addresses.Length == 0;
                    var error = dnsMiss ? "dns" : "connect failed";
                    logger.LogInformation(
                        "[RemoteCompute] {CorrelationId} open-failed reason={Reason} dns={Dns}",
                        correlationId,
                        error,
                        lookup.Via);
                    NoteEgress("Could not connect");
                    await SendEgressAsync(new { t = "open-failed", id = streamId, cid = correlationId, host, port, error }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                logger.LogInformation(
                    "[RemoteCompute] {CorrelationId} opened dns={Dns}",
                    correlationId,
                    lookup.Via);
                _clients[streamId] = connected;
                await SendEgressAsync(new { t = "opened", id = streamId, cid = correlationId, host, port }, cancellationToken).ConfigureAwait(false);
                _ = PumpAsync(streamId, connected, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "[RemoteCompute] Could not open reason={Reason}", "connect failed");
                NoteEgress("Could not connect");
                await SendEgressAsync(new { t = "open-failed", id = streamId, cid = correlationId, host, port, error = "connect failed" }, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task PumpAsync(int streamId, TcpClient client, CancellationToken cancellationToken)
        {
            var buffer = new byte[RelayWire.MaxPayload];
            try
            {
                var stream = client.GetStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await SendEgressBytesAsync(RelayWire.Encode(streamId, buffer.AsSpan(0, read)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "[RemoteCompute] Upstream read ended for {StreamId}", streamId);
            }
            finally
            {
                if (_clients.TryRemove(streamId, out var removed))
                {
                    removed.Dispose();
                }

                try
                {
                    await SendEgressAsync(new { t = "close", id = streamId, cid = correlationId }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // socket already closed
                }
            }
        }

        private async Task SendRawAsync(byte[] payload, WebSocketMessageType type, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(payload, type, true, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _send.Release();
            }
        }

        private void ApplyTransport(string? mode, CancellationToken cancellationToken)
        {
            List<byte[]> replay;
            lock (_directGate)
            {
                _transportMode = mode;
                if (mode == "webrtc")
                {
                    _useDirect = true;
                    replay = _earlyDirect.ToList();
                    _earlyDirect.Clear();
                }
                else
                {
                    _useDirect = false;
                    _earlyDirect.Clear();
                    return;
                }
            }

            foreach (var data in replay)
            {
                DispatchDirect(data, cancellationToken);
            }
        }

        private async Task HandleSignalAsync(JsonElement frame)
        {
            var kind = frame.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
            if (kind == "ice")
            {
                var candidate = frame.TryGetProperty("candidate", out var candidateEl) ? candidateEl.GetString() ?? "" : "";
                var mid = frame.TryGetProperty("sdpMid", out var midEl) ? midEl.GetString() : null;
                ushort line = 0;
                if (frame.TryGetProperty("sdpMLineIndex", out var lineEl)
                    && lineEl.TryGetInt32(out var lineNo)
                    && lineNo is >= 0 and <= ushort.MaxValue)
                {
                    line = (ushort)lineNo;
                }

                WebRtcComputePeer? peer;
                lock (_directGate)
                {
                    peer = _peer;
                    if (peer is null)
                    {
                        _earlyIce.Add((candidate, mid, line));
                        return;
                    }
                }

                await peer.AddIceCandidateAsync(candidate, mid, line).ConfigureAwait(false);
                return;
            }

            if (kind != "offer")
            {
                return;
            }

            var sdp = frame.TryGetProperty("sdp", out var sdpEl) ? sdpEl.GetString() ?? "" : "";
            await StartAnswerAsync(sdp).ConfigureAwait(false);
        }

        private async Task StartAnswerAsync(string sdp)
        {
            WebRtcComputePeer peer;
            List<(string Candidate, string? SdpMid, ushort Line)> queued;
            lock (_directGate)
            {
                if (_peer is not null)
                {
                    return;
                }

                peer = WebRtcComputePeer.CreateAnswerer();
                _peer = peer;
                queued = _earlyIce.ToList();
                _earlyIce.Clear();
            }

            peer.Diagnostic += line => logger.LogInformation("[RemoteCompute] {CorrelationId} direct: {Line}", correlationId, line);
            peer.IceCandidate += candidate =>
            {
                _ = SendAsync(new
                {
                    t = "signal",
                    kind = "ice",
                    cid = correlationId,
                    candidate = candidate.candidate,
                    sdpMid = candidate.sdpMid,
                    sdpMLineIndex = candidate.sdpMLineIndex
                }, CancellationToken.None);
            };
            peer.Received += data => OnDirect(data, _stop.Token);
            peer.Opened += AnnounceReady;
            peer.Closed += OnPeerClosed;
            peer.ConnectionLost += OnPeerClosed;
            _ = WatchUntilOpenAsync(peer);

            foreach (var ice in queued)
            {
                await peer.AddIceCandidateAsync(ice.Candidate, ice.SdpMid, ice.Line).ConfigureAwait(false);
            }

            var answer = await peer.AcceptOfferAsync(sdp).ConfigureAwait(false);
            await SendAsync(new { t = "signal", kind = "answer", cid = correlationId, sdp = answer }, CancellationToken.None)
                .ConfigureAwait(false);
        }

        private void AnnounceReady()
        {
            logger.LogInformation("[RemoteCompute] {CorrelationId} direct channel open: {Summary}", correlationId, CurrentPeerSummary());
            if (_transportMode == "relay" || Volatile.Read(ref _failedSent) == 1)
            {
                return;
            }

            if (Interlocked.Exchange(ref _announcedReady, 1) != 0)
            {
                return;
            }

            _ = SendAsync(new { t = "signal", kind = "ready", cid = correlationId }, CancellationToken.None);
        }

        private async Task WatchUntilOpenAsync(WebRtcComputePeer peer)
        {
            try
            {
                await peer.WaitUntilOpenAsync(DirectOpenBudget, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!_stop.IsCancellationRequested)
                {
                    LogDirectDidNotOpen();
                }

                OnIceLostBeforeReady();
            }
        }

        private void OnIceLostBeforeReady()
        {
            if (_stop.IsCancellationRequested || Volatile.Read(ref _announcedReady) != 0)
            {
                return;
            }

            LogDirectDidNotOpen();
            ApplyClose(DirectChannelClose.Decide(false, false, Volatile.Read(ref _resultSeen) == 1, correlationId));
        }

        private void LogDirectDidNotOpen()
        {
            if (Interlocked.Exchange(ref _didNotOpenLogged, 1) != 0)
            {
                return;
            }

            logger.LogInformation("[RemoteCompute] {CorrelationId} direct channel did not open: {Summary}", correlationId, CurrentPeerSummary());
        }

        private string CurrentPeerSummary()
        {
            WebRtcComputePeer? peer;
            lock (_directGate)
            {
                peer = _peer;
            }

            return peer?.Summary ?? "no peer";
        }

        private void OnPeerClosed()
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            ApplyClose(DirectChannelClose.Decide(
                Volatile.Read(ref _announcedReady) != 0,
                _useDirect,
                Volatile.Read(ref _resultSeen) == 1,
                correlationId));
        }

        private void ApplyClose(DirectChannelClose.Decision decision)
        {
            if (decision.SignalFailed)
            {
                if (Interlocked.Exchange(ref _failedSent, 1) != 0)
                {
                    return;
                }

                _ = SendAsync(new { t = "signal", kind = "failed", cid = correlationId, reason = "ice" }, CancellationToken.None);
                return;
            }

            if (decision.Fault is null || Interlocked.Exchange(ref _dropArmed, 1) != 0)
            {
                return;
            }

            var grace = new CancellationTokenSource();
            _dropGrace = grace;
            _ = FaultIfStillOpenAsync(grace.Token);
        }

        private async Task FaultIfStillOpenAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Volatile.Read(ref _resultSeen) == 1 || _stop.IsCancellationRequested || !_useDirect)
            {
                return;
            }

            DirectDropped = true;
            var error = new DirectConnectionDroppedException();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(error);
            }
        }

        private void OnDirect(byte[] data, CancellationToken cancellationToken)
        {
            lock (_directGate)
            {
                if (_transportMode == "relay")
                {
                    return;
                }

                if (_transportMode != "webrtc")
                {
                    _earlyDirect.Add(data);
                    return;
                }
            }

            DispatchDirect(data, cancellationToken);
        }

        private void DispatchDirect(byte[] data, CancellationToken cancellationToken)
        {
            if (data.Length == 0)
            {
                return;
            }

            if (data[0] == (byte)'{')
            {
                HandleText(Encoding.UTF8.GetString(data), cancellationToken, fromSocket: false);
                return;
            }

            if (!RelayWire.TryDecode(data, out var streamId, out var payload) || !_clients.TryGetValue(streamId, out var tcp))
            {
                return;
            }

            try
            {
                tcp.GetStream().Write(payload, 0, payload.Length);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[RemoteCompute] Write to {StreamId} failed", streamId);
            }
        }

        private Task SendEgressAsync(object payload, CancellationToken cancellationToken) =>
            SendEgressBytesAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), cancellationToken);

        private async Task SendEgressBytesAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var peer = _useDirect ? _peer : null;
            if (peer is not null)
            {
                await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    peer.Send(payload);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogInformation(ex, "[RemoteCompute] Direct channel send failed");
                    OnPeerClosed();
                }
                finally
                {
                    _send.Release();
                }

                return;
            }

            var text = payload.Length > 0 && payload[0] == (byte)'{';
            await SendRawAsync(payload, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal static class RelayWire
{
    public const int MaxPayload = 32 * 1024;

    public static byte[] Encode(int streamId, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[5 + payload.Length];
        buffer[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1), (uint)streamId);
        payload.CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out int streamId, out byte[] payload)
    {
        streamId = 0;
        payload = [];
        if (frame.Length < 5 || frame[0] != 1)
        {
            return false;
        }

        streamId = (int)BinaryPrimitives.ReadUInt32BigEndian(frame[1..]);
        payload = frame[5..].ToArray();
        return true;
    }
}
