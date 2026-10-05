namespace VardyParty.Ports;

/// <summary>
/// Share-compute and paired-host settings. The invite code is not a credential;
/// Auth0 still gates the relay.
/// </summary>
public interface IRemoteComputePreferences
{
    bool LoadShareEnabled();

    void SaveShareEnabled(bool enabled);

    string LoadInviteCode();

    void SaveInviteCode(string code);

    /// <summary>Unix milliseconds when the displayed invite stops being valid. Zero when unknown.</summary>
    long LoadInviteExpiresAt();

    void SaveInviteExpiresAt(long unixMilliseconds);

    /// <summary>Auth0 <c>sub</c> of the host this device redeemed. Empty when unpaired.</summary>
    string LoadPairedHostSub();

    void SavePairedHostSub(string hostSub);
}
