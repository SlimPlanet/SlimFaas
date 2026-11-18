"""Edit verified dashboard frames, narration, captions and a silent preview."""
from bisect import bisect_right
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import wave

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
WORK = ROOT / "artifacts/live-stream-video"
OUT = ROOT / "docs/media/live-stream"
FPS = 20
FFMPEG = os.environ.get("FFMPEG", "ffmpeg")
scenes = json.loads((HERE / "scenes.json").read_text())
live_chapters = list(dict.fromkeys(s["chapter"] for s in scenes if "card" not in s))


def stamp(seconds):
    ms = round(1000 * seconds)
    return f"{ms // 3600000:02}:{ms // 60000 % 60:02}:{ms // 1000 % 60:02}.{ms % 1000:03}"


def ffmpeg(*args):
    subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-filter_complex_threads", "2", *args],
                   cwd=WORK, check=True)


def prepare_fonts():
    for weight, override, filenames in [
        ("regular", "FILM_FONT", ("Arial.ttf", "DejaVuSans.ttf")),
        ("bold", "FILM_FONT_BOLD", ("Arial Bold.ttf", "DejaVuSans-Bold.ttf")),
    ]:
        candidates = [Path(os.environ.get(override, ""))]
        for location in ("/System/Library/Fonts/Supplemental", "/usr/share/fonts/truetype/dejavu",
                         "/usr/share/fonts/dejavu", "C:/Windows/Fonts"):
            candidates.extend(Path(location) / name for name in filenames)
        source = next((p for p in candidates if p.is_file()), None)
        if source is None:
            raise RuntimeError(f"Set {override} to an installed TrueType font")
        shutil.copyfile(source, WORK / f"{weight}.ttf")


def text_filter(name, value, x, y, size, color="white", weight="regular"):
    # textfile keeps punctuation, percent signs and quotes out of the filter syntax.
    filename = f"label-{name}.txt"
    (WORK / filename).write_text(value, encoding="utf-8")
    return (f"drawtext=textfile={filename}:expansion=none:fontfile={weight}.ttf:"
            f"fontsize={size}:fontcolor={color}:x={x}:y={y}")


def timeline(filename, paths, fps=None):
    lines = ["ffconcat version 1.0"]
    for path in paths:
        lines.append(f"file '{path}'")
        if fps:
            lines.extend([f"option framerate {fps}", f"duration {1 / fps:.6f}"])
    (WORK / filename).write_text("\n".join(lines) + "\n")
    return ["-f", "concat", "-safe", "0", "-i", filename]


class Take:
    def __init__(self, stage):
        self.stage = stage
        self.data = json.loads((WORK / stage / "take.json").read_text())
        self.marks = self.data["marks"]
        self.times, self.files = zip(*self.data["frames"])

    def at(self, ms):
        index = max(0, bisect_right(self.times, ms) - 1)
        return f"{self.stage}/{self.files[index]}"


def encode_clip(scene, take, begin, end, speed, count):
    key = scene["key"]
    inputs = timeline("frames.ffconcat", (
        take.at(min(end - 1, begin + 1000 * i / FPS * speed)) for i in range(count)
    ), FPS)
    prefix = "[0:v]"
    if key in ("wake", "sleep", "burst", "scale", "drain"):
        top = 132 if key == "wake" else 205
        prefix += ("split[header][map];[header]crop=1440:108:0:0[h];"
                   f"[map]crop=840:348:492:{top},scale=1440:596:flags=lanczos[m];[h][m]vstack,")
    filters = ["scale=1824:892:flags=lanczos", "setsar=1", "pad=1920:896:48:0:0xe3e8ef",
               "pad=1920:1080:0:0:0x121f38"]
    for i, chapter in enumerate(live_chapters):
        color = "0x586dff" if scene["chapter"] == chapter else "0x384967"
        filters.append(f"drawbox=x={480*i}:y=896:w=476:h=6:color={color}:t=fill")
    filters.extend([text_filter("chapter", scene["chapter"].upper(), 54, 918, 23, "0xadbaff", "bold"),
                    text_filter("title", scene["title"], 52, 950, 48, weight="bold"),
                    text_filter("caption", scene["caption"], 54, 1014, 30, "0xe1e7f0")])
    if speed > 1.02:
        filters.append(text_filter("rate", f"{speed:.1f}× elapsed time", "1860-tw", 917, 24))
    ffmpeg(*inputs, "-filter_complex", prefix + ",".join(filters),
           "-c:v", "libx264", "-preset", "veryfast", "-crf", "19",
           "-pix_fmt", "yuv420p", f"{key}.mp4")
    if key == "scale":
        ffmpeg("-ss", str((count - 4) / FPS), "-i", f"{key}.mp4", "-frames:v", "1",
               "-vf", "scale=960:540:flags=lanczos", "-c:v", "libwebp", "-quality", "87", str(OUT / "poster.webp"))


def encode_card(scene, count):
    # Title sequences use 10 fps. The opening inset plays recorded dashboard frames.
    ffmpeg("-stream_loop", "-1", "-framerate", "10", "-i", f"cards/{scene['key']}-%02d.png",
           "-vf", f"fps={FPS},fade=t=in:d=0.18", "-frames:v", str(count),
           "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p",
           scene["key"] + ".mp4")


