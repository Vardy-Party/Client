using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Auth;
using VardyParty.Hosting;
using VardyParty.Kernel;
using VardyParty.Ports;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class RemoteComputeRelaySocketTests
{
    private const string Page = "https://page.example.test/match/1";
    private const string OtherPage = "https://page.example.test/match/2";
    private const string Playlist = "https://stream.example.test/a.m3u8";

    [Fact]
    public async Task ResolveAsync_WhileMpHarvestIsOpen_DoesNotOpenASecondSocket()
    {
        // Arrange
        var connects = 0;
        ScriptedGuestSocket? harvest = null;
        var sut = CreateClient((authorization, relay, cancellationToken) =>
        {
            var call = Interlocked.Increment(ref connects);
            if (call == 1)
            {
                harvest = new ScriptedGuestSocket { OnText = ReplyWithChip };
                return Task.FromResult<WebSocket>(harvest);
            }

            return Task.FromResult<WebSocket>(new ScriptedGuestSocket { ThrowOnSend = true });
        });
        var first = await sut.ResolveAsync(useMp: true, Page, playerStreamName: null, CancellationToken.None);

        // Act
        var second = sut.ResolveAsync(useMp: true, OtherPage, playerStreamName: null, CancellationToken.None);

        // Assert
        Assert.Equal(Playlist, first?.Url);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, connects);
        harvest!.PushDone();
        var missed = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(missed);
        Assert.Equal(2, connects);
        Assert.False(sut.CanReuseHarvest(OtherPage));
    }

    [Fact]
    public async Task ResolveAsync_FailedSend_ClearsHarvestReuseAndReleasesTheGate()
    {
        // Arrange
        var connects = 0;
        var sut = CreateClient((authorization, relay, cancellationToken) =>
        {
            Interlocked.Increment(ref connects);
            return Task.FromResult<WebSocket>(new ScriptedGuestSocket { ThrowOnSend = true });
        });

        // Act
        var first = await sut.ResolveAsync(useMp: true, Page, playerStreamName: null, CancellationToken.None);

        // Assert
        Assert.Null(first);
        Assert.False(sut.CanReuseHarvest(Page));
        var second = await sut.ResolveAsync(useMp: true, Page, playerStreamName: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(second);
        Assert.Equal(2, connects);
    }

    [Fact]
    public async Task ResolveAsync_FaultedReceive_ReleasesTheGate()
    {
        // Arrange
        var connects = 0;
        var sut = CreateClient((authorization, relay, cancellationToken) =>
        {
            Interlocked.Increment(ref connects);
            return Task.FromResult<WebSocket>(new ScriptedGuestSocket { FailReceiveAfterSend = true });
        });

        // Act
        var first = await sut.ResolveAsync(useMp: false, Playlist, playerStreamName: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Null(first);
        var second = await sut.ResolveAsync(useMp: false, Playlist, playerStreamName: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(second);
        Assert.Equal(2, connects);
    }

    [Fact]
    public async Task ResolveAsync_CancelledAfterAcquire_ReleasesTheGate()
    {
        // Arrange
        var connects = 0;
        var sut = CreateClient((authorization, relay, cancellationToken) =>
        {
            var call = Interlocked.Increment(ref connects);
            if (call == 1)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var socket = new ScriptedGuestSocket { OnText = ReplyWithPlaylist };
            return Task.FromResult<WebSocket>(socket);
        });

        // Act
        var cancelled = await Record.ExceptionAsync(() =>
            sut.ResolveAsync(useMp: false, Playlist, playerStreamName: null, CancellationToken.None));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
        var second = await sut.ResolveAsync(useMp: false, Playlist, playerStreamName: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Playlist, second?.Url);
        Assert.Equal(2, connects);
    }

    private static RemoteComputePlayClient CreateClient(GuestSocketConnect connect)
    {
        var client = new RemoteComputePlayClient(
            new Prefs(),
            new Tokens(),
            Options.Create(new APISettings { HeadlessBaseUrl = "https://api.example.test" }),
            new Hosts(),
            new DnsPrefs(),
            new Doh(),
            NullLogger<RemoteComputePlayClient>.Instance);
        client.UseGuestSockets(connect);
        return client;
    }

    private static void ReplyWithChip(ScriptedGuestSocket socket, string json)
    {
        if (!TryReadRpcId(json, out var id))
        {
            return;
        }

        // Seed WaitForAsync(null) completes only after probe-ready rewritten segments
        // (playlist-only ReleaseChipThenSoak must not finish the harvest early).
        var line = JsonSerializer.Serialize(new
        {
            type = "chip",
            label = "Chip A",
            url = Playlist,
            rewrittenSegments = new[] { "https://cdn.example.test/a.exe?_s2=1" }
        });
        socket.PushText(JsonSerializer.Serialize(new { t = "rpc-part", id, body = line }));
    }

    private static void ReplyWithPlaylist(ScriptedGuestSocket socket, string json)
    {
        if (!TryReadRpcId(json, out var id))
        {
            return;
        }

        var playlist = JsonSerializer.Serialize(new { url = Playlist });
        socket.PushText(JsonSerializer.Serialize(new { t = "rpc-result", id, status = 200, body = playlist }));
    }

    private static bool TryReadRpcId(string json, out string id)
    {
        id = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("t", out var kind) || kind.GetString() != "rpc")
            {
                return false;
            }

            id = doc.RootElement.GetProperty("id").GetString() ?? "";
            return id.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class ScriptedGuestSocket : WebSocket
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource _closed = new();
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public Action<ScriptedGuestSocket, string>? OnText { get; init; }

        public bool ThrowOnSend { get; init; }

        public bool FailReceiveAfterSend { get; init; }

        public string? RpcId { get; private set; }

        public void PushText(string json) => _inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

        public void PushDone()
        {
            var line = JsonSerializer.Serialize(new { type = "done" });
            PushText(JsonSerializer.Serialize(new { t = "rpc-part", id = RpcId, body = line }));
        }

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override void Abort() => Dispose();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            Dispose();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            Dispose();
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _closed.Cancel();
            _sent.TrySetCanceled();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (FailReceiveAfterSend)
            {
                await _sent.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            var bytes = await _inbound.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            bytes.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, endOfMessage: true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (ThrowOnSend)
            {
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }

            if (messageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer);
                if (TryReadRpcId(json, out var id))
                {
                    RpcId = id;
                }

                OnText?.Invoke(this, json);
            }

            if (FailReceiveAfterSend)
            {
                _sent.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Tokens : IAuthTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false) =>
            Task.FromResult<string?>("token");

        public Task<bool> IsAuthenticatedAsync() => Task.FromResult(true);

        public Task LogoutAsync() => Task.CompletedTask;
    }

    private sealed class Prefs : IRemoteComputePreferences
    {
        public bool LoadShareEnabled() => false;

        public void SaveShareEnabled(bool enabled)
        {
        }

        public string LoadInviteCode() => "";

        public void SaveInviteCode(string code)
        {
        }

        public long LoadInviteExpiresAt() => 0;

        public void SaveInviteExpiresAt(long unixMilliseconds)
        {
        }

        public string LoadPairedHostSub() => "";

        public void SavePairedHostSub(string hostSub)
        {
        }
    }

    private sealed class DnsPrefs : IDnsPreferencesStore
    {
        public bool LoadDnsOverHttpsFallbackEnabled() => false;

        public void SaveDnsOverHttpsFallbackEnabled(bool enabled)
        {
        }
    }

    private sealed class Hosts : IHostNameResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<IPAddress>());
    }

    private sealed class Doh : IDnsOverHttpsClient
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<IPAddress>());
    }
}
