using SIPSorcery.Net;

namespace VardyParty.Hosting;

/// <summary>
/// One WebRTC data channel for a compute resolve. STUN only. The Durable
/// Object carries the offer, answer, and ICE candidates, then this channel
/// carries the CONNECT bytes when it opens.
/// </summary>
internal sealed class WebRtcComputePeer : IAsyncDisposable
{
    public const string StunUrl = "stun:stun.l.google.com:19302";

    private readonly RTCPeerConnection _pc;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RTCIceCandidateInit> _earlyIce = new();
    private readonly object _iceGate = new();
    private RTCDataChannel? _channel;
    private bool _remoteDescriptionSet;
    private int _closed;
    private int _closeNotified;

    private WebRtcComputePeer(bool useStun)
    {
        _pc = useStun
            ? new RTCPeerConnection(new RTCConfiguration
            {
                iceServers = new List<RTCIceServer> { new() { urls = StunUrl } }
            })
            : new RTCPeerConnection();
        _pc.onicecandidate += candidate =>
        {
            if (string.IsNullOrWhiteSpace(candidate?.candidate))
            {
                return;
            }

            IceCandidate?.Invoke(new RTCIceCandidateInit
            {
                candidate = candidate.candidate,
                sdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex
            });
        };
        _pc.oniceconnectionstatechange += state => NotifyConnectionLost(state is RTCIceConnectionState.failed or RTCIceConnectionState.disconnected);
        _pc.onconnectionstatechange += state => NotifyConnectionLost(state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected);
    }

    public event Action<RTCIceCandidateInit>? IceCandidate;

    public event Action<byte[]>? Received;

    public event Action? Opened;

    public event Action? Closed;

    /// <summary>
    /// ICE or the peer connection reached <c>failed</c> or <c>disconnected</c>
    /// without the data channel having to close.
    /// </summary>
    public event Action? ConnectionLost;

    public static async Task<WebRtcComputePeer> CreateOffererAsync(bool useStun = true)
    {
        var peer = new WebRtcComputePeer(useStun);
        peer._channel = await peer._pc.createDataChannel("compute").ConfigureAwait(false);
        peer.Wire(peer._channel);
        return peer;
    }

    public static WebRtcComputePeer CreateAnswerer(bool useStun = true)
    {
        var peer = new WebRtcComputePeer(useStun);
        peer._pc.ondatachannel += channel =>
        {
            peer._channel = channel;
            peer.Wire(channel);
        };
        return peer;
    }

    public async Task<string> CreateOfferAsync()
    {
        var offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer).ConfigureAwait(false);
        return offer.sdp ?? "";
    }

    public async Task<string> AcceptOfferAsync(string sdp)
    {
        await SetRemoteAsync(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp }).ConfigureAwait(false);
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        return answer.sdp ?? "";
    }

    public Task AcceptAnswerAsync(string sdp) =>
        SetRemoteAsync(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });

    public async Task AddIceCandidateAsync(RTCIceCandidateInit candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.candidate))
        {
            return;
        }

        lock (_iceGate)
        {
            if (!_remoteDescriptionSet)
            {
                _earlyIce.Add(candidate);
                return;
            }
        }

        _pc.addIceCandidate(candidate);
        await Task.CompletedTask;
    }

    public Task AddIceCandidateAsync(string candidate, string? sdpMid, ushort sdpMLineIndex) =>
        AddIceCandidateAsync(new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = sdpMLineIndex
        });

    public async Task WaitUntilOpenAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        await _opened.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    public void Send(byte[] data)
    {
        var channel = _channel ?? throw new InvalidOperationException("Data channel is not open.");
        channel.send(data);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            _opened.TrySetCanceled();
            return;
        }

        _opened.TrySetCanceled();
        try
        {
            _channel?.close();
        }
        catch
        {
            // already closed
        }

        try
        {
            _pc.Close("dispose");
        }
        catch
        {
            // already closed
        }

        await Task.CompletedTask;
    }

    private void Wire(RTCDataChannel channel)
    {
        channel.onopen += NotifyOpened;
        if (channel.readyState == RTCDataChannelState.open)
        {
            NotifyOpened();
        }
        channel.onmessage += (_, _, data) =>
        {
            if (data is { Length: > 0 })
            {
                Received?.Invoke(data);
            }
        };
        channel.onclose += () =>
        {
            _opened.TrySetCanceled();
            if (Volatile.Read(ref _closed) == 0 && Interlocked.Exchange(ref _closeNotified, 1) == 0)
            {
                Closed?.Invoke();
            }
        };
    }

    private void NotifyOpened()
    {
        if (_opened.TrySetResult())
        {
            Opened?.Invoke();
        }
    }

    private void NotifyConnectionLost(bool lost)
    {
        if (!lost || Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        ConnectionLost?.Invoke();
    }

    private async Task SetRemoteAsync(RTCSessionDescriptionInit description)
    {
        var result = _pc.setRemoteDescription(description);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Remote description was rejected ({result}).");
        }

        List<RTCIceCandidateInit> queued;
        lock (_iceGate)
        {
            _remoteDescriptionSet = true;
            queued = _earlyIce.ToList();
            _earlyIce.Clear();
        }

        foreach (var candidate in queued)
        {
            _pc.addIceCandidate(candidate);
        }

        await Task.CompletedTask;
    }
}
