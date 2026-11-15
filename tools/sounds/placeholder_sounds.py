"""Writes the engine's placeholder sounds (sounds/*.wav): plain synthesised tones, original work, no samples.
Run from the repository root: python3 tools/sounds/placeholder_sounds.py"""
import math, random, struct, wave

RATE = 48000
random.seed(7)

def write(name, samples):
    with wave.open(f"sounds/{name}.wav", "wb") as out:
        out.setnchannels(1); out.setsampwidth(2); out.setframerate(RATE)
        out.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, s)) * 32000)) for s in samples))

def don(seconds=0.22):  # a low drum head: a falling sine with a soft thump of noise
    phase, out = 0.0, []
    for i in range(int(RATE * seconds)):
        t = i / RATE
        phase += 2 * math.pi * (70 + 110 * math.exp(-t * 30)) / RATE
        out.append(0.9 * math.sin(phase) * math.exp(-t * 14) + 0.15 * random.uniform(-1, 1) * math.exp(-t * 90))
    return out

def ka(seconds=0.09):  # a rim: two bright partials and a click
    return [(0.5 * math.sin(2 * math.pi * 1900 * i / RATE) + 0.3 * math.sin(2 * math.pi * 3150 * i / RATE)) * math.exp(-i / RATE * 60)
            + 0.25 * random.uniform(-1, 1) * math.exp(-i / RATE * 400) for i in range(int(RATE * seconds))]

def blip(hz, seconds):
    return [0.5 * math.sin(2 * math.pi * hz * i / RATE) * math.exp(-i / RATE * 35) for i in range(int(RATE * seconds))]

write("don", don())
write("ka", ka())
write("tick", blip(2000, 0.05))
write("coin", blip(988, 0.08) + blip(1319, 0.25))
