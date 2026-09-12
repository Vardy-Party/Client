using System.Text;
using VardyParty.Catalog;
using VardyParty.Kernel;

namespace VardyParty.Linux.Services;

/// <summary>
/// Pure overlay copy for the Linux Avalonia chrome window. Hosts own widgets;
/// this owns video-info formatting and scores-ticker line building so unit
/// tests can cover it without Avalonia/MAUI.
/// </summary>
public static class LinuxPlaybackChromeInfoText
{
    public static string FormatVideoInfo(PlayerOverlayInfo info, string? playbackStatus = null)
    {
        var sb = new StringBuilder();
        var status = string.IsNullOrWhiteSpace(playbackStatus) ? "Playing" : playbackStatus.Trim();
        sb.AppendLine($"Status: {status}");

        if (info.Total > 0)
            sb.AppendLine($"Stream: {info.Index}/{info.Total}");

        if (!string.IsNullOrWhiteSpace(info.Channel))
            sb.AppendLine($"Channel: {info.Channel}");

        if (!string.IsNullOrWhiteSpace(info.Resolution))
            sb.AppendLine($"Resolution: {info.Resolution}");

        var aspect = info.AspectRatio ?? PlayerOverlayFormatter.BuildAspect(info.Resolution);
        if (!string.IsNullOrWhiteSpace(aspect))
            sb.AppendLine($"Aspect ratio: {aspect}");

        if (info.BitrateKbps is > 0)
            sb.AppendLine($"Bitrate: {info.BitrateKbps.Value} kbps");

        if (!string.IsNullOrWhiteSpace(info.VideoCodec))
            sb.AppendLine($"Video Codec: {info.VideoCodec}");

        if (!string.IsNullOrWhiteSpace(info.AudioCodec))
            sb.AppendLine($"Audio Codec: {info.AudioCodec}");

        if (info.BufferPercent is >= 0)
            sb.AppendLine($"Buffer: {info.BufferPercent.Value}%");

        if (!string.IsNullOrWhiteSpace(info.Title) &&
            !string.Equals(info.Title, info.Channel, StringComparison.Ordinal))
        {
            sb.AppendLine(info.Title);
        }

        var source = PlayerOverlayFormatter.StripQuery(info.M3u8Url);
        if (!string.IsNullOrWhiteSpace(source))
            sb.AppendLine($"Source: {source}");

        var referer = PlayerOverlayFormatter.RefererHost(info.RefererUrl);
        if (!string.IsNullOrWhiteSpace(referer))
            sb.AppendLine($"Referer: {referer}");

        return sb.ToString().TrimEnd();
    }

    public static PlayerOverlayInfo? BuildOverlayInfo(
        EnrichedStream? current,
        int index,
        int total,
        string? refererUrl,
        string? fallbackM3u8Url = null) =>
        PlayerOverlayFormatter.BuildOverlayInfo(current, index, total, refererUrl, fallbackM3u8Url);

    public static string FormatScoresTicker(
        IEnumerable<Game> games,
        ScoresTickerMode mode,
        string? watchedLeague)
    {
        var filtered = FilterGames(games, mode, watchedLeague).ToList();
        if (filtered.Count == 0)
        {
            return mode switch
            {
                ScoresTickerMode.SameLeagueInPlay => "No in-play scores in this league",
                ScoresTickerMode.AllLeaguesInPlay => "No in-play scores",
                ScoresTickerMode.AllFinished => "No finished scores",
                _ => "No upcoming fixtures"
            };
        }

        return string.Join("   •   ", filtered.Select(FormatGameScore));
    }

    public static IEnumerable<Game> FilterGames(
        IEnumerable<Game> games,
        ScoresTickerMode mode,
        string? watchedLeague)
    {
        bool IsSameLeague(Game g) => ScoresTickerPolicy.IsSameLeague(g, watchedLeague);

        return mode switch
        {
            ScoresTickerMode.SameLeagueInPlay => games.Where(g => IsSameLeague(g) && ScoresTickerPolicy.IsInPlay(g)),
            ScoresTickerMode.AllLeaguesInPlay => games.Where(ScoresTickerPolicy.IsInPlay),
            ScoresTickerMode.AllFinished => games.Where(ScoresTickerPolicy.IsFinishedWithScore),
            _ => games.Where(ScoresTickerPolicy.IsUpcoming)
        };
    }

    private static string FormatGameScore(Game game)
    {
        var home = string.IsNullOrWhiteSpace(game.DisplayHome) ? "?" : game.DisplayHome;
        var away = string.IsNullOrWhiteSpace(game.DisplayAway) ? "?" : game.DisplayAway;
        if (game.HomeScore.HasValue && game.AwayScore.HasValue)
            return $"{home} {game.HomeScore}-{game.AwayScore} {away}";
        return $"{home} v {away}";
    }
}
