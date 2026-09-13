using System;
using System.Collections.Generic;
using VardyParty.LocalService.Abstractions;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class TransportPluginPlaylistProcessorTests
{
    [Fact]
    public void Process_AppliesEachPluginInOrder()
    {
        var processor = new TransportPluginPlaylistProcessor(
        [
            new StubPlugin(text => text.Replace("A", "B", StringComparison.Ordinal)),
            new StubPlugin(text => text.Replace("B", "C", StringComparison.Ordinal))
        ]);

        Assert.Equal("#EXTM3U\nC", processor.Process("#EXTM3U\nA"));
    }

    [Fact]
    public void Process_Empty_ReturnsEmpty()
    {
        var processor = new TransportPluginPlaylistProcessor([new StubPlugin(_ => "nope")]);
        Assert.Equal("", processor.Process(""));
    }

    private sealed class StubPlugin(Func<string, string> rewrite) : IPlaybackTransportPlugin
    {
        public string StrategyId => "stub";
        public string LocalServiceEndpoint => "mp";
        public bool Matches(string? resolutionStrategy, string? source) => true;
        public string PostProcessPlaylist(string playlistText) => rewrite(playlistText);
    }
}
