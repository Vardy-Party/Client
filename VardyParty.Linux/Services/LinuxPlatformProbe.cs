using System;
using System.Collections.Generic;
using System.IO;

namespace VardyParty.Linux.Services;

/// <summary>
/// Environment probes and libvlc option policy for the Linux/desktop head.
///
/// WSL matters to playback: WSLg's compositor and GPU paths have wedged
/// libvlc in the field (a stuck hardware-decode/vout probe froze the whole
/// app), so conservative options are the WSL default — software decode,
/// plain X11 vout, no hardware probing. The same set is forced by
/// <c>VARDYPARTY_LINUX_VLC_SAFE=1</c> and is also safe under xvfb.
///
/// Audio: SoundFlow (miniaudio) and libvlc share Pulse/ALSA. Leaving aout
/// unspecified lets VLC probe into a dummy output under WSLg (silent
/// video) or grab the sink in a way that kills the UI-sound device
/// permanently. Default is always <c>pulse</c> (WSLg Pulse and Ubuntu
/// PipeWire-as-Pulse). <c>any</c> is opt-in only — on PipeWire hosts it can
/// pick ALSA exclusive or a bad module and show as crackle / one-shot
/// silence. Never <c>--no-audio</c>. Override with
/// <c>VARDYPARTY_LINUX_VLC_AOUT=pulse|alsa|any</c>.
/// </summary>
public static class LinuxPlatformProbe
{
    /// <summary>
    /// True when running under WSL: /proc/version contains "microsoft"
    /// (case-insensitive; covers both WSL1 "Microsoft" and WSL2
    /// "microsoft-standard" kernels).
    /// </summary>
    public static bool IsWsl { get; } = DetectWsl();

    /// <summary>
    /// VARDYPARTY_LINUX_VLC_SAFE=1 forces the same conservative libvlc
    /// option set WSL gets, on any machine — a diagnostic/test hook (used by
    /// the headless xvfb verification, and handy when a desktop's VA-API/GL
    /// stack misbehaves).
    /// </summary>
    public static bool ForceSafeVlcOptions =>
        Environment.GetEnvironmentVariable("VARDYPARTY_LINUX_VLC_SAFE") == "1";

    /// <summary>
    /// Conservative libvlc options are the WSL default and the
    /// VARDYPARTY_LINUX_VLC_SAFE=1 override.
    /// </summary>
    public static bool UseConservativeVlcOptions => IsWsl || ForceSafeVlcOptions;

    /// <summary>Optional aout pin: pulse, alsa, or any. Other values ignored.</summary>
    public const string AudioOutputVariableName = "VARDYPARTY_LINUX_VLC_AOUT";

    public const string PulseAudioOutput = "pulse";
    public const string AlsaAudioOutput = "alsa";
    public const string AnyAudioOutput = "any";

    /// <summary>
    /// Picks the libvlc <c>--aout</c> module. An explicit env/override of
    /// pulse, alsa, or any wins; otherwise <c>pulse</c> on both WSL and
    /// native Ubuntu (PipeWire Pulse server). Dummy / no-audio are rejected.
    /// <paramref name="conservative"/> is retained for call-site symmetry
    /// with video options; it does not change the aout default.
    /// </summary>
    public static string ResolveAudioOutputModule(bool conservative, string? overrideModule = null)
    {
        _ = conservative;
        if (TryNormalizeAudioOutput(overrideModule, out var fromOverride))
        {
            return fromOverride;
        }

        return PulseAudioOutput;
    }

    /// <summary>
    /// Reads <see cref="AudioOutputVariableName"/> and resolves the aout
    /// module for the current process environment.
    /// </summary>
    public static string ResolveAudioOutputModule() =>
        ResolveAudioOutputModule(
            UseConservativeVlcOptions,
            Environment.GetEnvironmentVariable(AudioOutputVariableName));

