namespace VardyParty.Ports;

/// <summary>The six UI feedback sounds. See docs/architecture/homepage-maui-avalonia.md.</summary>
public enum UiSound
{
    /// <summary>Focus moved between cards or menu items (very quiet tick).</summary>
    FocusMove,

    /// <summary>Card or menu item confirmed.</summary>
    Select,

    /// <summary>Back / cancel, and menu close.</summary>
    Back,

    /// <summary>League/settings menu opened.</summary>
    MenuOpen,

    /// <summary>Stream-resolution (or similar) error surfaced.</summary>
    Error,

    /// <summary>A live game's score changed.</summary>
    Goal,
}
