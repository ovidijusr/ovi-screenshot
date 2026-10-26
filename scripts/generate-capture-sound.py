"""Rebuild the original, quiet capture tick using only Python's standard library."""

from pathlib import Path
import math
import random
import struct
import wave


def main():
    sample_rate = 48_000
    duration = 0.16
    random_source = random.Random(73)
    low_pass = 0.0
    body = 0.0
    samples = []
    for index in range(round(sample_rate * duration)):
        time = index / sample_rate
        low_pass += 0.22 * (random_source.uniform(-1, 1) - low_pass)
        body += 0.025 * (low_pass - body)
        texture = (low_pass - body) * math.exp(-time / 0.018)
        second_tick = max(0, time - 0.028)
        secondary = 0 if time < 0.028 else (low_pass - body) * math.exp(-second_tick / 0.014) * 0.35
        tone = math.sin(2 * math.pi * (450 * time - 650 * time * time)) * math.exp(-time / 0.024) * 0.16
        attack = 0.5 - 0.5 * math.cos(math.pi * min(1, time / 0.004))
        release = 0.5 - 0.5 * math.cos(math.pi * min(1, (duration - time) / 0.025))
        samples.append((texture + secondary + tone) * attack * release)

    gain = 0.18 / max(abs(sample) for sample in samples)
    pcm = [round(sample * gain * 32767) for sample in samples]
    pcm[0] = pcm[-1] = 0
    destination = Path(__file__).resolve().parents[1] / "apps/windows/Assets/capture.wav"
    with wave.open(str(destination), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(struct.pack(f"<{len(pcm)}h", *pcm))
    print(f"Wrote {destination.name}: {duration:.2f}s, {sample_rate} Hz, peak 0.18")


if __name__ == "__main__":
    main()
