#if EMBEDDED_DESKTOP_VIDEO
using LibVLCSharp.Shared;

namespace VardyParty.Desktop.Controls;

/// <summary>
/// MAUI view that hosts libvlc's video output INSIDE the app window. Its
/// handler (<see cref="VideoHostViewHandler"/>, registered in MauiProgram)
/// realizes it as LibVLCSharp.Avalonia's <c>VideoView</c> — a
/// <c>NativeControlHost</c> that creates a native child window (an X window
/// on Linux) and hands its handle to the assigned <see cref="MediaPlayer"/>
/// as the drawable, so playback renders in-window instead of libvlc opening
/// its own toplevel.
///
/// Airspace caveat: the video is a NATIVE child window stacked above the
/// Avalonia-drawn scene, so MAUI content overlapping this view's bounds is
/// covered by the video. DesktopHomePage therefore reserves a chrome strip
/// (title / Close / toasts) that never overlaps the host.
///
/// The whole type compiles out with <c>-p:EmbeddedDesktopVideo=false</c>
/// (see VardyParty.Desktop.csproj).
/// </summary>
public sealed class VideoHostView : View
{
    public static readonly BindableProperty MediaPlayerProperty = BindableProperty.Create(
        nameof(MediaPlayer),
        typeof(MediaPlayer),
        typeof(VideoHostView));

    /// <summary>
    /// The libvlc MediaPlayer whose video output should render in this view.
    /// Assign BEFORE Play() so the drawable is set when the vout is created;
    /// set null to detach. Assignments must happen on the UI thread (they
    /// flow into the Avalonia control); the underlying drawable set/unset is
    /// a non-blocking libvlc setter, but never assign a player whose session
    /// has been abandoned as wedged (see DesktopVideoPlayerService — a wedged
    /// player can hold its object lock and stall the caller).
    /// </summary>
    public MediaPlayer? MediaPlayer
    {
        get => (MediaPlayer?)GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }
}
#endif
