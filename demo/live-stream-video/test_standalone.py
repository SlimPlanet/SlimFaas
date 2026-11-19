"""Exercise the film's browser dependency and bundled artwork outside the repository."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

PROJECT = Path(__file__).resolve().parent


class StandaloneFilmTests(unittest.TestCase):
    def test_render_one_frame_without_a_dashboard_checkout(self):
        with tempfile.TemporaryDirectory() as scratch:
            project = Path(scratch) / "film"
            editor = project / "marketing"
            editor.mkdir(parents=True)
            for name in ("frames.mjs", "board.html", "slimfaas.svg"):
                shutil.copy(PROJECT / "marketing" / name, editor)
            # Copy just the installed renderer so the fixture has no link back to the checkout.
            shutil.copytree(PROJECT / "node_modules/playwright-core", project / "node_modules/playwright-core")
            scenes = json.loads((PROJECT / "marketing/scenes.json").read_text())
            scene = next(scene for scene in scenes if scene["key"] == "cost")
            scene.update(frames=1, cues=[])
            plan = {"fps": 24, "scenes": [scene], "observed": json.loads((PROJECT / "observed.json").read_text())}
            (project / "plan.json").write_text(json.dumps(plan))
            subprocess.run(["node", editor / "frames.mjs", project], cwd=scratch, check=True)
            self.assertTrue((project / "frames/00000.jpg").read_bytes().startswith(b"\xff\xd8"))
