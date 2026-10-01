namespace VardyParty.HomeUi.Views;

/// <summary>
/// How to wait for a card after leaving a stream. A live view, even when it
/// is detached, can be posted: Android runs that from the view run queue
/// when the view attaches. A card that has not been created yet is not ready
/// until the rows list lays out. Posting on that already-attached list is a
/// looper message, and a chain of them spends the budget before the row binds.
/// </summary>
public static class StreamExitFocusRetry
{
    public enum Wait
    {
        PostOnCard,
        NextLayoutPass,
    }

    public static Wait Choose(bool cardViewExists) =>
        cardViewExists ? Wait.PostOnCard : Wait.NextLayoutPass;
}
