#if EMBEDDED_DESKTOP_VIDEO
using Avalonia.Controls.Maui.Handlers;

namespace VardyParty.Desktop.Controls;

/// <summary>
/// Bridges <see cref="VideoHostView"/> to an Avalonia Image through the
/// MAUI-Avalonia backend's
/// <see cref="AvaloniaControlHandler{TVirtualView, TControl}"/>. LibVLC
/// software frames land on this Image so in-player chrome can overlay the
/// picture in the same scene graph (no native-child airspace).
///
/// Compiles out with -p:EmbeddedDesktopVideo=false.
/// </summary>
public sealed class VideoHostViewHandler : AvaloniaControlHandler<VideoHostView, Avalonia.Controls.Image>
{
    public VideoHostViewHandler()
    {
    }

    protected override void OnAvaloniaControlCreated(Avalonia.Controls.Image control)
    {
        control.Stretch = Avalonia.Media.Stretch.Uniform;
        VirtualView?.AttachPlatformImage(control);
        base.OnAvaloniaControlCreated(control);
    }

    /// <summary>
    /// Image has no LibVLC drawable to detach. Clearing the source is enough;
    /// a wedged libvlc pair is abandoned on the service side without touching
    /// this control.
    /// </summary>
    protected override void OnAvaloniaControlDestroying(Avalonia.Controls.Image? control)
    {
        VirtualView?.DetachPlatformImage();
        if (control != null)
        {
            control.Source = null;
        }

        base.OnAvaloniaControlDestroying(control);
    }
}
#endif
