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
if [[ -d "$stage_dir" && -n "$(find "$stage_dir" \( -iname Preview -o -name local-data -o -name settings.json -o -name library-location.json -o -name directories.json -o -name preset-categories.json -o -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' -o -iname '*.wav' -o -iname '*.m4a' -o -name '*.log' \) -print -quit)" ]]; then
    echo 'Existing package staging contains user data. Preserve or move the old directory first; no files were deleted.' >&2; exit 2
fi
for file in SoundPreviewer SoundPreviewer.Worker; do
    [[ -f "$publish_dir/$file" ]] || { echo "Publish first; missing $file" >&2; exit 1; }
done
pwsh -NoProfile -File "$project_dir/scripts/Test-UserGuide.ps1" -GuideDirectory "$publish_dir/UserGuide" -RequireChm
if [[ -n "$(find "$publish_dir" \( -iname Preview -o -name local-data -o -name settings.json -o -name library-location.json -o -name directories.json -o -name preset-categories.json -o -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' -o -iname '*.wav' -o -iname '*.m4a' -o -name '*.log' \) -print -quit)" ]]; then
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
SoundPreviewer

请先将 SoundPreviewer.app 复制到您有写入权限的独立文件夹，
例如 ~/Applications/SoundPreviewer，再从本机打开。
DMG 是只读磁盘，请勿直接在其中启动应用。

首次使用会在 app 旁建立 Preview 文件夹，保存音色索引、设置和试听缓存。
个人目录、分类规则与定位配置统一保存到 app 旁的 Config 文件夹。
升级时退出应用并替换 SoundPreviewer.app，保留 Preview、Config 以及旧版根目录配置。
移动应用时，请将这些文件一起保留。

点击软件窗口右下角的书本图标可打开 Markdown 操作说明。
说明文件位于 SoundPreviewer.app/Contents/MacOS/UserGuide/index.md。
Windows 版本使用同目录内的 SoundPreviewer.chm 编译手册。

Applications 快捷方式供拥有该目录写入权限的用户使用。
README
ln -s /Applications "$stage_dir/Applications"
zip_path="$release_dir/SoundPreviewer-$version-$runtime.zip"
ditto -c -k --sequesterRsrc --keepParent "$app_dir" "$zip_path"
# Use logical file lengths rather than allocated blocks: sparse or compressed
# single-file executables can require more space when copied into the image.
# Do not traverse the Applications symlink while measuring the staged payload.
payload_bytes="$(find -P "$stage_dir" -type f -exec stat -f '%z' {} + | awk '{ total += $1 } END { printf "%.0f\n", total }')"
[[ "$payload_bytes" =~ ^[0-9]+$ && "$payload_bytes" -gt 0 ]] || { echo 'Could not determine the staged payload size.' >&2; exit 2; }
padded_bytes="$(( (payload_bytes * 5 + 3) / 4 + 64 * 1024 * 1024 ))"
image_megabytes="$(( (padded_bytes + 1024 * 1024 - 1) / (1024 * 1024) ))"
echo "DMG payload: $payload_bytes logical bytes; image capacity: $image_megabytes MiB (25% plus 64 MiB padding)."
du -sk "$stage_dir"
df -k "$stage_dir" "$release_dir" "${TMPDIR:-/tmp}"
for attempt in 1 2 3; do
    image_log="$project_dir/artifacts/dmg-$runtime-attempt-$attempt.txt"
    if LC_ALL=C hdiutil create -volname SoundPreviewer -srcfolder "$stage_dir" -size "${image_megabytes}m" -fs HFS+ -ov -format UDZO "$release_dir/SoundPreviewer-$version-$runtime.dmg" > "$image_log" 2>&1; then
        cat "$image_log"
        break
    else
        image_status=$?
        cat "$image_log" >&2
        if [[ "$attempt" == 3 ]] || ! grep -Fxq 'hdiutil: create failed - Resource busy' "$image_log"; then
            exit "$image_status"
        fi
        # Hosted macOS runners occasionally report EBUSY while creating an
        # image. Retry this one operation; never detach disks or hide other errors.
        delay=2
        [[ "$attempt" == 2 ]] && delay=5
        echo "DMG creation reported Resource busy; retrying in $delay seconds ($attempt/3)."
        sleep "$delay"
    fi
done
(
    cd "$release_dir"
    shasum -a 256 "SoundPreviewer-$version-$runtime.zip" "SoundPreviewer-$version-$runtime.dmg" > "SHA256SUMS-$runtime.txt"
)
echo "Packages: $release_dir"
