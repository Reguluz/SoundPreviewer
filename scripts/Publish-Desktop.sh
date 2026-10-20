#!/bin/bash
# Publish the application and worker on their matching macOS build runner.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$project_dir"
runtime="${1:-osx-arm64}"
case "$runtime" in osx-arm64|osx-x64) ;; *) echo "Unsupported macOS runtime: $runtime" >&2; exit 2;; esac
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$project_dir/artifacts/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$project_dir/.build/nuget-packages}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
export MSBUILDDISABLENODEREUSE=1
dotnet_command="${DOTNET_ROOT:+$DOTNET_ROOT/}dotnet"
output_dir="$project_dir/artifacts/publish-$runtime"
[[ ! -L "$project_dir/artifacts" && ! -L "$output_dir" ]] || { echo 'Refusing a linked publish directory.' >&2; exit 2; }
if [[ -d "$output_dir" && -n "$(find "$output_dir" \( -iname Preview -o -name local-data -o -name settings.json -o -name library-location.json -o -name directories.json -o -name preset-categories.json -o -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' -o -name '*.wav' -o -name '*.log' \) -print -quit)" ]]; then
    echo 'Existing publish output contains user data. Preserve or move the old directory before publishing; no files were deleted.' >&2; exit 2
fi
rm -rf -- "$output_dir"
"$dotnet_command" restore src/SoundPreviewer.App/SoundPreviewer.App.csproj --configfile NuGet.Config -r "$runtime" -p:SelfContained=true -p:PublishSingleFile=true -p:NuGetAudit=false
"$dotnet_command" publish src/SoundPreviewer.App/SoundPreviewer.App.csproj -c Release -r "$runtime" \
    --self-contained true --no-restore --disable-build-servers -p:UseSharedCompilation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=false -p:NuGetAudit=false -m:1 -o "$output_dir"
for file in SoundPreviewer SoundPreviewer.Worker Renderers/dawdreamer_worker.py Renderers/serum2_state.py; do
    [[ -f "$output_dir/$file" ]] || { echo "Missing published file: $file" >&2; exit 1; }
done
cp "$project_dir/docs/releases.md" "$output_dir/RELEASES.md"
cp "$project_dir/scripts/Prepare-DawDreamer.sh" "$output_dir/Prepare-DawDreamer.sh"
cat > "$output_dir/Start-SoundPreviewer.command" <<'LAUNCH'
#!/bin/bash
set -euo pipefail
application_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
cd "$application_dir"
exec "$application_dir/SoundPreviewer" "$@"
LAUNCH
chmod +x "$output_dir/Start-SoundPreviewer.command" "$output_dir/Prepare-DawDreamer.sh" "$output_dir/SoundPreviewer" "$output_dir/SoundPreviewer.Worker"
echo "Published: $output_dir"
