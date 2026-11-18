"""Edit verified dashboard frames, narration, captions and a silent preview."""
from bisect import bisect_right
import json
import math
import os
from pathlib import Path
import subprocess
import wave

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
WORK = ROOT / "artifacts/live-stream-video"
OUT = ROOT / "docs/media/live-stream"
FPS = 20
INK = "#121f38"
FFMPEG = os.environ.get("FFMPEG", "ffmpeg")
scenes = json.loads((HERE / "scenes.json").read_text())
chapters = list(dict.fromkeys(s["chapter"] for s in scenes))


def font(size, bold=False):
    override = os.environ.get("FILM_FONT_BOLD" if bold else "FILM_FONT")
    for name in (override, "Arial Bold.ttf" if bold else "Arial.ttf",
                 "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"):
        if name:
            try:
                return ImageFont.truetype(name, size)
            except OSError:
                pass
    raise RuntimeError("Set FILM_FONT and FILM_FONT_BOLD to TrueType font files.")


def stamp(seconds):
    ms = round(1000 * seconds)
    return f"{ms // 3600000:02}:{ms // 60000 % 60:02}:{ms // 1000 % 60:02}.{ms % 1000:03}"


class Take:
    def __init__(self, stage):
        self.directory = WORK / stage
        self.data = json.loads((self.directory / "take.json").read_text())
        self.marks = self.data["marks"]
        self.times, self.files = zip(*self.data["frames"])
        self.loaded = None
        self.image = None

    def frame(self, ms):
        index = max(0, bisect_right(self.times, ms) - 1)
        if self.loaded != index:
            with Image.open(self.directory / self.files[index]) as source:
                self.image = source.convert("RGB")
            self.loaded = index
        return self.image


def dashboard(image, key):
    # Cut in on the function and its queue while preserving the dashboard header.
    if key not in ("wake", "sleep", "burst", "scale", "drain"):
        return image
    top = 132 if key == "wake" else 205
    region = image.crop((492, top, 1332, top + 348))
    canvas = image.copy()
    canvas.paste(region.resize((1440, 596), Image.Resampling.LANCZOS), (0, 108))
    return canvas


def card(scene, speed):
    img = Image.new("RGB", (1920, 1080), INK)
    draw = ImageDraw.Draw(img)
    draw.rectangle((0, 0, 1920, 896), fill="#e3e8ef")
    active = chapters.index(scene["chapter"])
    for i in range(4):
        draw.rectangle((480*i, 896, 480*i+475, 901), fill="#586dff" if i == active else "#384967")
    draw.text((54, 914), scene["chapter"].upper(), fill="#adbaff", font=font(23, True))
    draw.text((52, 943), scene["title"], fill="white", font=font(48, True))
    draw.text((54, 1010), scene["caption"], fill="#e1e7f0", font=font(30))
    if speed > 1.02:
        draw.text((1860, 913), f"{speed:.1f}× elapsed time", fill="white", anchor="ra", font=font(24))
    return img


