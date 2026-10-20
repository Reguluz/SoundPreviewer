#!/bin/bash
# Build an unsigned .app, ZIP and drag-to-Applications disk image on macOS.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")/.." && pwd)"
runtime="${1:-osx-arm64}"
case "$runtime" in osx-arm64|osx-x64) ;; *) echo "Unsupported macOS runtime: $runtime" >&2; exit 2;; esac
[[ "$(uname -s)" == Darwin ]] || { echo 'macOS packaging requires macOS.' >&2; exit 2; }
version="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$project_dir/Version.props")"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo 'Invalid Version.props.' >&2; exit 2; }
publish_dir="$project_dir/artifacts/publish-$runtime"
release_dir="$project_dir/artifacts/releases"
stage_dir="$project_dir/artifacts/package-$runtime"
[[ ! -L "$project_dir/artifacts" && ! -L "$stage_dir" ]] || { echo 'Refusing a linked staging directory.' >&2; exit 2; }
if [[ -d "$stage_dir" && -n "$(find "$stage_dir" \( -iname Preview -o -name local-data -o -name settings.json -o -name library-location.json -o -name directories.json -o -name preset-categories.json -o -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' -o -name '*.wav' -o -name '*.log' \) -print -quit)" ]]; then
    echo 'Existing package staging contains user data. Preserve or move the old directory first; no files were deleted.' >&2; exit 2
fi
for file in SoundPreviewer SoundPreviewer.Worker libcoreclr.dylib; do
    [[ -f "$publish_dir/$file" ]] || { echo "Publish first; missing $file" >&2; exit 1; }
done
if [[ -n "$(find "$publish_dir" \( -iname Preview -o -name local-data -o -name settings.json -o -name library-location.json -o -name directories.json -o -name preset-categories.json -o -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' -o -name '*.wav' -o -name '*.log' \) -print -quit)" ]]; then
    echo 'Publish output contains local user data; publish into a clean directory again.' >&2; exit 1
fi
rm -rf -- "$stage_dir"
app_dir="$stage_dir/SoundPreviewer.app"
mkdir -p "$app_dir/Contents/MacOS" "$release_dir"
cp -R "$publish_dir/." "$app_dir/Contents/MacOS/"
rm -f "$app_dir/Contents/MacOS/Start-SoundPreviewer.command"
cat > "$app_dir/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>com.soundpreviewer.desktop</string>
  <key>CFBundleName</key><string>SoundPreviewer</string>
  <key>CFBundleDisplayName</key><string>SoundPreviewer</string>
  <key>CFBundleExecutable</key><string>SoundPreviewer</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${version%%-*}</string>
  <key>CFBundleVersion</key><string>${version%%-*}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
plutil -lint "$app_dir/Contents/Info.plist"
cat > "$stage_dir/安装说明.txt" <<'README'
SoundPreviewer 预发行版

请先将 SoundPreviewer.app 复制到您有写入权限的独立文件夹，
例如 ~/Applications/SoundPreviewer，再从本机打开。
DMG 是只读磁盘，请勿直接在其中启动应用。

首次使用会在 app 旁建立 Preview 文件夹，保存音色索引、设置和试听缓存。
升级时退出应用并替换 SoundPreviewer.app，保留 Preview、directories.json 和 library-location.json。
移动应用时，请将这些文件一起保留。

Applications 快捷方式供拥有该目录写入权限的用户使用。
README
ln -s /Applications "$stage_dir/Applications"
zip_path="$release_dir/SoundPreviewer-$version-$runtime.zip"
ditto -c -k --sequesterRsrc --keepParent "$app_dir" "$zip_path"
hdiutil create -volname SoundPreviewer -srcfolder "$stage_dir" -ov -format UDZO "$release_dir/SoundPreviewer-$version-$runtime.dmg"
(
    cd "$release_dir"
    shasum -a 256 "SoundPreviewer-$version-$runtime.zip" "SoundPreviewer-$version-$runtime.dmg" > "SHA256SUMS-$runtime.txt"
)
echo "Packages: $release_dir"
