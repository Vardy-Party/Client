namespace VardyParty.Kernel;

public class StreamResolutionProgress
{
    public bool IsResolving { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalStreams { get; set; }
    public int StreamsTested { get; set; }
    public int HealthyStreams { get; set; }
}
