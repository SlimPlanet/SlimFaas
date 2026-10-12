"""Release invariants shared by the build, publication, and verification commands."""
import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import tomllib
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
REPOSITORY = "SlimPlanet/SlimFaas"
NPM_PACKAGE = "@axa-fr/slimfaas-planet-saver"
VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(alpha|beta|dev)\.(0|[1-9][0-9]*))?")
TITLE = re.compile(r"(feat|fix|deps|perf|refactor|docs|test|build|ci|chore|style|revert)(?:\([a-zA-Z0-9_.:/-]+\))?(!)?: [^\r\n]+")
SHA = re.compile(r"[0-9a-f]{40}")
SAFE_NAME = re.compile(r"[a-zA-Z0-9][a-zA-Z0-9_.-]*")
SIGNATURE_FILES = {"SHA256SUMS", "SHA256SUMS.sigstore.json", "provenance.sigstore.jsonl"}


def run(*args, cwd=ROOT, capture=False, **kwargs):
    result = subprocess.run([str(arg) for arg in args], cwd=cwd, check=True,
                            text=True, stdout=subprocess.PIPE if capture else None, **kwargs)
    return result.stdout.strip() if capture else None


def gh(endpoint, method="GET", data=None):
    args = ["gh", "api", "--method", method, endpoint]
    if data is not None:
        args += ["--input", "-"]
    return json.loads(run(*args, capture=True, input=json.dumps(data) if data is not None else None))


def download(url):
    """Only a genuine HTTP 404 means a version does not exist. All other errors fail."""
    try:
        with urllib.request.urlopen(url, timeout=90) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise


def version_parts(version):
    match = VERSION.fullmatch(version)
    if not match:
        raise ValueError(f"Invalid release version: {version!r}")
    major, minor, patch, channel, number = match.groups()
    return int(major), int(minor), int(patch), channel, number


def python_version(version):
    major, minor, patch, channel, number = version_parts(version)
    return f"{major}.{minor}.{patch}" + ({"alpha": "a", "beta": "b", "dev": ".dev"}[channel] + number if channel else "")


def check_metadata(root=ROOT):
    """Reject a release PR that updates only some of the common version files."""
    version = (root / "version.txt").read_text().strip()
    if version_parts(version)[3]:
        raise ValueError("Committed version metadata must use a stable version")
    actual = [json.loads((root / ".release-please-manifest.json").read_text())["."],
              json.loads((root / "src/SlimFaasPlanetSaver/package.json").read_text())["version"],
              ET.parse(root / "client/dotnet/SlimFaasClient/src/SlimFaasClient/SlimFaasClient.csproj").findtext(".//Version")]
    python = root / "client/python/slimfaas-client"
    actual.append(tomllib.loads((python / "pyproject.toml").read_text())["project"]["version"])
    packages = tomllib.loads((python / "uv.lock").read_text())["package"]
    actual.extend(item["version"] for item in packages if item["name"] == "slimfaas-client")
    if len(actual) != 5 or any(item != version for item in actual):
        raise ValueError("Common version metadata is inconsistent")
    return version


def next_version(current, commits):
    major, minor, patch, channel, _ = version_parts(current)
    if channel:
        raise ValueError("The release manifest must contain a stable version")
    bump = 0
    for message in commits:
        title = message.splitlines()[0] if message else ""
        match = TITLE.fullmatch(title)
        if re.search(r"(?m)^Release-As:", message, re.IGNORECASE):
            raise ValueError("Release-As overrides are unsupported; review the release PR instead")
        if match and (match.group(2) or re.search(r"(?m)^BREAKING(?: CHANGE|-CHANGE):", message)):
            bump = max(bump, 3)
        elif match and match.group(1) == "feat":
            bump = max(bump, 2)
        elif match and match.group(1) in {"fix", "deps", "perf"}:
            bump = max(bump, 1)
    if bump == 3:
        return f"{major + 1}.0.0"
    if bump == 2:
        return f"{major}.{minor + 1}.0"
    # A requested preview with no releasable changes uses the next patch.
    return f"{major}.{minor}.{patch + 1}"


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, data):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def emit(**outputs):
    for key, value in outputs.items():
        value = str(value).lower() if isinstance(value, bool) else str(value)
        if "\n" in value or "\r" in value:
            raise ValueError("Workflow output must fit on one line")
        print(f"{key}={value}")
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                stream.write(f"{key}={value}\n")


