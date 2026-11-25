namespace VardyParty.Streaming;

/// <summary>
/// One NDJSON line from a streaming <c>POST /mp</c>.
/// </summary>
public sealed class V2PlayEvent
{
    public const string TypeCaptchaCleared = "captcha-cleared";

    public const string ReasonCaptchaUnsolved = "captcha-unsolved";
    public const string ReasonCaptchaRunCap = "captcha-run-cap";
    public const string ReasonNoPlayer = "no-player";

    public string Type { get; set; } = "";

    public int? StatusCode { get; set; }

    /// <summary>Machine-readable fail-fast code from LocalService when <see cref="Type"/> is error.</summary>
    public string? Reason { get; set; }

    public List<string>? Streams { get; set; }

    public string? SelectedStream { get; set; }

    public string? Label { get; set; }

    public string? Url { get; set; }

    public Dictionary<string, string>? RequestHeaders { get; set; }

    public List<string>? RewrittenSegments { get; set; }

    public string? Error { get; set; }
}
