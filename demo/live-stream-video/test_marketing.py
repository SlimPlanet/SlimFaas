import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

from marketing import render


class MarketingTimelineTests(unittest.TestCase):
    def plan_voice(self, directory, samples):
        (directory / "scenes.json").write_text(json.dumps([{"key": "try", "voice": "Wake up. Back to sleep."}]))
        voice_dir = directory / "voice"
        voice_dir.mkdir()
        with wave.open(str(voice_dir / "try.wav"), "wb") as output:
            output.setparams((1, 2, render.RATE, 0, "NONE", "not compressed"))
            output.writeframes(bytes(2 * samples))
        with patch.object(render, "HERE", directory), patch.object(render, "WORK", directory):
            return render.plan(directory)

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

    def test_one_extra_sample_rejects_the_edit_before_encoding(self):
        with tempfile.TemporaryDirectory() as scratch:
            directory = Path(scratch)
            samples = round((60 - render.LEAD - 1.8) * render.RATE) + 1
            with self.assertRaisesRegex(ValueError, "60 seconds or less"):
                self.plan_voice(directory, samples)
            self.assertFalse((directory / "voice.wav").exists())


if __name__ == "__main__":
    unittest.main()
