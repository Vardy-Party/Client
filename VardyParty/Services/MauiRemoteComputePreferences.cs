using Microsoft.Maui.Storage;
using VardyParty.Ports;

namespace VardyParty.MauiServices;

public sealed class MauiRemoteComputePreferences : IRemoteComputePreferences
{
    private const string ShareKey = "remote-compute.share";
    private const string InviteKey = "remote-compute.invite";
    private const string InviteExpiresKey = "remote-compute.invite-expires";
    private const string HostKey = "remote-compute.host-sub";

    public bool LoadShareEnabled() => Preferences.Get(ShareKey, false);

    public void SaveShareEnabled(bool enabled) => Preferences.Set(ShareKey, enabled);

    public string LoadInviteCode() => Preferences.Get(InviteKey, "");

    public void SaveInviteCode(string code) => Preferences.Set(InviteKey, code ?? "");

    public long LoadInviteExpiresAt() => Preferences.Get(InviteExpiresKey, 0L);

    public void SaveInviteExpiresAt(long unixMilliseconds) => Preferences.Set(InviteExpiresKey, unixMilliseconds);

    public string LoadPairedHostSub() => Preferences.Get(HostKey, "");

    public void SavePairedHostSub(string hostSub) => Preferences.Set(HostKey, hostSub ?? "");
}
