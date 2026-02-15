namespace VardyParty.Services
{
    public static class SwitchingDecision
    {
        public static bool CanSwitch(string? currentUrl, string candidateUrl, bool isPreparing)
        {
            if (string.IsNullOrEmpty(candidateUrl)) return false;
            if (isPreparing) return false;
            if (!string.IsNullOrEmpty(currentUrl) && string.Equals(currentUrl, candidateUrl, System.StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
    }
}
