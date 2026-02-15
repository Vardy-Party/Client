namespace VardyParty.Models;

public class StreamSelectionProgress
{
    public int TotalStreams { get; set; }
    public int StreamsTested { get; set; }
    public int WorkingStreams { get; set; }
    public bool IsPaused { get; set; }
    public string Status { get; set; } = string.Empty;
}