#!/usr/bin/env python3
"""Exercise the installer offline against fixture releases and simulated platforms."""
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parent.parent


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="SlimFaas installer tests ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.destination = self.root / "demo with spaces"
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                                FIXTURE_ROOT=str(self.root), FAKE_OS="Linux", FAKE_ARCH="x86_64")
        self.executable("uname", '#!/bin/sh\ncase "$1" in -s) echo "$FAKE_OS";; -m) echo "$FAKE_ARCH";; esac\n')
        self.executable("ldd", '#!/bin/sh\necho glibc\n')
        self.executable("cygpath", '#!/bin/sh\nprintf "%s\\n" "$2"\n')
        self.executable("curl", f"#!{sys.executable}\n" + '''import os, pathlib, shutil, sys
root = pathlib.Path(os.environ['FIXTURE_ROOT'])
args = sys.argv[1:]
url = args[-1]
with (root / 'requests.txt').open('a') as log: log.write(url + '\\n')
if url.endswith('/latest'):
    print('https://github.com/SlimPlanet/SlimFaas/releases/tag/test-release', end='')
    sys.exit(0)
source = root / url.rsplit('/', 1)[1]
if not source.exists(): sys.exit(22)
shutil.copyfile(source, args[args.index('--output') + 1])
''')

    def executable(self, name, content):
        path = self.bin / name
        path.write_text(content)
        path.chmod(0o755)

    def release(self, rid="linux-x64", bad_checksum=False, invalid_zip=False, traversal=False):
        asset = self.root / f"SlimFaas-Local-{rid}.zip"
        if invalid_zip:
            asset.write_bytes(b"not a zip")
        else:
            suffix = ".exe" if rid == "win-x64" else ""
            with zipfile.ZipFile(asset, "w") as archive:
                archive.writestr("bundle-version.txt", "test-release\n")
                archive.writestr("bundle-rid.txt", rid + "\n")
                archive.write(REPOSITORY / "demo/local-bundle/start.sh", "start.sh")
                archive.writestr("slimfaas.local.yaml", "")
                archive.writestr("slimfaas.local.prebuilt.yaml", "")
                for executable in ("runtime/SlimFaas", "functions/fibonacci/Fibonacci", "jobs/fibonacci-batch/FibonacciBatch"):
                    info = zipfile.ZipInfo(executable + suffix)
                    info.external_attr = 0o100755 << 16
                    archive.writestr(info, '#!/bin/sh\n[ "$1" = local ] && [ "$2" = validate ]\n')
                if traversal:
                    archive.writestr("../outside.txt", "should never be extracted")
        checksum = "0" * 64 if bad_checksum else hashlib.sha256(asset.read_bytes()).hexdigest()
        asset.with_suffix(".zip.sha256").write_text(f"{checksum}  {asset.name}\n")

    def run_installer(self, *extra):
        return subprocess.run(["sh", str(REPOSITORY / ".bin/install-local-demo.sh"), "--directory", str(self.destination), *extra],
                              env=self.environment, text=True, capture_output=True)

    def test_platforms_and_latest_pinning(self):
        platforms = [("Linux", "x86_64", "linux-x64"), ("Linux", "aarch64", "linux-arm64"),
                     ("Darwin", "x86_64", "osx-x64"), ("Darwin", "arm64", "osx-arm64"),
                     ("MINGW64_NT-10.0", "x86_64", "win-x64")]
        for system, arch, rid in platforms:
            with self.subTest(rid=rid):
                self.environment.update(FAKE_OS=system, FAKE_ARCH=arch)
                self.destination = self.root / f"demo with spaces {rid}"
                self.release(rid)
                result = self.run_installer()
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual((self.destination / "bundle-rid.txt").read_text().strip(), rid)
        requests = (self.root / "requests.txt").read_text().splitlines()
        self.assertEqual(sum(url.endswith("/latest") for url in requests), 5)
        self.assertTrue(all('/download/test-release/' in url for url in requests if not url.endswith('/latest')))

    def test_reinstall_preserves_state_and_modified_files(self):
        self.release()
        result = self.run_installer("--version", "test-release")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        (self.destination / ".slimfaas").mkdir()
        state = self.destination / ".slimfaas/state"
        state.write_text("keep this state")
        manifest = self.destination / "slimfaas.local.yaml"
        manifest.write_text("user configuration")
        result = self.run_installer("--version", "test-release")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(state.read_text(), "keep this state")
        self.assertEqual(manifest.read_text(), "user configuration")
        self.assertEqual(len((self.root / "requests.txt").read_text().splitlines()), 2)

    def test_existing_destination_is_never_overwritten(self):
        self.destination.mkdir()
        marker = self.destination / "mine.txt"
        marker.write_text("mine")
        result = self.run_installer("--version", "test-release")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(marker.read_text(), "mine")

    def test_checksum_mismatch(self):
        self.release(bad_checksum=True)
        result = self.run_installer()
        self.assertIn("SHA-256 mismatch", result.stderr)
        self.assertFalse(self.destination.exists())
        self.assertEqual(list(self.root.glob('.slimfaas-install.*')), [])

    def test_missing_bundle(self):
        result = self.run_installer()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("no downloadable local bundle", result.stderr)
        self.assertFalse(self.destination.exists())

    def test_invalid_zip_and_path_traversal(self):
        for flags in ({"invalid_zip": True}, {"traversal": True}):
            with self.subTest(flags=flags):
                self.release(**flags)
                result = self.run_installer()
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.destination.exists())
                self.assertFalse((self.root / "outside.txt").exists())

    def test_unsupported_platform_and_invalid_options(self):
        self.environment['FAKE_ARCH'] = 'riscv64'
        self.assertIn("Unsupported platform", self.run_installer().stderr)
        self.assertIn("Invalid release tag", self.run_installer("--version", "../main").stderr)
        self.assertIn("Missing value", self.run_installer("--version").stderr)

    def test_musl_is_reported(self):
        self.executable("ldd", '#!/bin/sh\necho musl >&2\n')
        self.assertIn("require glibc", self.run_installer().stderr)

    def test_launcher_validates_before_up_and_keeps_clean_out_of_validation(self):
        self.release()
        result = self.run_installer()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        runtime = self.destination / "runtime/SlimFaas"
        runtime.write_text(f"#!{sys.executable}\n" + '''import json, os, pathlib, sys
with (pathlib.Path(os.environ['FIXTURE_ROOT']) / 'launches.jsonl').open('a') as log:
    log.write(json.dumps(sys.argv[1:]) + '\\n')
if sys.argv[2] == 'validate' and os.environ.get('INVALID_MANIFEST') == 'true':
    sys.exit(9)
''')
        manifests = ["-f", "slimfaas.local.yaml", "-f", "slimfaas.local.prebuilt.yaml"]
        overlay = ["-f", "extra overlay.yaml", "--env-file", "demo env.txt"]
        cases = [
            (overlay, False, [["local", "validate", *manifests, *overlay], ["local", "up", *manifests, *overlay]]),
            (["--clean", *overlay], False, [["local", "validate", *manifests, *overlay], ["local", "up", *manifests, *overlay, "--clean"]]),
            (["--validate", *overlay], False, [["local", "validate", *manifests, *overlay]]),
            (["--clean", *overlay], True, [["local", "validate", *manifests, *overlay]])
        ]
        log = self.root / "launches.jsonl"
        for arguments, invalid, expected in cases:
            with self.subTest(arguments=arguments, invalid=invalid):
                log.write_text("")
                result = subprocess.run(["sh", str(self.destination / "start.sh"), *arguments],
                                        env=dict(self.environment, INVALID_MANIFEST=str(invalid).lower()),
                                        cwd=self.root, text=True, capture_output=True)
                self.assertEqual(result.returncode, 9 if invalid else 0, result.stdout + result.stderr)
                self.assertEqual([json.loads(line) for line in log.read_text().splitlines()], expected)


if __name__ == "__main__":
    unittest.main(verbosity=2)
