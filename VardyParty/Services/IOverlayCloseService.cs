namespace VardyParty.Services
{
    public interface IOverlayCloseService
    {
    event Action? CloseRequested;
        void ShowCloseControl();
        void HideCloseControl();
    }
}
