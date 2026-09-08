#!/usr/bin/env python3
"""Smoke a real extracted bundle with SDKs absent from the runtime PATH."""
import argparse
import json
import os
import signal
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="SlimFaas bundle with spaces ") as temporary:
        root = Path(temporary)
        with zipfile.ZipFile(args.archive) as archive:
            archive.extractall(root)
        suffix = ".exe" if os.name == "nt" else ""
        for executable in ("runtime/SlimFaas", "functions/fibonacci/Fibonacci", "jobs/fibonacci-batch/FibonacciBatch"):
            path = root / (executable + suffix)
            path.chmod(path.stat().st_mode | 0o111)
        # Native processes do not need any executable from PATH. Keep platform
        # environment (SystemRoot, TEMP, etc.), but remove SDK discovery settings.
        environment = {key: value for key, value in os.environ.items()
                       if not key.startswith(("DOTNET", "NODE")) and key != "PATH"}
        environment.update(PATH="", SLIMFAAS_DEMO_ROOT=root.as_posix(), SLIMFAAS_DEMO_EXE_SUFFIX=suffix)
        command = [str(root / ("runtime/SlimFaas" + suffix)), "local"]
        manifests = ["-f", str(root / "slimfaas.local.yaml"), "-f", str(root / "slimfaas.local.prebuilt.yaml")]
        subprocess.run(command + ["validate"] + manifests, env=environment, cwd=root, check=True)
        # Exercise the shipped launchers as well as the direct empty-PATH run.
        if os.name == "nt":
            powershell = shutil.which("pwsh") or shutil.which("powershell")
            if not powershell:
                raise RuntimeError("PowerShell is required to test the Windows launcher")
            launcher = [powershell, "-NoProfile", "-File", str(root / "start.ps1"), "-Validate"]
        else:
            launcher = ["/bin/sh", str(root / "start.sh"), "--validate"]
        subprocess.run(launcher, cwd=root.parent, check=True)
        options = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
        log_path = root / "smoke.log"
        with log_path.open("w", encoding="utf-8") as log:
            process = subprocess.Popen(command + ["up"] + manifests, env=environment, cwd=root,
                                       stdout=log, stderr=subprocess.STDOUT, **options)
            try:
                def request(route, method="GET", data=None):
                    payload = None if data is None else json.dumps(data).encode()
                    req = urllib.request.Request("http://127.0.0.1:30020" + route, data=payload, method=method,
                                                 headers={"Content-Type": "application/json"})
                    with urllib.request.urlopen(req, timeout=60) as response:
                        return response.status, response.read()

                deadline = time.monotonic() + 120
                while True:
                    if process.poll() is not None:
                        raise RuntimeError("Local supervisor exited before readiness")
                    try:
                        if request("/ready")[0] == 200:
                            break
                    except (urllib.error.URLError, TimeoutError):
                        pass
                    if time.monotonic() > deadline:
                        raise TimeoutError("Local bundle never became ready")
                    time.sleep(.25)
                assert b"<html" in request("/")[1].lower(), "Dashboard missing"
                status = json.loads(request("/status-functions")[1])
                assert len(status) == 4, status
                assert request("/function/fibonacci1/hello/bundle")[1] == b"Hello bundle!"
                assert json.loads(request("/function/fibonacci1/fibonacci", "POST", {"Input": 10})[1])["result"] == 55
                assert request("/function/fibonacci1/download")[1].startswith(b"\x89PNG"), "Download fixture missing"
                job = json.loads(request("/job/fibonacci", "POST", {"Args": ["10"], "TtlSecondsAfterFinished": 60})[1])
                deadline = time.monotonic() + 60
                while True:
                    jobs = json.loads(request("/job/fibonacci")[1])
                    matching = [item for item in jobs if item["Id"] == job["Id"]]
                    if matching and matching[0]["Status"] == "Succeeded":
                        break
                    if time.monotonic() > deadline:
                        raise TimeoutError(f"Packaged job did not succeed: {jobs}")
                    time.sleep(.25)
                request("/job/fibonacci/" + job["Id"], "DELETE")
                print("Bundle passed: empty PATH, three-node startup, dashboard, four functions, sync, file, job.")
            except BaseException:
                print(log_path.read_text(encoding="utf-8", errors="replace")[-14000:])
                raise
            finally:
                if process.poll() is None:
                    process.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGTERM)
                    try:
                        process.wait(timeout=45)
                    except subprocess.TimeoutExpired:
                        if os.name == "nt":
                            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False)
                        else:
                            os.killpg(process.pid, signal.SIGKILL)
                        process.wait()


if __name__ == "__main__":
    main()
