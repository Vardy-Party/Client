using VardyParty.Presentation;

namespace VardyParty.HomeUi;

public sealed class MauiDesktopAppQuitter : IDesktopAppQuitter
{
    public void RequestQuit()
    {
        var app = Application.Current;
        if (app is not null)
        {
            app.Quit();
            return;
        }

        Environment.Exit(0);
    }
}
