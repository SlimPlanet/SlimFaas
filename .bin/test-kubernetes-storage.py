"""Bootstrap must survive data-volume loss without reinitializing a cluster."""

from pathlib import Path
import os
import shutil
import tempfile
import unittest

from slimdata_kubernetes_storage import initialize_storage, prepare_control


@unittest.skipUnless(os.name == "posix", "The rootless kind fixture requires Unix directory fsync")
class StorageTests(unittest.TestCase):
    namespace = "slimfaas-pr506-storage-test"
    source = "a" * 40

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base / "data"
        self.root.mkdir()
        self.control = self.base / self.namespace / "control"
        prepare_control(self.control, self.namespace, self.source)

    def initialize(self, size=65536):
        return initialize_storage(self.root, self.control, "slimfaas-0", size, self.namespace, self.source)

    def test_fresh_permission_is_consumed_once_and_replay_preserves_original_geometry(self):
        self.assertTrue(self.initialize()["fresh_bootstrap_permission"])
        (self.root / ".profile-bootstrap-once").rename(self.root / ".profile-bootstrap-consumed")
        consumed = (self.control / "bootstrap-consumed.json").read_bytes()
        self.assertFalse(self.initialize()["fresh_bootstrap_permission"])
        self.assertEqual(consumed, (self.control / "bootstrap-consumed.json").read_bytes())
        self.assertFalse((self.root / ".profile-bootstrap-once").exists())

    def test_permanent_data_volume_loss_cannot_recreate_bootstrap_permission(self):
        self.initialize()
        shutil.rmtree(self.root)
        self.root.mkdir()
        self.assertFalse(self.initialize()["fresh_bootstrap_permission"])
        self.assertFalse((self.root / ".profile-bootstrap-once").exists())

    def test_existing_unmarked_wal_is_never_reinterpreted(self):
        wal = self.root / "slimfaas-0/wal"
        wal.mkdir(parents=True)
        (wal / "original-entry").write_bytes(b"original")
        with self.assertRaises(ValueError):
            self.initialize()
        self.assertEqual(b"original", (wal / "original-entry").read_bytes())
        self.assertFalse((wal / "slimdata-chunk-size").exists())

    def test_conflicting_geometry_does_not_rewrite_the_marker(self):
        self.initialize()
        with self.assertRaises(ValueError):
            self.initialize(4096)
        self.assertEqual(b"65536", (self.root / "slimfaas-0/wal/slimdata-chunk-size").read_bytes())

    def test_lost_wal_with_retained_snapshot_fails_closed(self):
        node = self.root / "slimfaas-0"
        node.mkdir()
        (node / "snapshot").write_bytes(b"retained")
        with self.assertRaises(ValueError):
            self.initialize()
        self.assertFalse((node / "wal").exists())

    def test_missing_control_proof_never_creates_a_wal(self):
        (self.control / "fresh-campaign.json").unlink()
        with self.assertRaises(FileNotFoundError):
            self.initialize()
        self.assertFalse((self.root / "slimfaas-0").exists())

    def test_control_loss_with_existing_wal_does_not_issue_another_permission(self):
        self.initialize()
        (self.root / ".profile-bootstrap-once").rename(self.root / ".profile-bootstrap-consumed")
        (self.control / "bootstrap-consumed.json").unlink()
        with self.assertRaises(ValueError):
            self.initialize()
        self.assertFalse((self.root / ".profile-bootstrap-once").exists())


if __name__ == "__main__":
    unittest.main()
