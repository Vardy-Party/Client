using System;
using System.Collections.Generic;
using System.IO;

namespace VardyParty.Linux.Services;

/// <summary>
/// Environment probes and libvlc option policy for the Linux/desktop head.
///
/// Product target is a real Ubuntu desktop (hardware decode, full-res
/// present, host FullScreen). WSL is a test host only: WSLg's compositor
/// and GPU have wedged libvlc in the field, so the conservative set
/// (software decode, no VA-API probe, heavier live cache, Pulse latency)
/// is the WSL default and must not leak onto native Linux. Standalone
/// fallback pins plain X11 vout; in-window compositing uses
/// <c>--vout=vmem</c> (never X11 on the same player). The conservative
/// set is also forced by <c>VARDYPARTY_LINUX_VLC_SAFE=1</c> (xvfb / a
/// broken desktop GPU) — that is an opt-in, not the Ubuntu default.
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
    /// WSL / safe-mode option set. False on a normal Ubuntu desktop.
    /// </summary>
    public static bool UseConservativeVlcOptions => IsWsl || ForceSafeVlcOptions;

    /// <summary>Optional aout pin: pulse, alsa, or any. Other values ignored.</summary>
    public const string AudioOutputVariableName = "VARDYPARTY_LINUX_VLC_AOUT";

    public const string PulseAudioOutput = "pulse";
    public const string AlsaAudioOutput = "alsa";
    public const string AnyAudioOutput = "any";

    /// <summary>
    /// Live HLS cache on a real desktop. VLC's 3s default plus a small
    /// cushion — do not use the WSL 12s value here (that is extra delay).
    /// </summary>
    public const int DesktopLiveNetworkCachingMs = 4_000;

    /// <summary>
    /// WSL-only: 3s plus software RV32 present late/flush/silence. Not
    /// applied on native Ubuntu.
    /// </summary>
    public const int WslLiveNetworkCachingMs = 12_000;

    /// <summary>
    /// Extra input-clock slack (µs). WSL-only: default 5ms makes Pulse/WSLg
    /// resample and flush. Not applied on native Ubuntu.
    /// </summary>
    public const int ClockJitterUs = 200_000;

    /// <summary>WSLg Pulse underruns below ~150ms; only set when unset.</summary>
    public const string PulseLatencyVariableName = "PULSE_LATENCY_MSEC";

    public const int PulseLatencyMsec = 300;

    /// <summary>
    /// WSL-only cap so Avalonia present does not starve Pulse. Native
    /// desktop presents at the decoded size.
    /// </summary>
    public const int WslMaxFrameWidth = 1280;

    /// <summary>WSL-only present floor (~25 fps). Native desktop is uncapped.</summary>
    public const int WslPresentIntervalMs = 40;

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
    /// <param name="callbackVout">
    /// True when frames are pulled via <c>SetVideoCallbacks</c> into Avalonia.
    /// Pins <c>--vout=vmem</c> and omits <c>--vout=x11</c> (X11 output and
    /// software callbacks cannot share one MediaPlayer).
    /// </param>
    public static string[] BuildLibVlcOptions(
        bool conservative,
        string? audioOutputModule = null,
        bool callbackVout = false)
    {
        var aout = ResolveAudioOutputModule(conservative, audioOutputModule);
        var cacheMs = ResolveLiveNetworkCachingMs(conservative);
        var vlcOptions = new List<string>
        {
            "--quiet",                       // Reduce verbose output
            "--no-video-title-show",         // Don't show video title on playback
            $"--network-caching={cacheMs}",
            $"--live-caching={cacheMs}",
            "--http-reconnect",              // Auto-reconnect on network issues
            "--no-spdif",                    // Avoid passthrough / exclusive SPDIF
            $"--aout={aout}",
        };

        if (callbackVout)
        {
            vlcOptions.Add("--vout=vmem");
        }

        if (conservative)
        {
            vlcOptions.Add("--avcodec-hw=none"); // software decode, no VA-API/VDPAU probing
            vlcOptions.Add("--avcodec-skiploopfilter=all");
            vlcOptions.Add($"--clock-jitter={ClockJitterUs}");
            vlcOptions.Add("--no-audio-time-stretch");
            vlcOptions.Add("--drop-late-frames");
            vlcOptions.Add("--skip-frames");
            // Do not pass --ipv4 or --pulse-latency: this LibVLC aborts
            // construction ("unknown option"). IPv4 is the loopback HLS
            // proxy; Pulse fragment size is PULSE_LATENCY_MSEC only.
            if (!callbackVout)
            {
                vlcOptions.Add("--vout=x11"); // standalone window only — fights vmem/callbacks
            }

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
        string userAgent,
        string? origin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);

        var options = new List<string>
        {
            $":http-user-agent={userAgent}",
            conservative ? ":avcodec-hw=none" : ":avcodec-hw=any",
            $":network-caching={ResolveLiveNetworkCachingMs(conservative)}",
        };

        if (conservative)
        {
            options.Add(":avcodec-skiploopfilter=all");
            options.Add($":clock-jitter={ClockJitterUs}");
        }

        if (string.IsNullOrWhiteSpace(referer))
            return options;

        var trimmed = referer.Trim();
        options.Add($":http-referrer={trimmed}");

        var originTrimmed = string.IsNullOrWhiteSpace(origin)
            ? null
            : origin.Trim();
        if (originTrimmed is null && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            originTrimmed = uri.GetLeftPart(UriPartial.Authority);
        }

        if (!string.IsNullOrWhiteSpace(originTrimmed))
        {
            options.Add(
                $":avformat-options=headers=Referer: {trimmed}\r\nOrigin: {originTrimmed}\r\nUser-Agent: {userAgent}");
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

    public static int ResolveLiveNetworkCachingMs(bool conservative) =>
        conservative ? WslLiveNetworkCachingMs : DesktopLiveNetworkCachingMs;

    /// <summary>
    /// Software-callback present limits. Native desktop is uncapped
    /// (full decoded size and frame rate). WSL caps width and pace so
    /// Pulse is not starved — test-host only.
    /// </summary>
    public static (int MaxFrameWidth, int MinPresentIntervalMs) ResolveSoftwarePresentLimits(
        bool conservative) =>
        conservative
            ? (WslMaxFrameWidth, WslPresentIntervalMs)
            : (0, 0);

    /// <summary>
    /// True when <paramref name="module"/> is a real aout we are willing to
    /// pin (pulse / alsa). <c>any</c> is a probe, not a SetAudioOutput name.
    /// </summary>
    public static bool IsPinnedAudioOutput(string? module) =>
        TryNormalizeAudioOutput(module, out var normalized) &&
        (normalized == PulseAudioOutput || normalized == AlsaAudioOutput);

    /// <summary>
    /// WSLg Pulse crackles at the default ~20ms fragment. Set
    /// <see cref="PulseLatencyVariableName"/> when conservative and unset.
    /// </summary>
    public static bool TryApplyPulseLatencyHint()
    {
        if (!UseConservativeVlcOptions)
            return false;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PulseLatencyVariableName)))
            return false;

        Environment.SetEnvironmentVariable(PulseLatencyVariableName, PulseLatencyMsec.ToString());
        return true;
    }

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
        var latency = Environment.GetEnvironmentVariable(PulseLatencyVariableName);
        var latencyText = string.IsNullOrWhiteSpace(latency) ? "unset" : latency.Trim();
        return $"aout={aout}; wsl={wsl}; PULSE_SERVER={pulse}; PULSE_LATENCY_MSEC={latencyText}; XDG_RUNTIME_DIR={runtime}";
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