def products():
    return json.loads((ROOT / ".github/release-products.json").read_text())


def payload_names(version):
    names = []
    for item in products()["native"]:
        names.append(f"{item['product']}-{item['rid']}.zip")
        if item["product"] == "SlimFaas":
            names.append(f"SlimFaas-Local-{item['rid']}.zip")
    names += [f"{item['name']}.oci.tar" for item in products()["images"]]
    names += [f"slimfaas-planet-saver-{version}.tgz", f"SlimFaasClient.{version}.nupkg",
              f"SlimFaasClient.{version}.snupkg", f"slimfaas_client-{python_version(version)}-py3-none-any.whl",
              f"slimfaas_client-{python_version(version)}.tar.gz"]
    return sorted(names)


def sbom_names(name):
    prefixes = [name]
    if name.endswith(".oci.tar"):
        item = next(x for x in products()["images"] if name == x["name"] + ".oci.tar")
        prefixes = [name + "." + platform.replace("/", "-") for platform in item["platforms"].split(",")]
    return [prefix + suffix for prefix in prefixes for suffix in (".sbom.spdx.json", ".sbom.cdx.json")]


def safe_extract_zip(source, destination):
    with zipfile.ZipFile(source) as archive:
        for item in archive.infolist():
            name = Path(item.filename)
            if name.is_absolute() or ".." in name.parts or "\\" in item.filename or ":" in item.filename:
                raise ValueError("Unsafe path in release archive")
            if (item.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("Symlinks are not allowed in release ZIPs")
        archive.extractall(destination)


def zip_contents(data):
    """NuGet may add a repository signature; compare every original package entry."""
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate entries in package")
        return {name: archive.read(name) for name in names if name != ".signature.p7s"}


def check_published_file(local, remote, nuget=False):
    if remote is None:
        return False
    expected = Path(local).read_bytes()
    equal = zip_contents(expected) == zip_contents(remote) if nuget else expected == remote
    if not equal:
        raise ValueError(f"Published version collision for {Path(local).name}; refusing to overwrite")
    return True


def oci_identity(path):
    with tarfile.open(path) as archive:
        index = json.load(archive.extractfile("index.json"))
        descriptors = index["manifests"]
        if len(descriptors) != 1:
            raise ValueError("OCI archive must have a single root descriptor")
        root = descriptors[0]

        def read(descriptor):
            digest = descriptor["digest"]
            if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
                raise ValueError("Unsupported OCI digest")
            data = archive.extractfile("blobs/sha256/" + digest[7:]).read()
            if hashlib.sha256(data).hexdigest() != digest[7:]:
                raise ValueError("Corrupt OCI manifest or config")
            return json.loads(data)

        manifest = read(root)
        platforms = []
        for descriptor in manifest.get("manifests", [root]):
            child = read(descriptor)
            if "config" not in child:
                raise ValueError("Unexpected nested OCI index")
            config = read(child["config"])
            if config.get("os") == "unknown":  # BuildKit attestations
                continue
            platforms.append({"platform": f"{config['os']}/{config['architecture']}",
                              "labels": config.get("config", {}).get("Labels", {})})
        return root["digest"], platforms


def make_inventory(directory, version, source_sha, channel):
    directory = Path(directory)
    version_parts(version)
    if not SHA.fullmatch(source_sha):
        raise ValueError("Release source must be a full commit SHA")
    for name in payload_names(version):
        if not (directory / name).is_file():
            raise ValueError(f"Missing release artifact: {name}")
        for sbom in sbom_names(name):
            if not (directory / sbom).is_file():
                raise ValueError(f"Missing SBOM: {sbom}")
    images = []
    for item in products()["images"]:
        digest, entries = oci_identity(directory / f"{item['name']}.oci.tar")
        if sorted(entry["platform"] for entry in entries) != sorted(item["platforms"].split(",")):
            raise ValueError(f"Wrong platforms for {item['name']}")
        for entry in entries:
            labels = entry["labels"]
            if labels.get("org.opencontainers.image.revision") != source_sha or labels.get("org.opencontainers.image.version") != version:
                raise ValueError(f"Wrong OCI source/version for {item['name']}")
        images.append({"name": "axaguildev/" + item["name"], "file": item["name"] + ".oci.tar", "digest": digest})
    files = []
    for path in sorted(directory.iterdir()):
        if not path.is_file() or path.is_symlink() or not SAFE_NAME.fullmatch(path.name):
            raise ValueError("Release artifacts must be flat regular files with safe names")
        if path.name in SIGNATURE_FILES or path.name == "release.json":
            continue
        files.append({"name": path.name, "sha256": sha256(path), "size": path.stat().st_size})
    manifest = {"schema": 1, "repository": REPOSITORY, "version": version,
                "source_sha": source_sha, "channel": channel, "images": images,
                "run_id": os.environ.get("GITHUB_RUN_ID", "local"), "files": files}
    write_json(directory / "release.json", manifest)
    sums = files + [{"name": "release.json", "sha256": sha256(directory / "release.json")}]
    (directory / "SHA256SUMS").write_text("".join(f"{item['sha256']}  {item['name']}\n" for item in sorted(sums, key=lambda x: x["name"])))
    return manifest


def verify_inventory(directory, version, source_sha, allow_validate=False):
    directory = Path(directory)
    manifest = json.loads((directory / "release.json").read_text())
    if manifest.get("schema") != 1 or manifest.get("repository") != REPOSITORY:
        raise ValueError("Unexpected release manifest")
    if manifest["version"] != version or manifest["source_sha"] != source_sha:
        raise ValueError("Release version or source SHA mismatch")
    channel = version_parts(version)[3] or "stable"
    if manifest["channel"] != channel and not (allow_validate and manifest["channel"] == "validate"):
        raise ValueError("Release channel mismatch")
    expected = set(payload_names(version))
    names = [item["name"] for item in manifest["files"]]
    if len(names) != len(set(names)) or not expected.issubset(names):
        raise ValueError("Missing or duplicated release artifacts")
    for name in expected:
        if any(sbom not in names for sbom in sbom_names(name)):
            raise ValueError("Release SBOM missing")
    for item in manifest["files"]:
        if not SAFE_NAME.fullmatch(item["name"]):
            raise ValueError("Unsafe manifest path")
        path = directory / item["name"]
        if path.is_symlink() or not path.is_file() or sha256(path) != item["sha256"] or path.stat().st_size != item["size"]:
            raise ValueError(f"Artifact checksum/size mismatch: {item['name']}")
    sums = {item["name"]: item["sha256"] for item in manifest["files"]}
    sums["release.json"] = sha256(directory / "release.json")
    expected_sums = "".join(f"{digest}  {name}\n" for name, digest in sorted(sums.items()))
    if (directory / "SHA256SUMS").read_text() != expected_sums:
        raise ValueError("Checksum index does not match release manifest")
    actual = {p.name for p in directory.iterdir()}
    if actual - set(names) - SIGNATURE_FILES - {"release.json"}:
        raise ValueError("Unlisted files in release candidate")
    wanted_images = {"axaguildev/" + x["name"]: x for x in products()["images"]}
    if len(manifest["images"]) != len(wanted_images) or {image["name"] for image in manifest["images"]} != set(wanted_images):
        raise ValueError("Release image set mismatch")
    for image in manifest["images"]:
        wanted = wanted_images[image["name"]]
        if image["file"] != wanted["name"] + ".oci.tar":
            raise ValueError("Release image filename mismatch")
        if oci_identity(directory / image["file"])[0] != image["digest"]:
            raise ValueError("Release OCI digest mismatch")
    return manifest
