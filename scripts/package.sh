#!/usr/bin/env bash
# Builds a release package for one runtime identifier (PRD §43-44):
#   win-*   -> artifacts/package/fermata-<rid>.zip        (FermataApp.exe + fermata.exe side by side)
#   linux-* -> artifacts/package/fermata-<rid>.tar.gz     (FermataApp + fermata + fermata.desktop)
#   osx-*   -> artifacts/package/fermata-macos-<arch>.tar.gz containing Fermata.app
#              (menu bar app, LSUIElement; the `fermata` CLI lives in Contents/MacOS too)
# Usage: scripts/package.sh <rid> [version]
set -euo pipefail
rid="${1:?usage: package.sh <rid> [version]}"
version="${2:-0.5.0}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/artifacts/package"
stage="$out/stage-$rid"
rm -rf "$stage"; mkdir -p "$stage" "$out"

publish() {
  dotnet publish "$repo/src/$1" -c Release -r "$rid" --self-contained \
    -p:Version="$version" -p:DebugType=none -o "$2" --nologo -v quiet
}

# Both apps share one directory; macOS and Windows file systems are case-insensitive.
check_case_collisions() {
  local dupes
  dupes="$(cd "$1" && find . -type f | tr '[:upper:]' '[:lower:]' | sort | uniq -d)"
  if [ -n "$dupes" ]; then echo "case-insensitive file name collision in $1:" >&2; echo "$dupes" >&2; exit 3; fi
}

case "$rid" in
  osx-*)
    app="$stage/Fermata.app"
    bin="$app/Contents/MacOS"
    mkdir -p "$bin" "$app/Contents/Resources"
    publish Fermata.Desktop "$bin"
    publish Fermata.Cli "$bin"
    check_case_collisions "$bin"
    cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Fermata</string>
    <key>CFBundleDisplayName</key><string>Fermata</string>
    <key>CFBundleIdentifier</key><string>com.fermata.app</string>
    <key>CFBundleExecutable</key><string>FermataApp</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
    chmod +x "$bin/FermataApp" "$bin/fermata"
    archive="$out/fermata-macos-${rid#osx-}.tar.gz"
    tar -czf "$archive" -C "$stage" Fermata.app
    ;;
  linux-*)
    dir="$stage/fermata"
    publish Fermata.Desktop "$dir"
    publish Fermata.Cli "$dir"
    check_case_collisions "$dir"
    cat > "$dir/fermata.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Fermata
Comment=Codex usage limits and reset credits
Exec=FermataApp
Terminal=false
Categories=Development;Utility;
DESKTOP
    chmod +x "$dir/FermataApp" "$dir/fermata"
    archive="$out/fermata-$rid.tar.gz"
    tar -czf "$archive" -C "$stage" fermata
    ;;
  win-*)
    dir="$stage/fermata"
    publish Fermata.Desktop "$dir"
    publish Fermata.Cli "$dir"
    check_case_collisions "$dir"
    archive="$out/fermata-$rid.zip"
    rm -f "$archive"
    if command -v zip > /dev/null; then (cd "$stage" && zip -qr "$archive" fermata)
    elif command -v 7z > /dev/null; then (cd "$stage" && 7z a -bd -bso0 "$archive" fermata)
    else pwsh -NoProfile -Command "Compress-Archive -Path '$dir' -DestinationPath '$archive'"; fi
    ;;
  *) echo "unsupported rid: $rid" >&2; exit 2 ;;
esac

rm -rf "$stage"
echo "$archive"
