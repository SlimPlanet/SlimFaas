"""Restore the original lossless voice takes, or generate replacements with a local speech server."""
import argparse
import http.client
import json
import os
from pathlib import Path
import subprocess
from urllib.parse import urlsplit
import wave

HERE = Path(__file__).resolve().parent
WORK = HERE / "artifacts"
FFMPEG = os.environ.get("FFMPEG", "ffmpeg")
RATE = 24000


def pcm(source, data=None):
    return subprocess.run([FFMPEG, "-loglevel", "error", "-i", source,
                           "-f", "s16le", "-ar", str(RATE), "-ac", "1", "-"],
                          input=data, capture_output=True, check=True).stdout


def speech_server(value):
    """Only a local speech engine may receive the script. Keep redirects disabled."""
    url = urlsplit(value)
    if (url.scheme not in ("https", "http") or url.hostname not in ("127.0.0.1", "localhost", "::1")
            or url.username or url.password or url.path != "/v1/audio/speech" or url.query or url.fragment):
        raise argparse.ArgumentTypeError("Supply a loopback /v1/audio/speech URL")
    connection = http.client.HTTPSConnection if url.scheme == "https" else http.client.HTTPConnection
    return connection("::1" if url.hostname == "::1" else "127.0.0.1", url.port, timeout=120)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("educational", "marketing", "house", "house-fr"), default="educational")
    parser.add_argument("--speech-url", type=speech_server, help="Optional local Kokoro-compatible /v1/audio/speech endpoint")
    args = parser.parse_args()
    edition = {"educational": Path("."), "marketing": Path("marketing"), "house": Path("marketing/house"),
               "house-fr": Path("marketing/house/fr")}[args.mode]
    source = HERE / edition
    destination = WORK / edition / "voice"
    destination.mkdir(parents=True, exist_ok=True)
    master = b"" if args.speech_url else pcm(str(source / "narration.flac"))
    offset = 0
    for scene in json.loads((source / "scenes.json").read_text()):
        if args.speech_url:
            body = json.dumps({"model": "kokoro", "voice": scene.get("speaker", "af_heart"), "speed": 1.055,
                               "input": scene["voice"], "response_format": "wav"}).encode()
            args.speech_url.request("POST", "/v1/audio/speech", body, {"Content-Type": "application/json"})
            with args.speech_url.getresponse() as response:
                if response.status != 200:
                    raise RuntimeError(f"Local speech server returned HTTP {response.status}")
                audio = pcm("pipe:0", response.read())
        else:
            end = offset + scene["voiceSamples"] * 2
            audio = master[offset:end]
            if len(audio) != end - offset:
                raise ValueError("Narration master is shorter than the scene list")
            offset = end
        with wave.open(str(destination / (scene["key"] + ".wav")), "wb") as wav:
            wav.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
            wav.writeframes(audio)
    if offset != len(master):
        raise ValueError("Unused samples in narration master")
    if args.speech_url:
        args.speech_url.close()


if __name__ == "__main__":
    main()
