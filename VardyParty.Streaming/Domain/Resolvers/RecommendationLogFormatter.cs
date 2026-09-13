using System.Text;
using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// One-line recommendation dumps for console / logcat diagnosis.
/// Structured <c>{@Recommendations}</c> falls back to the type name in many hosts.
/// </summary>
public static class RecommendationLogFormatter
{
    public static string Format(RecommendationResponse? recommendations)
    {
        if (recommendations is null)
            return "(null)";

        var items = recommendations.Recommended;
        if (items is null || items.Count == 0)
        {
            return $"confidence={recommendations.Confidence}, hasData={recommendations.HasData}, count=0"
                + FormatGeneratedAt(recommendations.GeneratedAt);
        }

        var sb = new StringBuilder();
        sb.Append("confidence=").Append(recommendations.Confidence);
        sb.Append(", hasData=").Append(recommendations.HasData);
        sb.Append(", count=").Append(items.Count);
        sb.Append(FormatGeneratedAt(recommendations.GeneratedAt));
        sb.Append(": ");

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                sb.Append(" | ");

            var item = items[i];
            var name = string.IsNullOrWhiteSpace(item.StreamName) ? "(unnamed)" : item.StreamName.Trim();
            sb.Append('#').Append(i + 1)
                .Append(' ')
                .Append(name)
                .Append(" [")
                .Append(item.Confidence)
                .Append("] ")
                .Append(string.IsNullOrWhiteSpace(item.Url) ? "(no-url)" : item.Url.Trim());

            if (item.Meta is { } meta)
            {
                var bits = new List<string>(4);
                if (!string.IsNullOrWhiteSpace(meta.Resolution))
                    bits.Add(meta.Resolution!);
                if (meta.Framerate is int fps)
                    bits.Add($"{fps}fps");
                if (!string.IsNullOrWhiteSpace(meta.VideoCodec))
                    bits.Add(meta.VideoCodec!);
                if (meta.Bitrate is int br)
                    bits.Add($"{br}kbps");
                if (bits.Count > 0)
                    sb.Append(" (").Append(string.Join(", ", bits)).Append(')');
            }
        }

        return sb.ToString();
    }

    public static string FormatTestOrder(
        IReadOnlyList<int> testOrder,
        Func<int, string> labelForIndex)
    {
        if (testOrder.Count == 0)
            return "(empty)";

        var parts = new string[testOrder.Count];
        for (var i = 0; i < testOrder.Count; i++)
        {
            var index = testOrder[i];
            parts[i] = $"{index}:{labelForIndex(index)}";
        }

        return string.Join(", ", parts);
    }

    private static string FormatGeneratedAt(long? generatedAt) =>
        generatedAt is long ms ? $", generatedAt={ms}" : "";
}
