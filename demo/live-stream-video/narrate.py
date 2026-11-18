"""Restore the original lossless voice takes, or generate replacements with a local speech server."""
import argparse
import json
import os
from pathlib import Path
import subprocess
from urllib.request import Request, urlopen
import wave

HERE = Path(__file__).resolve().parent
WORK = HERE.parents[1] / "artifacts/live-stream-video/voice"
FFMPEG = os.environ.get("FFMPEG", "ffmpeg")
RATE = 24000


def pcm(source, data=None):
    return subprocess.run([FFMPEG, "-loglevel", "error", "-i", source,
                           "-f", "s16le", "-ar", str(RATE), "-ac", "1", "-"],
                          input=data, capture_output=True, check=True).stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--speech-url", help="Optional local Kokoro-compatible /v1/audio/speech endpoint")
    args = parser.parse_args()
    WORK.mkdir(parents=True, exist_ok=True)
    master = b"" if args.speech_url else pcm(str(HERE / "narration.flac"))
    offset = 0
    for scene in json.loads((HERE / "scenes.json").read_text()):
        if args.speech_url:
            body = json.dumps({"model": "kokoro", "voice": "af_heart", "speed": 1.04,
                               "input": scene["voice"], "response_format": "wav"}).encode()
            request = Request(args.speech_url, data=body, headers={"Content-Type": "application/json"})
            with urlopen(request, timeout=120) as response:
                audio = pcm("pipe:0", response.read())
        else:
            end = offset + scene["voiceSamples"] * 2
            audio = master[offset:end]
            if len(audio) != end - offset:
                raise ValueError("Narration master is shorter than the scene list")
            offset = end
        with wave.open(str(WORK / (scene["key"] + ".wav")), "wb") as wav:
            wav.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
            wav.writeframes(audio)
    if offset != len(master):
        raise ValueError("Unused samples in narration master")


if __name__ == "__main__":
    main()
