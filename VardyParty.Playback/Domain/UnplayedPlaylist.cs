using System;
using System.Collections.Generic;
using System.Globalization;

namespace VardyParty.Playback;

/// <summary>
/// A live playlist reload repeats the segments still inside the window. Drop the
/// leading ones this viewing has already fetched so a prepare does not play them again.
/// The last segment is kept when the whole window was already fetched.
/// </summary>
public static class UnplayedPlaylist
{
    public readonly record struct Trim(string Text, int Dropped);

    /// <summary>Host and path. The signed query is not part of the segment.</summary>
    public static string SegmentIdentity(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url.Trim();
        return uri.Host + uri.AbsolutePath;
    }

    public static Trim TrimPlayedPrefix(string playlist, Func<string, bool> wasPlayed, string? playlistUrl = null)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(wasPlayed);

        var lines = playlist.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var uriAt = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsUriLine(lines[i]))
                uriAt.Add(i);
        }

        if (uriAt.Count == 0)
            return new Trim(playlist, 0);

        var firstBlock = BlockStart(lines, uriAt, 0);
        var keep = 0;
        for (; keep < uriAt.Count; keep++)
        {
            var absolute = Resolve(lines[uriAt[keep]].Trim(), playlistUrl);
            if (!wasPlayed(absolute))
                break;
        }

        if (keep == uriAt.Count)
            keep = uriAt.Count - 1;
        if (keep == 0)
            return new Trim(playlist, 0);

        var header = new List<string>(lines[..firstBlock]);
        BumpMediaSequence(header, keep);

        var output = new List<string>(header);
        for (var segment = keep; segment < uriAt.Count; segment++)
        {
            var start = BlockStart(lines, uriAt, segment);
            var end = uriAt[segment];
            for (var line = start; line <= end; line++)
                output.Add(lines[line]);
        }

        for (var line = uriAt[^1] + 1; line < lines.Length; line++)
            output.Add(lines[line]);

        return new Trim(string.Join('\n', output), keep);
    }

    private static int BlockStart(string[] lines, List<int> uriAt, int ordinal)
    {
        if (ordinal > 0)
            return uriAt[ordinal - 1] + 1;

        var i = uriAt[0] - 1;
        while (i >= 0 && IsSegmentTag(lines[i]))
            i--;
        return i + 1;
    }

    private static void BumpMediaSequence(List<string> header, int dropped)
    {
        for (var i = 0; i < header.Count; i++)
        {
            const string tag = "#EXT-X-MEDIA-SEQUENCE:";
            if (!header[i].StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                continue;
            var number = header[i][tag.Length..].Trim();
            if (!long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
                return;
            header[i] = tag + (sequence + dropped).ToString(CultureInfo.InvariantCulture);
            return;
        }

        var insertAt = 0;
        for (var i = 0; i < header.Count; i++)
        {
            if (header[i].StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                insertAt = i + 1;
                break;
            }
        }

        header.Insert(insertAt, "#EXT-X-MEDIA-SEQUENCE:" + dropped.ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsUriLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && !trimmed.StartsWith('#');
    }

    private static bool IsSegmentTag(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase);
    }

    private static string Resolve(string reference, string? playlistUrl)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out _))
            return reference;
        if (playlistUrl is not null
            && Uri.TryCreate(playlistUrl, UriKind.Absolute, out var basis)
            && Uri.TryCreate(basis, reference, out var absolute))
        {
            return absolute.ToString();
        }

        return reference;
    }
}