    /// <summary>
    /// Full libvlc argv for a new <c>LibVLC</c> instance. Pure: no process
    /// I/O besides the optional aout override already resolved by the caller.
    /// </summary>
    public static string[] BuildLibVlcOptions(bool conservative, string? audioOutputModule = null)
    {
        var aout = ResolveAudioOutputModule(conservative, audioOutputModule);
        var vlcOptions = new List<string>
        {
            "--quiet",                       // Reduce verbose output
            "--no-video-title-show",         // Don't show video title on playback
            "--network-caching=3000",        // Align with per-media :network-caching (live HLS)
            "--live-caching=3000",           // Live/HLS underrun cushion (a/v crackle)
            "--http-reconnect",              // Auto-reconnect on network issues
            "--no-spdif",                    // Avoid passthrough / exclusive SPDIF
            $"--aout={aout}",
        };

        if (conservative)
        {
            vlcOptions.Add("--avcodec-hw=none"); // software decode, no VA-API/VDPAU probing
            vlcOptions.Add("--vout=x11");        // plain X11 output, no GL/compositor probing
            // Do not pin --demux=avformat: libavformat's HTTP stack ignores
            // :http-referrer, so hotlink CDNs 403 after HttpClient health passed.
        }
        else
        {
            vlcOptions.Add("--avcodec-hw=any"); // Prefer hardware decode on native Linux
        }

        return vlcOptions.ToArray();
    }

    /// <summary>
    /// Per-media LibVLC options: VLC http access gets referrer/UA; avformat
    /// gets the same as <c>headers</c> if it is selected as a fallback demuxer.
    /// Do not quote values — quoted Referer is sent literally and CDNs 403.
    /// </summary>
    public static IReadOnlyList<string> BuildPlaybackMediaOptions(
        bool conservative,
        string? referer,
        string userAgent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);

        var options = new List<string>
        {
            $":http-user-agent={userAgent}",
            conservative ? ":avcodec-hw=none" : ":avcodec-hw=any",
            ":network-caching=3000",
        };

        if (string.IsNullOrWhiteSpace(referer))
            return options;

        var trimmed = referer.Trim();
        options.Add($":http-referrer={trimmed}");
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var origin = uri.GetLeftPart(UriPartial.Authority);
            options.Add($":avformat-options=headers=Referer: {trimmed}\r\nOrigin: {origin}\r\nUser-Agent: {userAgent}");
        }

        return options;
    }

    /// <summary>
    /// Builds options for this process (WSL/safe probe + env aout override).
    /// </summary>
    public static string[] BuildLibVlcOptions() =>
        BuildLibVlcOptions(
            UseConservativeVlcOptions,
            Environment.GetEnvironmentVariable(AudioOutputVariableName));

    /// <summary>
    /// True when <paramref name="module"/> is a real aout we are willing to
    /// pin (pulse / alsa). <c>any</c> is a probe, not a SetAudioOutput name.
    /// </summary>
    public static bool IsPinnedAudioOutput(string? module) =>
        TryNormalizeAudioOutput(module, out var normalized) &&
        (normalized == PulseAudioOutput || normalized == AlsaAudioOutput);

    public static bool TryNormalizeAudioOutput(string? raw, out string module)
    {
        module = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (module is PulseAudioOutput or AlsaAudioOutput or AnyAudioOutput)
        {
            return true;
        }

        module = string.Empty;
        return false;
    }

    /// <summary>
    /// One-line host audio context for field logs (Pulse env + aout pin).
    /// Pure string formatting over already-resolved values / env reads.
    /// </summary>
    public static string DescribeAudioEnvironment(
        string? audioOutputModule = null,
        string? pulseServer = null,
        string? runtimeDir = null,
        bool? isWsl = null)
    {
        var aout = ResolveAudioOutputModule(
            UseConservativeVlcOptions,
            audioOutputModule ?? Environment.GetEnvironmentVariable(AudioOutputVariableName));
        pulseServer ??= Environment.GetEnvironmentVariable("PULSE_SERVER");
        runtimeDir ??= Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var wsl = isWsl ?? IsWsl;

        var pulse = string.IsNullOrWhiteSpace(pulseServer) ? "default" : pulseServer.Trim();
        var runtime = string.IsNullOrWhiteSpace(runtimeDir) ? "unset" : "set";
        return $"aout={aout}; wsl={wsl}; PULSE_SERVER={pulse}; XDG_RUNTIME_DIR={runtime}";
    }

    private static bool DetectWsl()
    {
        try
        {
            return File.Exists("/proc/version") &&
                   File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
