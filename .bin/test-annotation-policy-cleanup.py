#!/usr/bin/env python3
"""Check that test-annotation-policy.py never deletes a kind cluster it did not create."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parent / "test-annotation-policy.py"


def load():
    spec = importlib.util.spec_from_file_location("test_annotation_policy", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class FakeSubprocess:
    def __init__(self, clusters="", create_fails=False):
        self.clusters = clusters
        self.create_fails = create_fails
        self.calls = []

    def run(self, command, **kwargs):
        self.calls.append(command)
        if command[:3] == ["kind", "get", "clusters"]:
            return subprocess.CompletedProcess(command, 0, self.clusters, "")
        if command[:3] == ["kind", "create", "cluster"] and self.create_fails:
            raise subprocess.CalledProcessError(1, command)
        return subprocess.CompletedProcess(command, 0, "", "")

    def deleted(self):
        return [command for command in self.calls if command[:3] == ["kind", "delete", "cluster"]]


class CleanupTests(unittest.TestCase):
    def run_main(self, fake, *argv):
        module = load()
        with mock.patch.object(module.subprocess, "run", fake.run), \
                mock.patch.object(module, "kubectl", side_effect=RuntimeError("stop")), \
                mock.patch.object(sys, "argv", ["test-annotation-policy.py", *argv]):
            with self.assertRaises((SystemExit, RuntimeError, subprocess.CalledProcessError)):
                module.main()

    def test_existing_cluster_is_rejected_and_kept(self):
        fake = FakeSubprocess(clusters="slimfaas-annotation-policy\n")
        self.run_main(fake)
        self.assertEqual([], fake.deleted())
        self.assertFalse([c for c in fake.calls if c[:3] == ["kind", "create", "cluster"]])

    def test_cluster_is_kept_when_creation_fails(self):
        # Another run can create the same name between the preflight and the create.
        fake = FakeSubprocess(clusters="other\n", create_fails=True)
        self.run_main(fake)
        self.assertEqual([], fake.deleted())

    def test_created_cluster_is_deleted_after_a_failure(self):
        fake = FakeSubprocess()
        self.run_main(fake)
        self.assertEqual([["kind", "delete", "cluster", "--name", "slimfaas-annotation-policy"]], fake.deleted())

    def test_keep_cluster_skips_delete(self):
        fake = FakeSubprocess()
        self.run_main(fake, "--keep-cluster")
        self.assertEqual([], fake.deleted())


if __name__ == "__main__":
    unittest.main()
