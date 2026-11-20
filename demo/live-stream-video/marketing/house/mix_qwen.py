"""Master a complete directory of Qwen takes and update the house film's cue timings."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

import numpy as np
import soundfile as sf

from record_qwen import HERE, directed_cues

RATE = 24000
PAUSE_SECONDS = {"palace-0": .36, "leave-0": .36, "year-0": .36, "year-2": .76}


def trim_take(voice):
    # A short RMS window keeps the initial consonant and the end of each breath.
    step = 120
    blocks = np.pad(voice, (0, -len(voice) % step)).reshape(-1, step)
    energy = np.sqrt(np.mean(blocks ** 2, axis=1))
    active = np.flatnonzero(energy > max(.0015, np.percentile(energy, 80) * .016))
    if not len(active):
        raise ValueError("Silent take")
    start = max(0, active[0] * step - 800)
    stop = min(len(voice), (active[-1] + 1) * step + 1500)
    voice = voice[start:stop].copy()
    fade = min(128, len(voice) // 3)
    voice[:fade] *= np.linspace(0, 1, fade)
    voice[-fade:] *= np.linspace(1, 0, fade)
    return voice


def pace_take(voice, key, visual):
    # Let the listener take in the prices and give the chick a beat to land the joke.
    if key in ("year-2", "year-3"):
        tempo = 1.0
    elif key in ("podbill-0", "savings-1", "year-0"):
        tempo = 1.12
    elif visual in ("opening", "penguin"):
        tempo = 1.18
    else:
        tempo = 1.24
    filters = f"atempo={tempo}"
    if visual in ("opening", "penguin"):
        # Lift pitch and formants by two semitones while retaining the intended delivery speed.
        boy_rate = round(RATE * 2 ** (2 / 12))
        filters = f"asetrate={boy_rate},aresample={RATE},atempo={tempo * RATE / boy_rate}"
    command = [os.environ.get("FFMPEG", "ffmpeg"), "-v", "error", "-nostdin", "-f", "f32le",
               "-ar", str(RATE), "-ac", "1", "-i", "-", "-af", filters, "-f", "f32le", "-"]
    paced = subprocess.run(command, input=voice.astype("<f4").tobytes(), capture_output=True, check=True)
    return np.frombuffer(paced.stdout, "<f4").copy()


def normalize(voice):
    command = [os.environ.get("FFMPEG", "ffmpeg"), "-hide_banner", "-nostdin",
               "-f", "f32le", "-ar", str(RATE), "-ac", "1", "-i", "-"]
    source = voice.astype("<f4").tobytes()
    target = "highpass=f=50,loudnorm=I=-17:TP=-2:LRA=11"
    measured = subprocess.run([*command, "-af", target + ":print_format=json", "-f", "null", "-"],
                              input=source, capture_output=True, check=True).stderr.decode()
    stats = json.loads(measured[measured.rindex("{"):measured.rindex("}") + 1])
    fields = {"measured_I": "input_i", "measured_TP": "input_tp", "measured_LRA": "input_lra",
              "measured_thresh": "input_thresh", "offset": "target_offset"}
    measurement = ":".join(f"{key}={stats[field]}" for key, field in fields.items())
    filters = f"{target}:{measurement}:linear=true,aresample={RATE}"
    mix = subprocess.run([*command, "-v", "error", "-af", filters, "-f", "s16le", "-"],
                         input=source, capture_output=True, check=True)
    return np.frombuffer(mix.stdout, "<i2")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("language", choices=("fr", "en"))
    parser.add_argument("--takes", required=True, type=Path)
    args = parser.parse_args()
    folder = HERE / "fr" if args.language == "fr" else HERE
    scenes = json.loads((folder / "scenes.json").read_text())
    scene_cues = {}
    for key, *_ in directed_cues(args.language):
        scene_cues.setdefault(key.rsplit("-", 1)[0], []).append(key)
    master = []
    for scene in scenes:
        name = scene["key"]
        cues = []
        for key in scene_cues[name]:
            voice, rate = sf.read(args.takes / f"{key}.wav", dtype="float32")
            if rate != RATE or voice.ndim != 1 or len(voice) < RATE // 4 or not np.isfinite(voice).all():
                raise ValueError(f"Expected a finite mono 24 kHz take for {key}")
            voice = trim_take(voice)
            voice = pace_take(voice, key, scene["visual"])
            if key == "year-0":
                voice *= 10 ** (.7 / 20)
            elif key == "year-2":
                voice *= 10 ** (-1.6 / 20)
            cues.append(voice)
        for i in range(len(cues) - 1):
            key = f"{name}-{i}"
            pause = PAUSE_SECONDS.get(key, .24)
            cues[i] = np.concatenate([cues[i], np.zeros(round(RATE * pause), dtype="float32")])
        mix = normalize(np.concatenate(cues))
        lengths = [len(cue) for cue in cues]
        correction = len(mix) - sum(lengths)
        if abs(correction) > RATE // 100:
            raise ValueError(f"Unexpected duration change while mastering {name}")
        lengths[-1] += correction
        scene.update(voiceSamples=len(mix), cueSamples=lengths)
        master.append(mix)
    audio = np.concatenate(master)
    with tempfile.TemporaryDirectory(dir=folder) as scratch:
        flac = Path(scratch) / "narration.flac"
        sf.write(flac, audio, RATE, subtype="PCM_16")
        decoded, rate = sf.read(flac, dtype="int16")
        if rate != RATE or not np.array_equal(decoded, audio):
            raise ValueError("FLAC round-trip verification failed")
        manifest = Path(scratch) / "scenes.json"
        manifest.write_text(json.dumps(scenes, ensure_ascii=False, indent=2) + "\n")
        flac.replace(folder / flac.name)
        manifest.replace(folder / manifest.name)


if __name__ == "__main__":
    main()
