"""Render the short social cut from the published Kubernetes recording and English voice."""
import json
import math
import os
from pathlib import Path
import re
import subprocess
import tempfile
import wave

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
MEDIA = ROOT / "docs/media/live-stream"
WORK = ROOT / "artifacts/live-stream-video/marketing"
FPS = 24
RATE = 24000
LEAD = 0.25
LIMIT = 60 * FPS
FFMPEG = os.environ.get("FFMPEG", "ffmpeg")

# In/out points in the educational master. Crop only the recorded dashboard, above its lower third.
# Freeze the resolved state at the end to leave time to read the dashboard.
SHOTS = {
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


def subtitles(voice, seconds):
    sentences = re.split(r"(?<=[.!?])\s+", voice)
    word_count = len(voice.split())
    cues = []
    words = 0
    for sentence in sentences:
        start = LEAD + words / word_count * seconds
        words += len(sentence.split())
        cues.append({"start": start, "end": LEAD + words / word_count * seconds, "text": sentence})
    return cues


def plan(directory):
    scenes = json.loads((HERE / "scenes.json").read_text())
    frames = 0
    audio = bytearray()
    vtt = ["WEBVTT", ""]
    for scene in scenes:
        with wave.open(str(WORK / "voice" / (scene["key"] + ".wav")), "rb") as take:
            if (take.getframerate(), take.getnchannels(), take.getsampwidth()) != (RATE, 1, 2):
                raise ValueError(f"Expected 24 kHz mono 16-bit voice for {scene['key']}")
            samples = take.getnframes()
            voice = take.readframes(samples)
        tail = 1.8 if scene["key"] == "try" else 0.65
        count = math.ceil((LEAD + samples / RATE + tail) * FPS)
        check_length(frames + count)
        scene["frames"] = count
        scene["cues"] = subtitles(scene["voice"], samples / RATE)
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


def render(directory):
    scenes, frames = plan(directory)
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
    encode("-framerate", str(FPS), "-start_number", "0", "-i", directory / "frames/%05d.jpg",
           "-i", directory / "voice.wav",
           "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-crf", "18", "-preset", "veryfast",
           "-vf", "scale=in_range=full:out_range=tv:out_color_matrix=bt709,format=yuv420p",
           "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
           "-c:a", "aac", "-b:a", "112k", "-movflags", "+faststart", "-metadata:s:a:0", "language=eng",
           "-metadata", "title=SlimFaas | Give your agent tools the night off", directory / "marketing.mp4")
    (directory / "marketing.mp4").replace(MEDIA / "marketing.mp4")
    (directory / "subtitles.vtt").replace(MEDIA / "marketing.vtt")
    print(f"Marketing cut: {frames / FPS:.2f}s, 1080 × 1350, {FPS} fps", flush=True)


if __name__ == "__main__":
    WORK.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=WORK) as scratch:
        render(Path(scratch))
