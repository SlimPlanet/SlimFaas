#!/usr/bin/env python3
"""Build a release bundle using only the existing SDK and Python standard library."""
import argparse
import hashlib
import os
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parent.parent
RIDS = ("linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64")


def publish(project, rid, destination, aot=False):
    if aot:
        # Build before MSBuild evaluates wwwroot globs on a fresh checkout.
        dashboard = REPOSITORY / "src/SlimFaas/ClientApp"
        npm = "npm.cmd" if os.name == "nt" else "npm"
        if not (dashboard / "node_modules").is_dir():
            subprocess.run([npm, "ci"], cwd=dashboard, check=True)
        subprocess.run([npm, "run", "build"], cwd=dashboard, check=True)
    subprocess.run([
        "dotnet", "publish", str(REPOSITORY / "src" / project / f"{project}.csproj"),
        "-c", "Release", "-r", rid, "-o", str(destination),
        "--self-contained", "true", "-p:UseAppHost=true",
        f"-p:PublishAot={str(aot).lower()}", f"-p:PublishTrimmed={str(aot).lower()}",
        "-p:DebugType=none", "-p:DebugSymbols=false", "-p:SkipClientAppBuild=true",
    ], cwd=REPOSITORY, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=RIDS)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, default=REPOSITORY / "artifacts/local-demo")
    parser.add_argument("--slimfaas-publish", type=Path, help="Reuse this native AOT publish directory")
    args = parser.parse_args()
    if not args.version or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-" for c in args.version):
        parser.error("version must be a release tag")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="slimfaas-bundle-") as temporary:
        bundle = Path(temporary)
        if args.slimfaas_publish:
            shutil.copytree(args.slimfaas_publish.resolve(), bundle / "runtime")
        else:
            publish("SlimFaas", args.rid, bundle / "runtime", aot=True)
        publish("Fibonacci", args.rid, bundle / "functions/fibonacci")
        publish("FibonacciBatch", args.rid, bundle / "jobs/fibonacci-batch")
        suffix = ".exe" if args.rid == "win-x64" else ""
        for executable in ("runtime/SlimFaas", "functions/fibonacci/Fibonacci", "jobs/fibonacci-batch/FibonacciBatch"):
            path = bundle / (executable + suffix)
            if not path.is_file():
                raise RuntimeError(f"Missing executable: {path}")
            path.chmod(path.stat().st_mode | 0o111)
        if not (bundle / "functions/fibonacci/dog.png").is_file():
            raise RuntimeError("Fibonacci download fixture dog.png is missing")
        if not (bundle / "runtime/wwwroot/index.html").is_file():
            raise RuntimeError("The native runtime must include the built dashboard")
        for source in (REPOSITORY / "demo/local-bundle").iterdir():
            if source.is_file():
                shutil.copy2(source, bundle / source.name)
        (bundle / "start.sh").chmod(0o755)
        shutil.copy2(REPOSITORY / "slimfaas.local.yaml", bundle / "slimfaas.local.yaml")
        shutil.copytree(REPOSITORY / "demo/bruno-slimfaas-demo", bundle / "demo/bruno-slimfaas-demo",
                        ignore=shutil.ignore_patterns("node_modules", ".DS_Store", "*.log"))
        shutil.copy2(REPOSITORY / "demo/smoke-tour.sh", bundle / "demo/smoke-tour.sh")
        shutil.copy2(REPOSITORY / "LICENSE.md", bundle / "LICENSE.md")
        (bundle / "bundle-version.txt").write_text(args.version + "\n", encoding="utf-8")
        (bundle / "bundle-rid.txt").write_text(args.rid + "\n", encoding="utf-8")
        archive = output / f"SlimFaas-Local-{args.rid}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zipped:
            for source in sorted(bundle.rglob("*")):
                if source.is_file():
                    zipped.write(source, source.relative_to(bundle).as_posix())
        with archive.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
        print(f"Local bundle: {archive}")


if __name__ == "__main__":
    main()
