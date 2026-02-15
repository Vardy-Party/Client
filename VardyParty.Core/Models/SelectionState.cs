namespace VardyParty.Models;

public class SelectionState
{
    public string LastLeague { get; set; } = string.Empty;
    public string LastHomeTeam { get; set; } = string.Empty;
    public string LastAwayTeam { get; set; } = string.Empty;
    public string LastStreamUrl { get; set; } = string.Empty;
    public string LastRoute { get; set; } = string.Empty;
    public string PreviousRoute { get; set; } = string.Empty;
    public Game? CurrentGame { get; set; }
}