def preview(takes):
    # Six seconds per chapter. Concentrate on the actual delivery paths and pod dots.
    specs = [
        ("sync", "wake", (505, 177, 1273, 536), "One HTTP call wakes a sleeping pod"),
        ("async", "scale", (570, 227, 1310, 574), "30 queued requests scale out to four pods"),
        ("subscriptions", "broadcast", (337, 198, 1249, 625), "One broadcast reaches four local caches"),
        ("subscriptions", "ack", (219, 190, 1319, 704), "One replica per function, per attempt"),
    ]
    raw = []
    label_font, chapter_font = font(28, True), font(14, True)
    for i, (stage, key, box, label) in enumerate(specs):
        take = takes[stage]
        start = take.marks[key]
        for frame in range(48):
            # Keep the short broadcast/stream trips at normal speed.
            delta = (frame / 8) * (1.8 if key == "scale" else 1)
            image = take.frame(start + delta * 1000)
            shot = Image.new("RGB", (800, 450), INK)
            shot.paste(image.crop(box).resize((800, 374), Image.Resampling.LANCZOS))
            d = ImageDraw.Draw(shot)
            d.rectangle((0, 374, 800, 378), fill="#556bff")
            d.text((17, 386), "SLIMFAAS  /  " + chapters[i].upper() + ("   ·   1.8×" if key == "scale" else ""),
                   font=chapter_font, fill="#b5c1ff")
            d.text((16, 405), label, font=label_font, fill="white")
            raw.append(shot)
    palette = Image.new("RGB", (800, 450 * 12))
    for i in range(12):
        palette.paste(raw[i * 16], (0, i * 450))
    palette = palette.quantize(256)
    indexed = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in raw]
    indexed[0].save(OUT / "preview.gif", save_all=True, append_images=indexed[1:],
                    optimize=True, duration=[120, 130] * 96, loop=0, disposal=1)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    takes = {stage: Take(stage) for stage in ("sync", "async", "subscriptions")}
    evidence = takes["async"].data["evidence"]
    assert evidence["completed"] == evidence["accepted"] == 30
    assert max(s["ready"] for s in evidence["samples"]) == 4
    subs = takes["subscriptions"].data["evidence"]
    cursors = {s["Function"]: {"acknowledgedThrough": s["AcknowledgedThrough"], "lag": s["Lag"]}
               for s in subs["cursors"]}
    assert set(cursors) == {"fibonacci3", "fibonacci4"}
    assert all(s == {"acknowledgedThrough": 1, "lag": 0} for s in cursors.values())
    report = {
        "syncResponse": takes["sync"].data["evidence"]["reply"],
        "asyncAccepted": evidence["accepted"],
        "asyncCompletedWithResult4807526976": evidence["completed"],
        "peakQueueDepth": max(s["queue"] for s in evidence["samples"]),
        "peakReadyWorkers": max(s["ready"] for s in evidence["samples"]),
        "finalReadyWorkers": evidence["samples"][-1]["ready"],
        "invalidatedPods": [p["name"] for p in subs["invalidated"] if p["value"].endswith("invalidated pending")],
        "streamRecipients": [p["name"] for p in subs["after"] if p["value"].endswith("result-48")],
        "subscriptionProgress": cursors,
    }
    (HERE / "observed.json").write_text(json.dumps(report, indent=2) + "\n")
    vtt = ["WEBVTT", ""]
    transcript = ["SlimFaas Live Stream dashboard", ""]
    audio = bytearray()
    clips = []
    elapsed = 0
    rate = 24000
    for i, scene in enumerate(scenes):
        key = scene["key"]
        stage = ("sync", "async", "subscriptions", "subscriptions")[chapters.index(scene["chapter"])]
        take = takes[stage]
        begin = take.marks[key]
        end = take.marks.get(scenes[i+1]["key"], take.marks["end"]) if i+1 < len(scenes) else take.marks["end"]
        with wave.open(str(WORK / "voice" / (key + ".wav")), "rb") as wav:
            assert wav.getframerate() == rate and wav.getnchannels() == 1 and wav.getsampwidth() == 2
            length = wav.getnframes()
            voice = wav.readframes(length)
        # Allow viewers to read the card and see the resolved state after narration.
        count = math.ceil((length / rate + 1.65) * FPS)
        if key not in ("sleep", "scale", "drain"):
            count = max(count, math.ceil((end - begin) / 1000 * FPS))
        duration = count / FPS
        speed = max(1, (end - begin) / 1000 / duration)
        clips.append((scene, take, begin, end, speed, count))
        lead = int(0.45 * rate)
        audio.extend(bytes(lead * 2) + voice + bytes(2 * (count * rate // FPS - length - lead)))
        # Wrap speech at word boundaries into short, readable subtitle cues.
        words = scene["voice"].split()
        lines = []
        for word in words:
            if lines and len(lines[-1]) + len(word) < 42:
                lines[-1] += " " + word
            else:
                lines.append(word)
        offset = 0
        for j in range(0, len(lines), 2):
            cue = lines[j:j+2]
            start = elapsed + .45 + offset / len(words) * length / rate
            offset += sum(len(line.split()) for line in cue)
            stop = elapsed + .45 + offset / len(words) * length / rate
            vtt.extend([f"{stamp(start)} --> {stamp(stop)}", *cue, ""])
        transcript.extend([f"{stamp(elapsed)[:8]}  {scene['title']}", scene["voice"], ""])
        elapsed += duration
    with wave.open(str(WORK / "narration.wav"), "wb") as wav:
        wav.setparams((1, 2, rate, 0, "NONE", "not compressed"))
        wav.writeframes(audio)
    (OUT / "captions.vtt").write_text("\n".join(vtt))
    (OUT / "transcript.txt").write_text("\n".join(transcript))
    encode = subprocess.Popen([FFMPEG, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
        "-s", "1920x1080", "-r", str(FPS), "-i", "-", "-i", str(WORK / "narration.wav"),
        "-i", str(OUT / "captions.vtt"), "-map", "0:v", "-map", "1:a", "-map", "2:s",
        "-c:s", "mov_text", "-metadata:s:s:0", "language=eng", "-metadata:s:a:0", "language=eng",
        "-metadata", "title=SlimFaas Live Stream dashboard", "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "112k", "-movflags", "+faststart", str(OUT / "dashboard.mp4")], stdin=subprocess.PIPE)
    for scene, take, begin, end, speed, count in clips:
        template = card(scene, speed)
        previous = None
        for i in range(count):
            image = take.frame(min(end - 1, begin + 1000 * i / FPS * speed))
            if image is not previous:
                frame = template.copy()
                frame.paste(dashboard(image, scene["key"]).resize((1824, 892), Image.Resampling.LANCZOS), (48, 0))
                pixels = frame.tobytes()
                if scene["key"] == "scale" and i >= count // 2:
                    frame.resize((960, 540), Image.Resampling.LANCZOS).save(OUT / "poster.webp", quality=87)
                previous = image
            encode.stdin.write(pixels)
        print(scene["key"], round(count / FPS, 2), round(speed, 2), flush=True)
    encode.stdin.close()
    if encode.wait():
        raise RuntimeError("MP4 encoding failed")
    preview(takes)
    print(f"Finished {elapsed:.2f}s; GIF {(OUT / 'preview.gif').stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
