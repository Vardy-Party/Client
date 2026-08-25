namespace VardyParty
{
    public interface IOverlayCloseService
    {
        event Action? CloseRequested;
        void ShowCloseControl();
        void HideCloseControl();
    }
}
