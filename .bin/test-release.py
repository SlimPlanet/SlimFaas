#!/usr/bin/env python3
"""Release failure injection tests. No credentials, network, or registries required."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import zipfile

import release_lib as lib

spec = importlib.util.spec_from_file_location("release", Path(__file__).with_name("release.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
SOURCE = "a" * 40
VERSION = "0.85.0-alpha.42"
PRODUCTS = {"native": [{"product": "SlimFaas", "rid": "linux-x64"}],
            "images": [{"name": "slimfaas", "platforms": "linux/amd64,linux/arm64"}]}


def zip_bytes(entries):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name, value in entries.items():
            archive.writestr(name, value)
    return output.getvalue()


def image_fixture(path, source=SOURCE):
    blobs = {}

    def descriptor(value):
        data = json.dumps(value).encode()
        digest = hashlib.sha256(data).hexdigest()
        blobs["blobs/sha256/" + digest] = data
        return {"digest": "sha256:" + digest, "size": len(data)}

    children = []
    for arch in ("amd64", "arm64", "unknown"):
        config = descriptor({"os": "unknown" if arch == "unknown" else "linux", "architecture": arch,
                             "config": {"Labels": {"org.opencontainers.image.revision": source,
                                                   "org.opencontainers.image.version": VERSION}}})
        children.append(descriptor({"schemaVersion": 2, "config": config, "layers": []}))
    root = descriptor({"schemaVersion": 2, "manifests": children})
    blobs["index.json"] = json.dumps({"schemaVersion": 2, "manifests": [root]}).encode()
    with tarfile.open(path, "w") as archive:
        for name, data in blobs.items():
            item = tarfile.TarInfo(name)
            item.size = len(data)
            archive.addfile(item, io.BytesIO(data))


class Versions(unittest.TestCase):
    def test_conventional_commits_and_breaking_changes(self):
        for commits, expected in [(["fix: correct routing"], "0.84.17"),
                                  (["feat(api): stream"], "0.85.0"),
                                  (["fix!: remove old API"], "1.0.0"),
                                  (["feat: new API\n\nBREAKING CHANGE: removed v1"], "1.0.0")]:
            with self.subTest(commits=commits):
                self.assertEqual(lib.next_version("0.84.16", commits), expected)

    def test_free_text_cannot_select_a_channel_or_bump(self):
        self.assertEqual(lib.next_version("0.84.16", ["docs: explain release alpha beta"]), "0.84.17")
        with self.assertRaisesRegex(ValueError, "Release-As"):
            lib.next_version("0.84.16", ["docs: notes\nRelease-As: 10.0.0"])

    def test_pep440(self):
        for version, expected in [(VERSION, "0.85.0a42"), ("1.0.0-beta.7", "1.0.0b7"),
                                  ("1.0.0-dev.1", "1.0.0.dev1"), ("1.0.0", "1.0.0")]:
            self.assertEqual(lib.python_version(version), expected)

    def test_reject_aliases_and_shell_paths(self):
        for version in ["v0.84.16", "../0.84.16", "0.84.016", "1.0.0-alpha", "1.0.0+sha", "1.0.0\n"]:
            with self.subTest(version=version), self.assertRaises(ValueError):
                lib.version_parts(version)


class Inventory(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.catalog = patch.object(lib, "products", return_value=PRODUCTS)
        self.catalog.start()
        self.addCleanup(self.catalog.stop)
        for name in lib.payload_names(VERSION):
            (self.directory / name).write_bytes(b"built and tested artifact")
            for sbom in lib.sbom_names(name):
                (self.directory / sbom).write_text('{"test": true}')
        image_fixture(self.directory / "slimfaas.oci.tar")
        self.manifest = lib.make_inventory(self.directory, VERSION, SOURCE, "alpha")

    def test_complete_candidate(self):
        self.assertEqual(lib.verify_inventory(self.directory, VERSION, SOURCE), self.manifest)

    def test_missing_or_corrupt_artifact_is_fatal(self):
        path = self.directory / "SlimFaas-linux-x64.zip"
        path.write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "checksum"):
            lib.verify_inventory(self.directory, VERSION, SOURCE)
        path.unlink()
        with self.assertRaisesRegex(ValueError, "checksum"):
            lib.verify_inventory(self.directory, VERSION, SOURCE)

    def test_corrupt_index_and_wrong_source_are_fatal(self):
        with self.assertRaisesRegex(ValueError, "source SHA"):
            lib.verify_inventory(self.directory, VERSION, "b" * 40)
        (self.directory / "SHA256SUMS").write_text("")
        with self.assertRaisesRegex(ValueError, "Checksum index"):
            lib.verify_inventory(self.directory, VERSION, SOURCE)

    def test_unlisted_files_and_absent_sbom_are_fatal(self):
        extra = self.directory / "unexpected.exe"
        extra.touch()
        with self.assertRaisesRegex(ValueError, "Unlisted"):
            lib.verify_inventory(self.directory, VERSION, SOURCE)
        extra.unlink()
        next(self.directory.glob("*.sbom.spdx.json")).unlink()
        with self.assertRaisesRegex(ValueError, "Missing SBOM"):
            lib.make_inventory(self.directory, VERSION, SOURCE, "alpha")

    def test_wrong_image_source_is_fatal(self):
        image_fixture(self.directory / "slimfaas.oci.tar", source="b" * 40)
        with self.assertRaisesRegex(ValueError, "OCI source/version"):
            lib.make_inventory(self.directory, VERSION, SOURCE, "alpha")

    def test_identity_failure_prevents_any_publication(self):
        failure = subprocess.CalledProcessError(1, "cosign")
        with patch.object(release, "run", side_effect=failure) as command:
            with self.assertRaises(subprocess.CalledProcessError):
                release.verify(self.directory, VERSION, SOURCE)
            self.assertEqual(command.call_count, 1)
            self.assertIn("--certificate-oidc-issuer", command.call_args.args)

    def test_provenance_requires_exact_source_and_workflow(self):
        with patch.object(release, "run") as command:
            release.verify(self.directory, VERSION, SOURCE)
        invocation = command.call_args.args
        self.assertIn(SOURCE, invocation)
        self.assertIn("--signer-workflow", invocation)
        self.assertIn("--deny-self-hosted-runners", invocation)


class CollisionAndResume(unittest.TestCase):
    def test_package_collision_and_nuget_repository_signature(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "client.nupkg"
            original = {"lib/client.dll": b"the binary", "client.nuspec": b"version metadata"}
            path.write_bytes(zip_bytes(original))
            self.assertFalse(lib.check_published_file(path, None))
            self.assertTrue(lib.check_published_file(path, zip_bytes(dict(original, **{".signature.p7s": b"registry signature"})), nuget=True))
            with self.assertRaisesRegex(ValueError, "collision"):
                lib.check_published_file(path, zip_bytes({"lib/client.dll": b"different"}), nuget=True)
            with self.assertRaisesRegex(ValueError, "collision"):
                lib.check_published_file(path, b"different")

    def test_only_404_means_absent(self):
        for code in (401, 403, 429, 500, 503):
            with patch("urllib.request.urlopen", side_effect=urllib.error.HTTPError("url", code, "failure", {}, None)):
                with self.assertRaises(urllib.error.HTTPError):
                    lib.download("https://example.test/package")
        with patch("urllib.request.urlopen", side_effect=urllib.error.HTTPError("url", 404, "missing", {}, None)):
            self.assertIsNone(lib.download("https://example.test/package"))

    def test_zip_path_traversal(self):
        with tempfile.TemporaryDirectory() as temporary:
            for name in ("../escape", "/escape", "C:\\escape", "a\\..\\escape"):
                with self.subTest(name=name), self.assertRaises(ValueError):
                    lib.safe_extract_zip(io.BytesIO(zip_bytes({name: b"unsafe"})), temporary)

    def test_resume_only_failed_jobs_of_original_run(self):
        original = {"head_sha": SOURCE, "head_repository": {"full_name": lib.REPOSITORY},
                    "path": ".github/workflows/main.yml", "status": "completed", "conclusion": "failure"}
        with patch.object(release, "gh", return_value=original), patch.object(release, "run") as command:
            release.resume_run({"run_id": "123", "source_sha": SOURCE})
            self.assertEqual(command.call_count, 1)
            self.assertTrue(command.call_args.args[-1].endswith("/123/rerun-failed-jobs"))
            original["head_sha"] = "b" * 40
            with self.assertRaisesRegex(ValueError, "source"):
                release.resume_run({"run_id": "123", "source_sha": SOURCE})
            self.assertEqual(command.call_count, 1)

    def test_preview_cannot_run_from_fork_or_branch(self):
        for repository, ref in [("someone/SlimFaas", "refs/heads/main"), (lib.REPOSITORY, "refs/heads/topic")]:
            with patch.dict(os.environ, GITHUB_SHA=SOURCE, GITHUB_REPOSITORY=repository, GITHUB_REF=ref, RELEASES_ENABLED="true"):
                with self.assertRaisesRegex(ValueError, "main workflow"):
                    release.prepare("alpha", "")

    def test_failed_registry_verification_never_promotes_channels(self):
        with patch.object(release, "registry_files", side_effect=ValueError("collision")), patch.object(release, "run") as command:
            with self.assertRaisesRegex(ValueError, "collision"):
                release.finalize(Path("unused"), {})
            command.assert_not_called()

    def test_historical_v_tag_cannot_replace_canonical_tag(self):
        with patch.object(release, "run", return_value="b" * 40) as command:
            with self.assertRaisesRegex(ValueError, "source"):
                release.check_tag("0.84.16", SOURCE)
            self.assertIn("refs/tags/0.84.16^{commit}", command.call_args.args)


if __name__ == "__main__":
    unittest.main(verbosity=2)
