"""Record directed house-film cues as individual 24 kHz WAVs with a local MLX model."""
import argparse
import json
from pathlib import Path
import re
import tempfile

HERE = Path(__file__).resolve().parent
ACCENTS = {
    "en": "Speak in natural, clear American English.",
    "fr": "Parle en français de France avec une diction naturelle et précise.",
}
FRENCH_NUMBERS = {
    "72 000 €": "soixante-douze mille euros", "10 000 €": "dix mille euros",
    "6 000 €": "six mille euros", "4 000 €": "quatre mille euros",
    "96 centimes": "quatre-vingt-seize centimes", "60 %": "soixante pour cent",
    "80 €": "quatre-vingts euros", "10 €": "dix euros",
}


def spoken(text, language):
    """Keep the printed captions while spelling out the amounts and technical terms for the actor."""
    text = " ".join(text.split()).replace("HTTP", "H T T P").replace("SDK", "S D K")
    if language == "en":
        return text.replace("SlimFaas", "Slim Foss")
    for printed, words in FRENCH_NUMBERS.items():
        text = text.replace(printed, words)
    return text.replace("SlimFaas", "Slim Fass").replace("agentic", "agentiques")


def directed_cues(language):
    score = json.loads((HERE / "voice-direction.json").read_text())
    overrides = score.get("spokenOverrides", {}).get(language, {})
    source = HERE / "fr" if language == "fr" else HERE
    for scene in json.loads((source / "scenes.json").read_text()):
        actor = score["cast"][score["roleByVisual"][scene["visual"]]]
        lines = re.split(r"(?<=[.!?])\s+", scene["voice"])
        notes = score["scenes"][language][scene["key"]]
        for i, (line, note) in enumerate(zip(lines, notes, strict=True)):
            instruction = " ".join((ACCENTS[language], actor["instruct"][language], note))
            key = f"{scene['key']}-{i}"
            text = overrides[key] if key in overrides else spoken(line, language)
            yield key, text, actor["voice"][language], instruction


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("language", choices=ACCENTS)
    parser.add_argument("--model-dir", type=Path, required=True, help="Local Qwen3-TTS 1.7B CustomVoice MLX snapshot")
    parser.add_argument("--output", type=Path, required=True, help="Directory for the WAV takes")
    parser.add_argument("--cue", action="append", help="Record only these cues, e.g. --cue year-2 --cue year-3")
    parser.add_argument("--seed", type=int, default=8471)
    args = parser.parse_args()
    cues = list(directed_cues(args.language))
    if args.cue:
        indexed = {cue[0]: cue for cue in cues}
        for key in args.cue:
            if key not in indexed:
                parser.error(f"Unknown cue {key!r}. Available: {', '.join(indexed)}")
        cues = [indexed[key] for key in dict.fromkeys(args.cue)]
    if not args.model_dir.is_dir():
        parser.error(f"Model directory does not exist: {args.model_dir}")

    # The standard film render restores the recorded FLAC and needs none of these packages.
    import mlx.core as mx
    from mlx_audio.tts.utils import load_model
    import numpy as np
    import soundfile as sf

    model = load_model(args.model_dir)
    if model.config.tts_model_type != "custom_voice" or model.config.tts_model_size != "1b7":
        parser.error("Supply the 1.7B CustomVoice checkpoint to use acting instructions.")
    args.output.mkdir(parents=True, exist_ok=True)
    for start in range(0, len(cues), 4):
        batch = cues[start:start + 4]
        mx.random.seed(args.seed + start + sum(map(ord, batch[0][0])))
        cap = min(170, max(60, 9 * max(len(text.split()) for _, text, _, _ in batch) + 18))
        takes = model.batch_generate(
            texts=[cue[1] for cue in batch], voices=[cue[2] for cue in batch],
            instructs=[cue[3] for cue in batch], lang_code="French" if args.language == "fr" else "English",
            temperature=.72, top_k=50, top_p=.96, repetition_penalty=1.05, max_tokens=cap,
        )
        remaining = {cue[0] for cue in batch}
        for take in takes:
            key = batch[take.sequence_idx][0]
            remaining.remove(key)
            audio = np.asarray(take.audio, dtype=np.float32).reshape(-1)
            if take.sample_rate != 24000 or len(audio) < 6000 or not np.isfinite(audio).all():
                raise ValueError(f"Invalid generated audio for {key}")
            path = args.output / (key + ".wav")
            with tempfile.TemporaryDirectory(dir=args.output) as temporary:
                staged = Path(temporary) / path.name
                sf.write(staged, audio, take.sample_rate, subtype="PCM_16")
                staged.replace(path)
            print(path, flush=True)
        if remaining:
            raise RuntimeError(f"No takes returned for {', '.join(sorted(remaining))}")


if __name__ == "__main__":
    main()
