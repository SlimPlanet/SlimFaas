#!/usr/bin/env python3
"""Verify that completed or terminated Windows benchmark hosts leave no children."""

import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import socket
import sys
import tempfile
import time
import unittest


@unittest.skipUnless(os.name == "nt", "Windows Job Object regression")
class BenchmarkHostTests(unittest.TestCase):
    def assert_stopped(self, pid):
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x100000, False, pid)
        if not handle:
            self.assertEqual(87, ctypes.get_last_error())  # Process no longer exists.
            return
        try:
            self.assertEqual(0, kernel.WaitForSingleObject(handle, 3000))
        finally:
            kernel.CloseHandle(handle)

    def exercise(self, terminate):
        helper = Path(__file__).with_name("run-local-benchmark-host.py")
        code = (
            "import json,os,subprocess,sys,time\n"
            "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'])\n"
            "with open(sys.argv[1]+'.tmp','w') as output: json.dump({'root':os.getpid(),'child':child.pid},output)\n"
            "os.replace(sys.argv[1]+'.tmp',sys.argv[1])\n"
            "if sys.argv[2]=='wait': time.sleep(30)\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            receipt = Path(directory) / "owned-pids.json"
            process = subprocess.Popen([sys.executable, str(helper), sys.executable, "-c", code,
                                        str(receipt), "wait" if terminate else "exit"])
            try:
                deadline = time.monotonic() + 10
                while not receipt.exists() and time.monotonic() < deadline:
                    time.sleep(.02)
                self.assertTrue(receipt.exists(), "Owned host failed to start")
                pids = json.loads(receipt.read_text())
                if terminate:
                    process.kill()
                code = process.wait(timeout=10)
                if not terminate:
                    self.assertEqual(0, code)
                for pid in pids.values():
                    self.assert_stopped(pid)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=10)

    def test_root_exit_closes_its_descendants(self):
        self.exercise(False)

    def test_abrupt_launcher_exit_closes_the_entire_job(self):
        self.exercise(True)

    def test_disk_reserve_failure_stops_the_owned_process_tree(self):
        helper = Path(__file__).with_name("run-local-benchmark-host.py")
        root_code = (
            "import json,os,subprocess,sys,time\n"
            "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'])\n"
            "with open(sys.argv[1]+'.tmp','w') as output: json.dump({'root':os.getpid(),'child':child.pid},output)\n"
            "os.replace(sys.argv[1]+'.tmp',sys.argv[1])\n"
            "time.sleep(30)\n"
        )
        launcher_code = (
            "import importlib.util,pathlib,sys,types\n"
            "spec=importlib.util.spec_from_file_location('host',sys.argv[1])\n"
            "host=importlib.util.module_from_spec(spec); spec.loader.exec_module(host)\n"
            "receipt=pathlib.Path(sys.argv[2]); original=host.shutil.disk_usage\n"
            "host.shutil.disk_usage=lambda directory: types.SimpleNamespace(free=0) if receipt.exists() else original(directory)\n"
            "raise SystemExit(host.windows_host(sys.argv[3:]))\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            receipt = Path(directory) / "owned-pids.json"
            result = subprocess.run([sys.executable, "-c", launcher_code, str(helper), str(receipt),
                sys.executable, "-c", root_code, str(receipt)], capture_output=True, text=True,
                timeout=15, check=False)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("disk reserve exhausted", result.stderr)
            self.assertTrue(receipt.exists())
            for pid in json.loads(receipt.read_text()).values():
                self.assert_stopped(pid)

    def test_insufficient_disk_reserve_rejects_the_host_before_startup(self):
        helper = Path(__file__).with_name("run-local-benchmark-host.py")
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "started"
            result = subprocess.run([sys.executable, str(helper), "--minimum-free-bytes", str(2 ** 64),
                sys.executable, "-c", "from pathlib import Path; import sys; Path(sys.argv[1]).touch()", str(marker)],
                capture_output=True, text=True, check=False)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("disk reserve exhausted", result.stderr)
            self.assertFalse(marker.exists())


class BenchmarkPortTests(unittest.TestCase):
    def test_occupied_port_is_rejected_before_starting_the_runtime(self):
        with socket.socket() as listener, tempfile.TemporaryDirectory() as directory:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            marker = Path(directory) / "started"
            helper = Path(__file__).with_name("run-local-benchmark-host.py")
            result = subprocess.run([sys.executable, str(helper), "--ports", str(listener.getsockname()[1]),
                sys.executable, "-c", "from pathlib import Path; import sys; Path(sys.argv[1]).touch()", str(marker)],
                capture_output=True, text=True, check=False)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("already has a listener", result.stderr)
            self.assertFalse(marker.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
