"""Render the short social cut from the published Kubernetes recording and English voice."""
import argparse
import json
import math
import os
from pathlib import Path
import re
import subprocess
import tempfile
import wave

HERE = Path(__file__).resolve().parent
MEDIA = HERE.parent / "media"
WORK = HERE.parent / "artifacts" / "marketing"
FPS = 24
RATE = 24000
LEAD = 0.25
LIMIT = 60 * FPS
FFMPEG = os.environ.get("FFMPEG", "ffmpeg")

# In/out points in the educational master. Crop only the recorded dashboard, above its lower third.
# Freeze the resolved state at the end to leave time to read the dashboard.
SHOTS = {
    "idle": (26.1, 31.8, "528:240:1200:364"),
    "wake": (33.15, 43.0, "840:390:840:490"),
    "rush": (66.4, 73.2, "840:390:840:348"),
    "quiet": (79.55, 88.2, "840:390:840:348"),
}


def check_length(frames):
    if frames > LIMIT:
        raise ValueError(f"Marketing cut lasts {frames / FPS:.2f}s. Shorten it to 60 seconds or less.")


def timestamp(seconds):
    ms = round(1000 * seconds)
    return f"{ms // 3600000:02}:{ms // 60000 % 60:02}:{ms // 1000 % 60:02}.{ms % 1000:03}"


def subtitles(voice, samples, cue_samples):
    sentences = re.split(r"(?<=[.!?])\s+", voice)
    if len(cue_samples) == len(sentences) and sum(cue_samples) == samples:
        weights = cue_samples
    else:
        # Replacement narrators can supply WAVs of a different length.
        weights = [len(sentence.split()) for sentence in sentences]
    seconds_per_unit = samples / RATE / sum(weights)
    cues = []
    offset = 0
    for sentence, weight in zip(sentences, weights):
        start = LEAD + offset * seconds_per_unit
        offset += weight
        cues.append({"start": start, "end": LEAD + offset * seconds_per_unit, "text": sentence})
    return cues


def plan(directory, edition="marketing"):
    house = edition == "house"
    source = HERE / "house" if house else HERE
    work = WORK / "house" if house else WORK
    scenes = json.loads((source / "scenes.json").read_text())
    frames = 0
    audio = bytearray()
    vtt = ["WEBVTT", ""]
    for scene in scenes:
        with wave.open(str(work / "voice" / (scene["key"] + ".wav")), "rb") as take:
            if (take.getframerate(), take.getnchannels(), take.getsampwidth()) != (RATE, 1, 2):
                raise ValueError(f"Expected 24 kHz mono 16-bit voice for {scene['key']}")
            samples = take.getnframes()
            voice = take.readframes(samples)
        tail = 1.8 if scene["key"] == "try" else 0.65
        count = math.ceil((LEAD + samples / RATE + tail) * FPS)
        if not house:
            check_length(frames + count)
        scene["frames"] = count
        scene["cues"] = subtitles(scene["voice"], samples, scene.get("cueSamples", []))
        for cue in scene["cues"]:
            start = timestamp(frames / FPS + cue["start"])
            end = timestamp(frames / FPS + cue["end"])
            vtt.extend([f"{start} --> {end}", cue["text"], ""])
        lead_samples = round(LEAD * RATE)
        audio.extend(bytes(2 * lead_samples) + voice + bytes(2 * (count * RATE // FPS - samples - lead_samples)))
        frames += count
    with wave.open(str(directory / "voice.wav"), "wb") as output:
        output.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
        output.writeframes(audio)
    (directory / "subtitles.vtt").write_text("\n".join(vtt), encoding="utf-8")
    return scenes, frames


def encode(*args):
    subprocess.run([FFMPEG, "-nostdin", "-y", "-loglevel", "error", *args], check=True)


def render(directory, edition="marketing"):
    scenes, frames = plan(directory, edition)
    observed = json.loads((HERE.parent / "observed.json").read_text())
    # English voice quotes these measurements from the published master.
    for key, value in {"syncResponse": {"result": 55}, "asyncAccepted": 30, "peakReadyWorkers": 4,
                       "asyncCompletedWithResult4807526976": 30, "finalReadyWorkers": 0}.items():
        if observed[key] != value:
            raise ValueError(f"The recorded {key} changed. Review the marketing script and in/out points.")
    for scene in scenes:
        if scene["key"] not in SHOTS:
            continue
        start, stop, crop = SHOTS[scene["key"]]
        destination = directory / scene["key"]
        destination.mkdir()
        # Resample the selected footage at 12 fps. Chromium holds its final frame for the last beat.
        encode("-ss", str(start), "-t", str(stop - start), "-i", MEDIA / "dashboard.mp4",
               "-vf", f"crop={crop},fps=12", "-q:v", "2", destination / "%04d.jpg")
        scene["images"] = len(list(destination.glob("*.jpg")))
        if scene["images"] < int((stop - start) * 12) - 1:
            raise ValueError(f"Incomplete dashboard excerpt for {scene['key']}")
    (directory / "plan.json").write_text(json.dumps({"fps": FPS, "scenes": scenes, "observed": observed}))
    subprocess.run(["node", HERE / "frames.mjs", directory], check=True)
    title = "The last ones out" if edition == "house" else "Zero idle replicas. One HTTP call wakes your API."
    encode("-framerate", str(FPS), "-start_number", "0", "-i", directory / "frames/%05d.jpg",
           "-i", directory / "voice.wav",
           "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-crf", "18", "-preset", "veryfast",
           "-vf", "scale=in_range=full:out_range=tv:out_color_matrix=bt709,format=yuv420p",
           "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
           "-c:a", "aac", "-b:a", "112k", "-movflags", "+faststart", "-metadata:s:a:0", "language=eng",
           "-metadata", f"title=SlimFaas | {title}",
           "-metadata", "comment=Cost result reported by Guillaume Chervet (AXA France), Devoxx Belgium 2025: "
           "https://m.devoxx.com/events/dvbe25/talks/11002/i-lost-weight-on-kubernetes-with-slimfaas",
           directory / "marketing.mp4")
    (directory / "marketing.mp4").replace(MEDIA / f"{edition}.mp4")
    (directory / "subtitles.vtt").replace(MEDIA / f"{edition}.vtt")
    print(f"{title}: {frames / FPS:.2f}s, 1080 × 1350, {FPS} fps", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("edition", nargs="?", choices=("marketing", "house"), default="marketing")
    edition = parser.parse_args().edition
    work = WORK / "house" if edition == "house" else WORK
    work.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=work) as scratch:
        render(Path(scratch), edition)
