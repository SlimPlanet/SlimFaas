from contextlib import redirect_stderr
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

from marketing import render
from marketing.house import record_qwen


class MarketingTimelineTests(unittest.TestCase):
    def plan_voice(self, directory, samples, edition="marketing"):
        source = directory / "house" if edition == "house" else directory
        source.mkdir(exist_ok=True)
        (source / "scenes.json").write_text(json.dumps([{"key": "try", "voice": "Wake up. Back to sleep."}]))
        voice_dir = source / "voice"
        voice_dir.mkdir()
        with wave.open(str(voice_dir / "try.wav"), "wb") as output:
            output.setparams((1, 2, render.RATE, 0, "NONE", "not compressed"))
            output.writeframes(bytes(2 * samples))
        with patch.object(render, "HERE", directory), patch.object(render, "WORK", directory):
            return render.plan(directory, edition)

    def test_one_minute_includes_voice_padding_and_cta_hold(self):
        with tempfile.TemporaryDirectory() as scratch:
            directory = Path(scratch)
            samples = round((60 - render.LEAD - 1.8) * render.RATE)
            scenes, frames = self.plan_voice(directory, samples)
            self.assertEqual(frames, 60 * render.FPS)
            with wave.open(str(directory / "voice.wav"), "rb") as master:
                self.assertEqual(master.getnframes(), 60 * render.RATE)
            first, last = scenes[0]["cues"]
            self.assertEqual(first["start"], render.LEAD)
            self.assertEqual(first["end"], last["start"])
            self.assertAlmostEqual(last["end"], render.LEAD + samples / render.RATE)
            self.assertIn("Back to sleep.", (directory / "subtitles.vtt").read_text())

    def test_caption_change_follows_the_recorded_sentence_boundary(self):
        cues = render.subtitles("All thirty finished. The workers scaled back to zero.", 3 * render.RATE,
                                [render.RATE, 2 * render.RATE])
        self.assertEqual(cues[0]["end"], render.LEAD + 1)
        self.assertEqual(cues[1]["start"], cues[0]["end"])
        self.assertEqual(cues[1]["end"], render.LEAD + 3)

    def test_each_house_caption_has_a_voice_direction_and_a_recorded_boundary(self):
        house = render.HERE / "house"
        direction = json.loads((house / "voice-direction.json").read_text())
        for source in (house, house / "fr"):
            scenes = json.loads((source / "scenes.json").read_text())
            language = "fr" if source.name == "fr" else "en"
            cues = {key: (text, voice, note) for key, text, voice, note in record_qwen.directed_cues(language)}
            for key, spoken in direction["spokenOverrides"].get(language, {}).items():
                self.assertEqual(cues[key][0], spoken)
            self.assertEqual(set(direction["scenes"][language]), {scene["key"] for scene in scenes})
            for scene in scenes:
                with self.subTest(edition=source.name, scene=scene["key"]):
                    role = direction["roleByVisual"][scene["visual"]]
                    self.assertTrue(direction["cast"][role]["voice"][language])
                    notes = direction["scenes"][language][scene["key"]]
                    lengths = scene["cueSamples"]
                    self.assertEqual(sum(lengths), scene["voiceSamples"])
                    captions = render.subtitles(scene["voice"], sum(lengths), lengths)
                    self.assertEqual(len(captions), len(notes))
                    self.assertEqual(len(lengths), len(notes))
                    for i, note in enumerate(notes):
                        text, voice, instruction = cues[f"{scene['key']}-{i}"]
                        self.assertTrue(note.strip() and text.strip())
                        self.assertEqual(voice, direction["cast"][role]["voice"][language])
                        self.assertTrue(instruction.endswith(note))
            amount = "soixante-douze mille euros" if language == "fr" else "seventy-two thousand euros"
            self.assertIn(amount, cues["year-0"][0])

    def test_mouth_follows_the_audio_and_closes_during_padding(self):
        samples = (4096).to_bytes(2, "little") * (render.RATE // render.FPS)
        levels = render.mouth_motion(samples, 8)
        self.assertEqual(levels, [0, 0, 0, 0, 0, 0, 1, 0])

    def test_unknown_voice_cue_fails_before_loading_model_or_writing_takes(self):
        with tempfile.TemporaryDirectory() as scratch:
            output = Path(scratch) / "takes"
            args = ["record_qwen.py", "fr", "--cue", "absent", "--model-dir", scratch, "--output", str(output)]
            with patch("sys.argv", args), redirect_stderr(io.StringIO()) as error, self.assertRaises(SystemExit):
                record_qwen.main()
            self.assertIn("Unknown cue 'absent'", error.getvalue())
            self.assertFalse(output.exists())

    def test_house_edition_can_pass_one_minute(self):
        with tempfile.TemporaryDirectory() as scratch:
            scenes, frames = self.plan_voice(Path(scratch), 65 * render.RATE, "house")
            self.assertGreater(frames, render.LIMIT)
            self.assertEqual(scenes[0]["frames"], frames)

    def test_one_extra_sample_rejects_the_edit_before_encoding(self):
        with tempfile.TemporaryDirectory() as scratch:
            directory = Path(scratch)
            samples = round((60 - render.LEAD - 1.8) * render.RATE) + 1
            with self.assertRaisesRegex(ValueError, "60 seconds or less"):
                self.plan_voice(directory, samples)
            self.assertFalse((directory / "voice.wav").exists())


if __name__ == "__main__":
    unittest.main()