def preview(takes):
    # Six seconds per chapter. The crop includes each recipient and its status.
    specs = [
        ("sync", "wake", (505,177,768,359), "The HTTP proxy wakes the first pod"),
        ("async", "scale", (570,227,740,347), "SlimFaas scrapes metrics and scales to four"),
        ("subscriptions", "broadcast", (337,198,912,427), "One broadcast reaches four local caches"),
        ("subscriptions", "ack", (219,190,1100,514), "One replica per function, per attempt"),
    ]
    for i, (stage, key, box, label) in enumerate(specs):
        take = takes[stage]
        speed = 1.8 if key == "scale" else 1
        inputs = timeline("highlight.ffconcat", (
            take.at(take.marks[key] + frame * 125 * speed) for frame in range(48)
        ), 8)
        x, y, w, h = box
        chapter = "SLIMFAAS  /  " + live_chapters[i].upper() + ("   ·   1.8×" if speed != 1 else "")
        filters = [f"crop={w}:{h}:{x}:{y}", "scale=800:374:flags=lanczos", "setsar=1",
                   "pad=800:450:0:0:0x121f38", "drawbox=x=0:y=374:w=800:h=5:color=0x556bff:t=fill",
                   text_filter("gif-chapter", chapter, 17, 388, 14, "0xb5c1ff", "bold"),
                   text_filter("gif-title", label, 16, 410, 28, weight="bold")]
        ffmpeg(*inputs, "-vf", ",".join(filters), "-c:v", "ffv1", f"highlight-{i}.mkv")
    inputs = timeline("highlights.ffconcat", [f"highlight-{i}.mkv" for i in range(4)])
    ffmpeg(*inputs, "-filter_complex", "split[a][b];[a]palettegen[p];[b][p]paletteuse=dither=none",
           "-loop", "0", str(OUT / "preview.gif"))


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
    # Capture boundaries are independent of where editorial cards are inserted in the film.
    recorded = {}
    for take in takes.values():
        marks = sorted(take.marks.items(), key=lambda mark: mark[1])
        for (key, begin), (_, end) in zip(marks, marks[1:]):
            recorded[key] = (take, begin, end)
    chapter_times = []
    for scene in scenes:
        key = scene["key"]
        with wave.open(str(WORK / "voice" / (key + ".wav")), "rb") as wav:
            assert wav.getframerate() == rate and wav.getnchannels() == 1 and wav.getsampwidth() == 2
            length = wav.getnframes()
            voice = wav.readframes(length)
        # Allow viewers to read the card and see the resolved state after narration.
        count = math.ceil((length / rate + 0.9) * FPS)
        if "card" in scene:
            clips.append((scene, count))
        else:
            take, begin, end = recorded[key]
            if key in ("wake", "burst", "broadcast", "ack"):
                # Keep the full real-time request and its visible outcome, including the ACK table.
                count = max(count, math.ceil((end - begin) / 1000 * FPS))
            speed = max(1, (end - begin) / 1000 / (count / FPS)) if key in ("sleep", "scale", "drain") else 1
            clips.append((scene, take, begin, end, speed, count))
        duration = count / FPS
        if not chapter_times or scene["chapter"] != chapter_times[-1][0]:
            chapter_times.append((scene["chapter"], elapsed))
        lead = int(0.3 * rate)
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
            start = elapsed + lead / rate + offset / len(words) * length / rate
            offset += sum(len(line.split()) for line in cue)
            stop = elapsed + lead / rate + offset / len(words) * length / rate
            vtt.extend([f"{stamp(start)} --> {stamp(stop)}", *cue, ""])
        transcript.extend([f"{stamp(elapsed)[:8]}  {scene['title']}", scene["voice"], ""])
        elapsed += duration
    with wave.open(str(WORK / "narration.wav"), "wb") as wav:
        wav.setparams((1, 2, rate, 0, "NONE", "not compressed"))
        wav.writeframes(audio)
    (OUT / "captions.vtt").write_text("\n".join(vtt))
    (OUT / "transcript.txt").write_text("\n".join(transcript))
    prepare_fonts()
    subprocess.run(["node", str(HERE / "cards.mjs")], check=True)
    for clip in clips:
        if "card" in clip[0]:
            encode_card(*clip)
        else:
            encode_clip(*clip)
        print(clip[0]["key"], round(clip[-1] / FPS, 2), flush=True)
    inputs = timeline("edit.ffconcat", [scene["key"] + ".mp4" for scene in scenes])
    metadata = [";FFMETADATA1"]
    for (name, start), (_, stop) in zip(chapter_times, chapter_times[1:] + [("", elapsed)]):
        metadata.extend(["[CHAPTER]", "TIMEBASE=1/1000", f"START={round(start*1000)}",
                         f"END={round(stop*1000)}", f"title={name}"])
    (WORK / "chapters.ffmeta").write_text("\n".join(metadata) + "\n")
    ffmpeg(*inputs, "-i", "narration.wav", "-i", str(OUT / "captions.vtt"), "-f", "ffmetadata", "-i", "chapters.ffmeta",
           "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "copy", "-c:a", "aac", "-b:a", "112k",
           "-map_chapters", "3", "-movie_timescale", "1000",
           "-c:s", "mov_text", "-metadata:s:s:0", "language=eng", "-metadata:s:a:0", "language=eng",
           "-metadata", "title=SlimFaas Live Stream dashboard", "-movflags", "+faststart", str(OUT / "dashboard.mp4"))
    preview(takes)
    print(f"Finished {elapsed:.2f}s; GIF {(OUT / 'preview.gif').stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
