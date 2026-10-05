#!/usr/bin/env bash
# End-to-end test of `fermata update` against a fake release feed:
#   1. build and install an old package (0.0.1-e2e),
#   2. serve a newer package + SHA256SUMS.txt from scripts/fake-release/server.cs,
#   3. run the OLD installed `fermata update --yes`,
#   4. wait until the install folder runs the new version.
# Usage: scripts/update-e2e.sh <rid> <new-version>
set -euo pipefail
rid="${1:?usage: update-e2e.sh <rid> <new-version>}"
new_version="${2:?usage: update-e2e.sh <rid> <new-version>}"
old_version="0.0.1-e2e"
repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
port="${E2E_PORT:-18765}"

sha256() { if command -v sha256sum > /dev/null; then sha256sum "$@"; else shasum -a 256 "$@"; fi; }

echo "== building $old_version and $new_version packages for $rid"
old_pkg="$("$repo/scripts/package.sh" "$rid" "$old_version" | tail -1)"
mkdir -p "$work/old" "$work/feed"
mv "$old_pkg" "$work/old/"
new_pkg="$("$repo/scripts/package.sh" "$rid" "$new_version" | tail -1)"
cp "$new_pkg" "$work/feed/"
(cd "$work/feed" && sha256 "$(basename "$new_pkg")" > SHA256SUMS.txt)

echo "== installing $old_version"
mkdir -p "$work/install"
pkg_name="$(basename "$old_pkg")"
case "$pkg_name" in
  *.zip) (cd "$work/install" && unzip -q "$work/old/$pkg_name") ;;
  *)     tar -xzf "$work/old/$pkg_name" -C "$work/install" ;;
esac
case "$rid" in
  osx-*) cli="$work/install/Fermata.app/Contents/MacOS/fermata" ;;
  win-*) cli="$work/install/fermata/fermata.exe" ;;
  *)     cli="$work/install/fermata/fermata" ;;
esac
"$cli" --version | grep -q "$old_version"

echo "== starting fake feed on :$port"
dotnet run "$repo/scripts/fake-release/server.cs" -- "$work/feed" "$port" "v$new_version" > "$work/server.log" 2>&1 &
server=$!
trap 'kill $server 2>/dev/null || true' EXIT
for _ in $(seq 1 180); do
  curl -sf "http://localhost:$port/releases/latest" > /dev/null && break
  sleep 1
done
curl -sf "http://localhost:$port/releases/latest" > /dev/null || { echo "fake feed did not start:" >&2; cat "$work/server.log" >&2; exit 1; }

export FERMATA_UPDATE_FEED="http://localhost:$port/releases/latest"
export FERMATA_HOME="$work/home"
echo "== fermata update --check"
"$cli" update --check
echo "== fermata update --yes"
"$cli" update --yes

# Windows finishes the swap in a helper after the command exits.
for _ in $(seq 1 90); do
  if "$cli" --version 2>/dev/null | grep -q "$new_version"; then
    echo "== installed version: $("$cli" --version)"
    leftovers="$(find "$work/install" -maxdepth 1 -name '*.old-*' | wc -l | tr -d ' ')"
    test "$leftovers" -eq 0 || { echo "old install left behind" >&2; exit 1; }
    echo "update e2e passed"
    exit 0
  fi
  sleep 1
done

echo "update did not complete" >&2
find "$work" -name update.log -exec cat {} \; >&2 || true
exit 1
