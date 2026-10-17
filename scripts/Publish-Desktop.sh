#!/bin/bash
# Shared portable distribution. Run on macOS for osx-arm64/osx-x64 or Windows cross-publishing.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$project_dir"
runtime="${1:-osx-arm64}"
case "$runtime" in osx-arm64|osx-x64|win-x64) ;; *) echo "Unsupported runtime: $runtime" >&2; exit 2;; esac
export DOTNET_CLI_HOME="$project_dir/.build/dotnet"
export NUGET_PACKAGES="$project_dir/.build/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
export MSBUILDDISABLENODEREUSE=1
dotnet_command="${DOTNET_ROOT:+$DOTNET_ROOT/}dotnet"
output_dir="$project_dir/artifacts/publish-$runtime"
"$dotnet_command" publish src/SoundPreviewer.App/SoundPreviewer.App.csproj -c Release -r "$runtime" \
    --self-contained true --disable-build-servers -p:UseSharedCompilation=false -p:PublishSingleFile=false -p:NuGetAudit=false -m:1 -o "$output_dir"
if [[ "$runtime" == osx-* ]]; then
    cp "$project_dir/scripts/Prepare-DawDreamer.sh" "$output_dir/Prepare-DawDreamer.sh"
    cat > "$output_dir/Start-SoundPreviewer.command" <<'LAUNCH'
#!/bin/bash
set -euo pipefail
application_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
cd "$application_dir"
exec "$application_dir/SoundPreviewer" "$@"
LAUNCH
    chmod +x "$output_dir/Start-SoundPreviewer.command" "$output_dir/Prepare-DawDreamer.sh" "$output_dir/SoundPreviewer" "$output_dir/SoundPreviewer.Worker"
else
    # The published folder already contains the runtime; never call the source-build launcher.
    cat > "$output_dir/Start-SoundPreviewer.cmd" <<'LAUNCH'
@echo off
cd /d "%~dp0"
start "" "%~dp0SoundPreviewer.exe" %*
LAUNCH
fi
echo "Published: $output_dir"
