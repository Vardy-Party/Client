namespace VardyParty.Ports;

public sealed class InMemoryRemoteComputePreferences : IRemoteComputePreferences
{
    private bool _share;
    private string _invite = "";
    private long _inviteExpiresAt;
    private string _hostSub = "";

    public bool LoadShareEnabled() => _share;

    public void SaveShareEnabled(bool enabled) => _share = enabled;

    public string LoadInviteCode() => _invite;

    public void SaveInviteCode(string code) => _invite = code ?? "";

    public long LoadInviteExpiresAt() => _inviteExpiresAt;

    public void SaveInviteExpiresAt(long unixMilliseconds) => _inviteExpiresAt = unixMilliseconds;

    public string LoadPairedHostSub() => _hostSub;

    public void SavePairedHostSub(string hostSub) => _hostSub = hostSub ?? "";
}
