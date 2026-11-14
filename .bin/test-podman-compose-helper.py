#!/usr/bin/env python3
"""Check argument forwarding and connection handling without a Podman VM."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class PodmanComposeHelperTests(unittest.TestCase):
    def run_helper(self, socket=None, exit_code=0):
        with tempfile.TemporaryDirectory() as directory:
            temporary = Path(directory)
            log = temporary / "calls.jsonl"
            podman = temporary / "podman"
            podman.write_text(
                "#!/usr/bin/env python3\n"
                "import json, os, sys\n"
                "with open(os.environ['PODMAN_TEST_LOG'], 'a') as log:\n"
                "    log.write(json.dumps({'args': sys.argv[1:], 'socket': os.environ.get('DOCKER_SOCKET_PATH'), "
                "'host': os.environ.get('DOCKER_HOST'), 'connection': os.environ.get('CONTAINER_CONNECTION')}) + '\\n')\n"
                "sys.exit(int(os.environ['PODMAN_TEST_EXIT']))\n",
                encoding="utf-8",
            )
            podman.chmod(0o755)
            environment = dict(os.environ)
            environment.pop("DOCKER_SOCKET_PATH", None)
            environment.update(
                PATH=directory + os.pathsep + environment["PATH"],
                PODMAN_TEST_LOG=str(log), PODMAN_TEST_EXIT=str(exit_code),
                CONTAINER_CONNECTION="my-custom-machine", PODMAN_LOG_LEVEL="error",
                DOCKER_HOST="unix:///host/socket", VERBOSE="0",
            )
            if socket is not None:
                environment["DOCKER_SOCKET_PATH"] = socket
            result = subprocess.run(
                ["bash", str(ROOT / "run-podman-compose.sh"), "-f", "path with spaces/compose.yml", "config"],
                env=environment, capture_output=True, text=True, check=False,
            )
            return result, [json.loads(line) for line in log.read_text().splitlines()]

    def test_active_connection_and_compose_arguments_are_preserved(self):
        result, calls = self.run_helper()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [{
            "args": ["--log-level=error", "compose", "-f", "path with spaces/compose.yml", "config"],
            "socket": "/run/docker.sock", "host": "unix:///var/run/docker.sock",
            "connection": "my-custom-machine",
        }])

    def test_custom_vm_socket_is_respected(self):
        result, calls = self.run_helper(socket="/run/user/1000/podman/podman.sock")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls[0]["socket"], "/run/user/1000/podman/podman.sock")

    def test_compose_failure_is_returned_to_caller(self):
        result, _ = self.run_helper(exit_code=42)
        self.assertEqual(result.returncode, 42)


if __name__ == "__main__":
    unittest.main()
