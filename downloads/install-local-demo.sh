#!/bin/sh
# Install a release bundle without installing build tools or changing the shell profile.
set -eu
fail() { printf 'SlimFaas local installer: %s\n' "$*" >&2; exit 1; }
usage() {
    printf '%s\n' 'Usage: sh install-local-demo.sh [--version TAG|latest] [--directory PATH]' \
        'Default: latest stable release into ./slimfaas-demo. Requires curl, unzip and a SHA-256 tool.'
}
version=latest
directory=./slimfaas-demo
while [ "$#" -gt 0 ]; do
    case $1 in
        --version|--directory)
            [ "$#" -ge 2 ] && [ -n "$2" ] || fail "Missing value for $1"
            case $1 in --version) version=$2 ;; --directory) directory=$2 ;; esac
            shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) fail "Unknown option: $1" ;;
    esac
done
case $version in *[!A-Za-z0-9._-]*|'') fail 'Invalid release tag.' ;; esac
for required in curl unzip mktemp; do
    command -v "$required" >/dev/null 2>&1 || fail "Install $required and retry."
done
if command -v sha256sum >/dev/null 2>&1; then
    checksum_tool=sha256sum
elif command -v shasum >/dev/null 2>&1; then
    checksum_tool=shasum
else
    fail 'Install sha256sum or shasum to verify the download.'
fi
os=$(uname -s)
arch=$(uname -m)
case "$os:$arch" in
    Linux:x86_64|Linux:amd64) rid=linux-x64 ;;
    Linux:aarch64|Linux:arm64) rid=linux-arm64 ;;
    Darwin:x86_64|Darwin:amd64) rid=osx-x64 ;;
    Darwin:arm64|Darwin:aarch64) rid=osx-arm64 ;;
    MINGW*:x86_64|MSYS*:x86_64|CYGWIN*:x86_64) rid=win-x64 ;;
    *) fail "Unsupported platform: $os $arch. See https://slimfaas.dev/get-started/local" ;;
esac
case $rid in
    linux-*)
        if command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl; then
            fail 'Linux bundles require glibc; Alpine/musl is not supported.'
        fi ;;
    win-x64) command -v cygpath >/dev/null 2>&1 || fail 'Use Git Bash (with cygpath) on Windows, or use WSL.' ;;
esac
releases=https://github.com/SlimPlanet/SlimFaas/releases
if [ "$version" = latest ]; then
    resolved=$(curl --proto '=https' --tlsv1.2 --fail --silent --show-error --location --retry 2 \
        --head --output /dev/null --write-out '%{url_effective}' "$releases/latest") || fail 'Cannot resolve the latest stable release. Check your connection or use --version TAG.'
    case $resolved in "$releases/tag/"*) version=${resolved##*/} ;; *) fail 'GitHub did not return a stable release tag.' ;; esac
    case $version in *[!A-Za-z0-9._-]*|'') fail 'Invalid release tag returned by GitHub.' ;; esac
fi
case $directory in /*) ;; *) directory="$(pwd)/$directory" ;; esac
if [ -e "$directory" ] || [ -L "$directory" ]; then
    if [ -d "$directory" ] && [ ! -L "$directory" ] &&
       [ -f "$directory/bundle-version.txt" ] && [ -f "$directory/bundle-rid.txt" ] &&
       [ "$(cat "$directory/bundle-version.txt")" = "$version" ] &&
       [ "$(cat "$directory/bundle-rid.txt")" = "$rid" ] && [ -f "$directory/start.sh" ]; then
        printf 'SlimFaas %s (%s) is already installed in %s. Existing files and state were preserved.\n' "$version" "$rid" "$directory"
        exit 0
    fi
    fail "Destination already exists: $directory. Choose a new --directory; no files were changed."
fi
parent=$(dirname -- "$directory")
mkdir -p -- "$parent"
temporary=$(mktemp -d "$parent/.slimfaas-install.XXXXXXXX")
trap 'rm -rf -- "$temporary"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
asset="SlimFaas-Local-$rid.zip"
printf 'Downloading SlimFaas %s for %s…\n' "$version" "$rid"
download() {
    curl --proto '=https' --tlsv1.2 --fail --silent --show-error --location --retry 2 \
        --output "$2" "$releases/download/$version/$1"
}
download "$asset.sha256" "$temporary/checksum" || fail "Release $version has no downloadable local bundle checksum for $rid. Choose a release containing SlimFaas-Local assets; older runtime-only releases cannot run this tutorial without build tools."
download "$asset" "$temporary/bundle.zip" || fail "Cannot download $asset from release $version. Check the release assets and your connection."
expected=$(awk 'NR == 1 { print $1 }' "$temporary/checksum")
case $expected in *[!0-9a-fA-F]*|'') fail 'Invalid SHA-256 checksum file.' ;; esac
[ "${#expected}" -eq 64 ] || fail 'Invalid SHA-256 checksum length.'
if [ "$checksum_tool" = sha256sum ]; then
    actual=$(sha256sum "$temporary/bundle.zip" | awk '{print $1}')
else
    actual=$(shasum -a 256 "$temporary/bundle.zip" | awk '{print $1}')
fi
[ "$actual" = "$expected" ] || fail 'SHA-256 mismatch. The archive was not installed.'
unzip -Z1 "$temporary/bundle.zip" > "$temporary/entries" || fail 'Invalid ZIP archive.'
if grep -Eq '(^/|(^|/)\.\.(/|$)|^[A-Za-z]:|\\)' "$temporary/entries"; then
    fail 'Archive contains an invalid path.'
fi
unzip -q "$temporary/bundle.zip" -d "$temporary/content" || fail 'Cannot extract the archive.'
[ "$(cat "$temporary/content/bundle-version.txt")" = "$version" ] || fail 'Bundle version does not match the selected release.'
[ "$(cat "$temporary/content/bundle-rid.txt")" = "$rid" ] || fail 'Bundle platform does not match this machine.'
chmod +x "$temporary/content/start.sh"
if [ "$rid" != win-x64 ]; then
    chmod +x "$temporary/content/runtime/SlimFaas" "$temporary/content/functions/fibonacci/Fibonacci" "$temporary/content/jobs/fibonacci-batch/FibonacciBatch"
fi
sh "$temporary/content/start.sh" --validate || fail 'The bundle could not validate. Check operating-system libraries and available ports.'
# Reserve the destination before copying. No existing installation can be overwritten.
mkdir -- "$directory" || fail "Destination appeared during installation: $directory"
cp -R "$temporary/content/." "$directory/"
printf '\nInstalled SlimFaas %s in %s\n' "$version" "$directory"
printf 'Start: cd "%s" && ./start.sh\nDashboard: http://127.0.0.1:30020/\n' "$directory"
