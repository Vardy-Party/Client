#!/usr/bin/env python3
"""Generates the six VardyParty UI sounds (stdlib only, deterministic).

Usage:  python3 Tools/generate-ui-sounds.py [output-dir]
Default output dir: VardyParty/Resources/Raw/Sounds/

Spec (docs/architecture/homepage-maui-avalonia.md, "UI sound design"):
  16-bit PCM WAV, 48 kHz, mono. Navigation sounds peak ~ -16 dBFS,
  error -14 dBFS, goal -8 dBFS. Everything is synthesised (sine/triangle
  bursts with exponential decay envelopes) so no third-party assets are
  involved and the files can be regenerated at any time.
"""

import math
import os
import struct
import sys
import wave

SAMPLE_RATE = 48_000


def db(dbfs: float) -> float:
    """dBFS -> linear peak amplitude."""
    return 10.0 ** (dbfs / 20.0)


def silence(ms: float) -> list[float]:
    return [0.0] * int(SAMPLE_RATE * ms / 1000.0)


def tone(freq_start: float, ms: float, *, freq_end: float | None = None,
         decay: float = 18.0, attack_ms: float = 2.0, shape: str = "sine") -> list[float]:
    """A single burst: optional linear pitch glide, exp decay, short attack ramp."""
    n = int(SAMPLE_RATE * ms / 1000.0)
    freq_end = freq_end if freq_end is not None else freq_start
    attack_n = max(1, int(SAMPLE_RATE * attack_ms / 1000.0))
    out = []
    phase = 0.0
    for i in range(n):
        t = i / n
        freq = freq_start + (freq_end - freq_start) * t
        phase += 2.0 * math.pi * freq / SAMPLE_RATE
        if shape == "triangle":
            # Triangle from phase: softer than square, brighter than sine.
            s = 2.0 / math.pi * math.asin(math.sin(phase))
        else:
            s = math.sin(phase)
        env = math.exp(-decay * t)
        if i < attack_n:
            env *= i / attack_n
        out.append(s * env)
    return out


def noise(ms: float, *, seed: int = 1234) -> list[float]:
    """Deterministic white noise, high-passed ~200 Hz (one-pole)."""
    n = int(SAMPLE_RATE * ms / 1000.0)
    state = seed
    raw = []
    for _ in range(n):
        # xorshift32: reproducible across Python versions, no imports.
        state ^= (state << 13) & 0xFFFFFFFF
        state ^= state >> 17
        state ^= (state << 5) & 0xFFFFFFFF
        raw.append((state / 0x7FFFFFFF) - 1.0)
    # One-pole high-pass at ~200 Hz.
    rc = 1.0 / (2.0 * math.pi * 200.0)
    dt = 1.0 / SAMPLE_RATE
    alpha = rc / (rc + dt)
    out = []
    prev_in = prev_out = 0.0
    for x in raw:
        y = alpha * (prev_out + x - prev_in)
        out.append(y)
        prev_in, prev_out = x, y
    return out


def mix(*layers: list[float]) -> list[float]:
    n = max(len(layer) for layer in layers)
    out = [0.0] * n
    for layer in layers:
        for i, s in enumerate(layer):
            out[i] += s
    return out


def envelope(samples: list[float], *, fade_out_ms: float = 8.0) -> list[float]:
    """Short linear fade-out so nothing clicks at the end."""
    n = len(samples)
    fade_n = min(n, int(SAMPLE_RATE * fade_out_ms / 1000.0))
    out = list(samples)
    for i in range(fade_n):
        out[n - fade_n + i] *= 1.0 - (i + 1) / fade_n
    return out


def normalise(samples: list[float], peak_dbfs: float) -> list[float]:
    peak = max(abs(s) for s in samples) or 1.0
    gain = db(peak_dbfs) / peak
    return [s * gain for s in samples]


def write_wav(path: str, samples: list[float]) -> None:
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SAMPLE_RATE)
        frames = bytearray()
        for s in samples:
            frames += struct.pack("<h", max(-32768, min(32767, int(s * 32767.0))))
        w.writeframes(bytes(frames))
    print(f"  {os.path.basename(path)}  {len(samples) / SAMPLE_RATE * 1000:.0f} ms")


def focus_tick() -> list[float]:
    # 30 ms very quiet soft tick: 2 kHz sine, fast exponential decay.
    return normalise(envelope(tone(2000, 30, decay=10.0, attack_ms=0.5)), -16.0)


def select() -> list[float]:
    # 80 ms rising two-note blip: 660 -> 880 Hz.
    first = tone(660, 40, decay=6.0)
    second = silence(36) + tone(880, 44, decay=6.0)
    return normalise(envelope(mix(first, second)), -16.0)


def back() -> list[float]:
    # Mirrored falling blip: 880 -> 660 Hz.
    first = tone(880, 40, decay=6.0)
    second = silence(36) + tone(660, 44, decay=6.0)
    return normalise(envelope(mix(first, second)), -16.0)


def menu_open() -> list[float]:
    # 100 ms airy swipe: high-passed noise fading in/out under a soft up-glide.
    airy = noise(100, seed=97531)
    n = len(airy)
    shaped = [s * math.sin(math.pi * i / n) * 0.8 for i, s in enumerate(airy)]
    glide = tone(500, 100, freq_end=900, decay=4.0, attack_ms=15.0)
    scaled_glide = [s * 0.45 for s in glide]
    return normalise(envelope(mix(shaped, scaled_glide)), -16.0)


def error() -> list[float]:
    # 150 ms muted double-tone: two low triangle taps at 220 Hz.
    first = tone(220, 65, decay=8.0, shape="triangle")
    second = silence(80) + tone(196, 70, decay=8.0, shape="triangle")
    return normalise(envelope(mix(first, second)), -14.0)


def goal() -> list[float]:
    # ~1.4 s celebratory sting: rising arpeggio (C5 E5 G5 C6) layered with a
    # noise swell that crests as the top note lands.
    notes = [(523.25, 0), (659.25, 110), (783.99, 220), (1046.50, 330)]
    layers = []
    for freq, offset_ms in notes:
        layers.append(silence(offset_ms) + tone(freq, 900, decay=3.2, attack_ms=6.0))
        # Octave shimmer under each note, quiet.
        shimmer = silence(offset_ms) + tone(freq * 2, 500, decay=5.0, attack_ms=6.0)
        layers.append([s * 0.25 for s in shimmer])

    swell_raw = noise(700, seed=24680)
    n = len(swell_raw)
    swell = [s * (math.sin(math.pi * min(1.0, i / (n * 0.55))) ** 2) * 0.5
             for i, s in enumerate(swell_raw)]
    layers.append(swell)

    combined = mix(*layers)
    combined += silence(120)  # tail room before the fade
    return normalise(envelope(combined, fade_out_ms=180.0), -8.0)


def main() -> None:
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "VardyParty", "Resources", "Raw", "Sounds")
    os.makedirs(out_dir, exist_ok=True)
    print(f"Writing UI sounds to {out_dir}")
    write_wav(os.path.join(out_dir, "focus_tick.wav"), focus_tick())
    write_wav(os.path.join(out_dir, "select.wav"), select())
    write_wav(os.path.join(out_dir, "back.wav"), back())
    write_wav(os.path.join(out_dir, "menu_open.wav"), menu_open())
    write_wav(os.path.join(out_dir, "error.wav"), error())
    write_wav(os.path.join(out_dir, "goal.wav"), goal())


if __name__ == "__main__":
    main()
