namespace VardyParty.Presentation;

/// <summary>
/// One precedence rule for the playing stream's bitrate, shared by the video-info overlay
/// and the health samples so both report the same number.
/// </summary>
/// <remarks>
/// <para>Sources, highest first:</para>
/// <list type="number">
/// <item><description>The <see cref="PlaybackTransferMeter"/> rate: bytes the player actually read.</description></item>
/// <item><description>The adaptive source's current download rate, which a custom download handler often leaves at 0.</description></item>
/// <item><description>The declared encoding bitrate, which HLS usually leaves unset.</description></item>
/// </list>
/// </remarks>
public static class MeasuredBitrate
{
    /// <summary>Bitrate in kilobits per second, or null when no source reports a positive rate.</summary>
    /// <param name="encodingKbps">Declared encoding-properties bitrate, in kilobits per second.</param>
    /// <param name="amsBitsPerSecond">Adaptive source current download rate, in bits per second.</param>
    /// <param name="meterKbps">Transfer meter rate, in kilobits per second.</param>
    public static int? ResolveKbps(int? encodingKbps, ulong amsBitsPerSecond, int? meterKbps)
    {
        if (meterKbps is > 0)
            return meterKbps;

        var amsKbps = amsBitsPerSecond / 1000;
        if (amsKbps > 0)
            return amsKbps > int.MaxValue ? int.MaxValue : (int)amsKbps;

        return encodingKbps is > 0 ? encodingKbps : null;
    }
}
