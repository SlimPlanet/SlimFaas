#!/usr/bin/env python3
"""Build and inspect release artifacts without registry or GitHub write access."""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import tarfile
import tempfile
import zipfile

from release_lib import ROOT, SHA, emit, products, python_version, release_directory, run, safe_extract_zip, version_parts

NATIVE_EVIDENCE = {
    "SlimFaas": ["src/SlimFaas/packages.lock.json", "src/SlimData/packages.lock.json", "src/SlimFaas/ClientApp/package-lock.json"],
    "SlimFaasMcp": ["src/SlimFaasMcp/packages.lock.json", "src/SlimFaasMcp/ClientApp/package-lock.json"],
}
LOCAL_EVIDENCE = NATIVE_EVIDENCE["SlimFaas"] + [
    "samples/Fibonacci/packages.lock.json", "samples/FibonacciBatch/packages.lock.json",
    "client/dotnet/SlimFaasClient/src/SlimFaasClient/packages.lock.json",
]


def evidence(artifact, paths, version, source_sha):
    with zipfile.ZipFile(str(artifact) + ".dependencies.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        for relative in paths:
            path = ROOT / relative
            if not path.is_file():
                raise ValueError(f"Resolved dependency evidence missing: {relative}")
            archive.write(path, relative)
        archive.writestr("build-context.json", json.dumps({"version": version, "source_sha": source_sha,
            "scope": "Resolved application dependencies and frontend build inputs; includes build-only dependencies."}))


def archive_directory(directory, target):
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(directory.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(directory).as_posix())


def native(product, rid, version, source_sha, output):
    node = "npm.cmd" if os.name == "nt" else "npm"
    ui = ROOT / "src" / product / "ClientApp"
    run(node, "ci", cwd=ui)
    run(node, "run", "build", cwd=ui)
    publish = ROOT / "artifacts" / "native" / f"{product}-{rid}"
    run("dotnet", "publish", f"src/{product}/{product}.csproj", "-c", "Release", "-r", rid,
        "-o", publish, "-p:PublishAot=true", "-p:PublishTrimmed=true", "-p:SelfContained=true",
        "-p:DebugType=none", "-p:DebugSymbols=false", "-p:SkipClientAppBuild=true",
        "-p:RestorePackagesWithLockFile=true", f"-p:Version={version}", f"-p:SourceRevisionId={source_sha}")
    archive = output / f"{product}-{rid}.zip"
    archive_directory(publish, archive)
    evidence(archive, NATIVE_EVIDENCE[product], version, source_sha)
    if product == "SlimFaas":
        executable = publish / ("SlimFaas.exe" if os.name == "nt" else "SlimFaas")
        run(executable, "local", "validate", "-f", "slimfaas.local.yaml", "-f", "slimfaas.local.dev.yaml", "--env-file", ".env.local.example")
        run(sys.executable, ".bin/package-local-demo.py", "--rid", rid, "--version", version,
            "--slimfaas-publish", publish, "--output", output)
        local = output / f"SlimFaas-Local-{rid}.zip"
        evidence(local, LOCAL_EVIDENCE, version, source_sha)
        run(sys.executable, ".bin/test-local-bundle.py", local)
        if os.name != "nt":
            run(sys.executable, ".bin/test-install-local-demo.py")


def stamp(version):
    """Stage a preview's package metadata; never commit or push build-time edits."""
    version_parts(version)
    package = ROOT / "src/SlimFaasPlanetSaver/package.json"
    data = json.loads(package.read_text())
    data["version"] = version
    package.write_text(json.dumps(data, indent=2) + "\n")
    for filename in ("pyproject.toml", "uv.lock"):
        path = ROOT / "client/python/slimfaas-client" / filename
        lines = path.read_text().splitlines()
        matches = [index for index, line in enumerate(lines) if "# x-release-please-version" in line]
        if len(matches) != 1:
            raise ValueError(f"Expected exactly one version marker in {filename}")
        lines[matches[0]] = f'version = "{python_version(version)}" # x-release-please-version'
        path.write_text("\n".join(lines) + "\n")


def packages(version, source_sha, output):
    stamp(version)
    planet = ROOT / "src/SlimFaasPlanetSaver"
    run("pnpm", "install", "--frozen-lockfile", cwd=planet)
    run("pnpm", "run", "coverage", cwd=planet)
    packed = json.loads(run("npm", "pack", "--json", "--pack-destination", output, cwd=planet, capture=True))[0]
    npm = output / f"slimfaas-planet-saver-{version}.tgz"
    (output / packed["filename"]).rename(npm)
    evidence(npm, ["src/SlimFaasPlanetSaver/pnpm-lock.yaml"], version, source_sha)
    client = "client/dotnet/SlimFaasClient"
    run("dotnet", "test", f"{client}/tests/SlimFaasClient.Tests/SlimFaasClient.Tests.csproj", "-c", "Release", f"-p:Version={version}")
    run("dotnet", "pack", f"{client}/src/SlimFaasClient/SlimFaasClient.csproj", "-c", "Release", "-o", output,
        "-p:RestorePackagesWithLockFile=true", f"-p:Version={version}", f"-p:SourceRevisionId={source_sha}")
    for suffix in ("nupkg", "snupkg"):
        evidence(output / f"SlimFaasClient.{version}.{suffix}", [f"{client}/src/SlimFaasClient/packages.lock.json"], version, source_sha)
    python = ROOT / "client/python/slimfaas-client"
    run("uv", "sync", "--frozen", "--extra", "dev", cwd=python)
    run("uv", "run", "--frozen", "pytest", "--tb=short", "-q", cwd=python)
    run("uv", "build", "--out-dir", output, cwd=python)
    for name in (f"slimfaas_client-{python_version(version)}-py3-none-any.whl", f"slimfaas_client-{python_version(version)}.tar.gz"):
        evidence(output / name, ["client/python/slimfaas-client/uv.lock"], version, source_sha)
    run(sys.executable, ".bin/test-release-packages.py", "--directory", output, "--version", version)


def sbom(directory, version):
    """Scan both the distribution and its exact restore graph, including AOT inputs."""
    from release_lib import payload_names
    for name in payload_names(version):
        artifact = directory / name
        if name.endswith(".oci.tar"):
            item = next(x for x in products()["images"] if name == x["name"] + ".oci.tar")
            for platform in item["platforms"].split(","):
                prefix = str(artifact) + "." + platform.replace("/", "-")
                run("syft", "scan", "oci-archive:" + str(artifact), "--platform", platform,
                    "--select-catalogers", "+dotnet-packages-lock-cataloger,+javascript-lock-cataloger,+python-package-cataloger", "--source-name", item["name"], "--source-version", version,
                    "-o", "spdx-json=" + prefix + ".sbom.spdx.json", "-o", "cyclonedx-json=" + prefix + ".sbom.cdx.json")
        else:
            with tempfile.TemporaryDirectory(prefix="slimfaas-sbom-") as temporary:
                stage = Path(temporary)
                safe_extract_zip(str(artifact) + ".dependencies.zip", stage / "dependencies")
                if name.endswith((".zip", ".whl", ".nupkg", ".snupkg")):
                    safe_extract_zip(artifact, stage / "distribution")
                else:
                    with tarfile.open(artifact) as archive:
                        archive.extractall(stage / "distribution", filter="data")
                run("syft", "scan", "dir:" + str(stage), "--select-catalogers", "+dotnet-packages-lock-cataloger,+javascript-lock-cataloger,+python-package-cataloger",
                    "--source-name", name, "--source-version", version,
                    "-o", f"spdx-json={artifact}.sbom.spdx.json", "-o", f"cyclonedx-json={artifact}.sbom.cdx.json")
    # Fail closed when a source graph was silently dropped by a scanner change.
    for path in directory.glob("*.sbom.spdx.json"):
        document = json.loads(path.read_text())
        if not document.get("spdxVersion", "").startswith("SPDX-2.") or not document.get("packages"):
            raise ValueError(f"Empty or invalid SPDX SBOM: {path.name}")
    for path in directory.glob("*.sbom.cdx.json"):
        document = json.loads(path.read_text())
        if document.get("bomFormat") != "CycloneDX" or not document.get("components"):
            raise ValueError(f"Empty or invalid SBOM: {path.name}")
        components = document["components"]
        dotnet_images = tuple(item["name"] + ".oci.tar." for item in products()["images"]
                              if item["name"] != "fibonacci-webapp")
        if path.name.startswith(("SlimFaas-", "SlimFaasMcp-", "SlimFaasClient.") + dotnet_images):
            if not any(x.get("purl", "").startswith("pkg:nuget/") for x in components):
                raise ValueError(f"NuGet restore graph absent from {path.name}")
        if path.name.startswith(("SlimFaas-", "SlimFaasMcp-", "slimfaas.oci.tar.", "slimfaas-dotnet.oci.tar.",
                                 "slimfaas-mcp.oci.tar.", "fibonacci-webapp.oci.tar.")):
            if not any(x.get("purl", "").startswith("pkg:npm/") for x in components):
                raise ValueError(f"Embedded frontend absent from {path.name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("native", "packages", "sbom", "matrices"))
    parser.add_argument("--product")
    parser.add_argument("--rid")
    parser.add_argument("--version")
    parser.add_argument("--sha")
    parser.add_argument("--directory", type=release_directory, default=ROOT / "artifacts/release")
    args = parser.parse_args()
    if args.command == "matrices":
        emit(native=json.dumps({"include": products()["native"]}), images=json.dumps({"include": products()["images"]}))
        return
    version_parts(args.version)
    args.directory = args.directory.resolve()
    args.directory.mkdir(parents=True, exist_ok=True)
    if args.command == "sbom":
        sbom(args.directory, args.version)
        return
    if not SHA.fullmatch(args.sha or "") or run("git", "rev-parse", "HEAD", capture=True) != args.sha:
        raise ValueError("Build checkout does not match the requested source SHA")
    if args.command == "native":
        native(args.product, args.rid, args.version, args.sha, args.directory)
    else:
        packages(args.version, args.sha, args.directory)


if __name__ == "__main__":
    main()
