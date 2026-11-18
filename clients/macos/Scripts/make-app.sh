#!/bin/bash
# Wraps the release binary in "RenkuPorts Builder.app" (ad-hoc signed, not sandboxed: it runs docker).
# Usage: swift build -c release && Scripts/make-app.sh [output dir, default .build/app]
set -euo pipefail
pkg=$(cd "$(dirname "$0")/.." && pwd)
bin="$(swift build --package-path "$pkg" -c release --show-bin-path)/RenkuPortsBuilder"
[ -x "$bin" ] || { echo "build first: swift build -c release" >&2; exit 1; }
version=$(sed -n 's/^public let appVersion = "\(.*\)"/\1/p' "$pkg/Sources/BuilderCore/Settings.swift")
app="${1:-$pkg/.build/app}/RenkuPorts Builder.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS"
cp "$bin" "$app/Contents/MacOS/RenkuPortsBuilder"

# Placeholder icon (Scripts/make-icon.swift) until there is final artwork.
iconset=$(mktemp -d)/AppIcon.iconset
mkdir -p "$iconset" "$app/Contents/Resources"
swift "$pkg/Scripts/make-icon.swift" "$iconset/source.png"
for px in 16 32 128 256 512; do
	sips -z $px $px "$iconset/source.png" --out "$iconset/icon_${px}x${px}.png" >/dev/null
	sips -z $((px * 2)) $((px * 2)) "$iconset/source.png" --out "$iconset/icon_${px}x${px}@2x.png" >/dev/null
done
rm "$iconset/source.png"
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
rm -rf "$(dirname "$iconset")"
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
	<key>CFBundleExecutable</key><string>RenkuPortsBuilder</string>
	<key>CFBundleIconFile</key><string>AppIcon</string>
	<key>CFBundleIdentifier</key><string>org.renkuos.PortsBuilder</string>
	<key>CFBundleName</key><string>RenkuPorts Builder</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleShortVersionString</key><string>$version</string>
	<key>CFBundleVersion</key><string>$version</string>
	<key>LSMinimumSystemVersion</key><string>14.0</string>
	<key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
codesign --force --sign - "$app"
echo "$app"
