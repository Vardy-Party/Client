namespace VardyParty.Presentation;

public interface IDesktopPendingUpdateStore
{
    AppReleaseVersion? Read();

    void Write(AppReleaseVersion expected);

    void Clear();
}
