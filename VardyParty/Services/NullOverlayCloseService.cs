namespace VardyParty;

public sealed class NullOverlayCloseService : IOverlayCloseService
{
    public event Action? CloseRequested
    {
        add { }
        remove { }
    }

    public void ShowCloseControl()
    {
    }

    public void HideCloseControl()
    {
    }
}
