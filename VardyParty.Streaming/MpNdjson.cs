using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VardyParty.Kernel;

namespace VardyParty.Streaming;

public static class MpNdjson
{
    public const string ContentType = "application/x-ndjson";
    public const string RequestHeaderName = "X-Vardy-Mp-Stream";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool IsNdjson(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && contentType.Contains("ndjson", StringComparison.OrdinalIgnoreCase);

    public static MpPlayEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MpPlayEvent>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async IAsyncEnumerable<string> ReadLinesAsync(
        System.IO.Stream body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            yield return line;
        }
    }

    public static M3U8Response? ToResponse(MpPlayEvent ev, IReadOnlyList<string>? streams)
    {
        if (!string.Equals(ev.Type, "chip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new M3U8Response
        {
            Url = ev.Url ?? "",
            RequestHeaders = ev.RequestHeaders,
            RewrittenSegments = ev.RewrittenSegments,
            SelectedStream = ev.Label ?? ev.SelectedStream,
            Streams = streams is { Count: > 0 } ? streams.ToList() : ev.Streams
        };
    }
}
