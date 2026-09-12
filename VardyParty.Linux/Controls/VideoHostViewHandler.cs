#if EMBEDDED_LINUX_VIDEO
using Avalonia.Controls.Maui.Handlers;
using LibVLCSharp.Avalonia;

namespace VardyParty.Linux.Controls;

/// <summary>
/// Bridges <see cref="VideoHostView"/> to LibVLCSharp.Avalonia's
/// <see cref="VideoView"/> through the MAUI-Avalonia backend's generic
/// <see cref="AvaloniaControlHandler{TVirtualView, TControl}"/> (the
/// supported way to host a custom Avalonia control inside this head's MAUI
/// visual tree). Maps the MediaPlayer property; VideoView itself owns the
/// drawable attach/detach on its visual-tree/native-handle lifecycle
/// (OnAttachedToVisualTree / DestroyNativeControlCore).
///
/// Compiles out with -p:EmbeddedLinuxVideo=false.
/// </summary>
public sealed class VideoHostViewHandler : AvaloniaControlHandler<VideoHostView, VideoView>
{
    /// <summary>Chains the base view mapper and adds the MediaPlayer mapping.</summary>
    public static readonly IPropertyMapper<VideoHostView, VideoHostViewHandler> HostMapper =
        new PropertyMapper<VideoHostView, VideoHostViewHandler>(Mapper)
        {
            [nameof(VideoHostView.MediaPlayer)] = MapMediaPlayer,
        };

    public VideoHostViewHandler()
        : base(HostMapper)
    {
    }

    /// <summary>
    /// Deliberately does NOT clear <c>control.MediaPlayer</c>: VideoView's
    /// own native-handle teardown (DestroyNativeControlCore) already detaches
    /// the drawable, and an extra property write here would be a redundant
    /// libvlc setter on a player that may already be torn down. Hosts that
    /// abandon a wedged player never destroy the host view at all (they park
    /// it invisible — see LinuxHomePage) precisely so no detach path can
    /// touch the wedged instance.
    /// </summary>
    protected override void OnAvaloniaControlDestroying(VideoView? control)
    {
    }

    private static void MapMediaPlayer(VideoHostViewHandler handler, VideoHostView view)
    {
        if (handler.AvaloniaControl is { } videoView)
        {
            videoView.MediaPlayer = view.MediaPlayer;
        }
    }
}
#endif
