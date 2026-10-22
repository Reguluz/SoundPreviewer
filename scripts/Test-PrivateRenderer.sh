#!/bin/bash
# CI/developer acceptance: real empty host, then move the whole private app copy.
set -euo pipefail
[[ "$(uname -s)" == Darwin ]] || { echo 'Run this acceptance check on macOS.' >&2; exit 2; }
project_dir="$(cd -- "$(dirname -- "$0")/.." && pwd -P)"
runtime="${1:-osx-arm64}"
case "$runtime" in osx-arm64) architecture=arm64 ;; osx-x64) architecture=x64 ;; *) echo 'Unsupported runtime.' >&2; exit 2 ;; esac
published="${2:-$project_dir/artifacts/publish-$runtime}"
[[ -f "$published/Prepare-DawDreamer.sh" && -f "$published/Renderers/uv/uv" ]] || { echo 'Publish this platform and bundled uv first.' >&2; exit 2; }
[[ -z "$(find "$published" \( -name Preview -o -name local-data -o -name library.db -o -name directories.json -o -iname '*.wav' -o -iname '*.m4a' \) -print -quit)" ]] || { echo 'The published source contains personal data; refusing to copy it.' >&2; exit 2; }
mkdir -p -- "$project_dir/artifacts"
diagnostics="$(mktemp -d "$project_dir/artifacts/private-renderer-smoke-$runtime-XXXXXXXX")"
application="$diagnostics/application"
mkdir -- "$application"
cp -R "$published/." "$application/"

run_setup() {
    local app="$1"
    local result="$2"
    shift 2
    # These deliberately invalid inherited settings must neither affect downloads
    # nor create their directories; the setup has its own fixed private settings.
    env UV_CACHE_DIR="$diagnostics/forbidden-uv-cache" \
        UV_PYTHON_INSTALL_DIR="$diagnostics/forbidden-python" \
        UV_PYTHON_DOWNLOADS_JSON_URL='https://invalid.example/forbidden-python-catalog.json' \
        UV_INDEX_URL='https://invalid.example/forbidden-index' \
        PIP_INDEX_URL='https://invalid.example/forbidden-pip-index' \
        PYTHONHOME="$diagnostics/forbidden-python-home" \
        PYTHONPATH="$diagnostics/forbidden-python-path" \
        bash "$app/Prepare-DawDreamer.sh" --app-path "$app" --architecture "$architecture" \
        --include-serum2 --result "$result" "$@"
}

run_setup "$application" "$application/Preview/diagnostics/setup-first.json"
manifest="$application/Preview/renderers/dawdreamer-portable-$runtime/runtime.json"
[[ -f "$manifest" ]] || { echo 'The private runtime marker is missing.' >&2; exit 1; }
first_hash="$(shasum -a 256 "$manifest" | awk '{print $1}')"
# Reproduce a hard interruption between the old-runtime and new-runtime moves.
# This retry must bootstrap from the private backup, even without a download.
private_runtime="${manifest%/runtime.json}"
interrupted_backup="$private_runtime-before-setup-0123456789abcdef"
[[ ! -e "$interrupted_backup" && ! -L "$interrupted_backup" ]] || { echo 'The recovery test backup already exists.' >&2; exit 1; }
mv -- "$private_runtime" "$interrupted_backup"
run_setup "$application" "$application/Preview/diagnostics/setup-recovered.json" --offline
[[ -f "$manifest" && ! -e "$interrupted_backup" ]] || { echo 'Interrupted private runtime was not recovered.' >&2; exit 1; }
[[ "$(shasum -a 256 "$manifest" | awk '{print $1}')" == "$first_hash" ]] || { echo 'Recovery changed the private runtime marker.' >&2; exit 1; }
relocated="$diagnostics/迁移后的 Portable 🎹"
[[ ! -e "$relocated" && ! -L "$relocated" ]] || { echo 'The relocation destination already exists.' >&2; exit 1; }
mv -- "$application" "$relocated"
run_setup "$relocated" "$relocated/Preview/diagnostics/setup-second.json" --offline
python="$relocated/Preview/renderers/dawdreamer-portable-$runtime/bin/python3.12"
"$python" -I -B - "$relocated" "$diagnostics" "$runtime" "$first_hash" <<'PYTHON'
import hashlib
import importlib.metadata
import json
from pathlib import Path
import sys

application, diagnostics = Path(sys.argv[1]), Path(sys.argv[2])
runtime_id, previous_manifest = sys.argv[3:5]
runtime = application / "Preview" / "renderers" / ("dawdreamer-portable-" + runtime_id)
manifest_path = runtime / "runtime.json"
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
result = json.loads((application / "Preview" / "diagnostics" / "setup-second.json").read_text(encoding="utf-8"))
recovered = json.loads((application / "Preview" / "diagnostics" / "setup-recovered.json").read_text(encoding="utf-8"))
expected = {"dawdreamer": "0.8.3", "numpy": "1.26.4", "serum2-preset-loader": "0.1.3", "cbor2": "5.6.5", "zstandard": "0.23.0"}
assert manifest["RuntimeKind"] == "uv-managed-cpython" and manifest["Platform"] == runtime_id
assert manifest["PythonExecutable"] == "bin/python3.12" and manifest["PythonVersion"] == "3.12.15"
assert manifest["UvVersion"] == "0.13.4" and manifest["Dependencies"] == expected
assert hashlib.sha256(manifest_path.read_bytes()).hexdigest() == previous_manifest
assert result["Success"] is True and result["ManagedRuntime"] is True and result["IncludeSerum2"] is True
assert recovered["Success"] is True and recovered["ManagedRuntime"] is True
assert Path(result["EnvironmentPythonPath"]).resolve() == Path(sys.executable).resolve()
assert Path(sys.prefix).resolve() == runtime.resolve() and sys.prefix == sys.base_prefix
assert sys.version_info[:3] == (3, 12, 15)
for package, version in expected.items():
    assert importlib.metadata.version(package) == version
assert 0 < len(manifest["Files"]) <= 64
for relative, expected_hash in manifest["Files"].items():
    path = runtime / relative
    assert not path.is_symlink() and runtime.resolve() in path.resolve().parents
    assert hashlib.sha256(path.read_bytes()).hexdigest() == expected_hash
for name in ("forbidden-uv-cache", "forbidden-python", "forbidden-python-home", "forbidden-python-path"):
    assert not (diagnostics / name).exists()
probe = json.loads(Path(result["ProbeResultPath"]).read_text(encoding="utf-8"))
assert probe["Success"] is True and probe["CommercialPluginsLoaded"] is False and probe["AudioWritten"] is False
report = {"Success": True, "Runtime": runtime_id, "PrivatePython": True, "Dependencies": expected,
    "RelocationChecked": True, "OfflineSetupChecked": True, "RuntimeFilesUnchanged": True,
    "InterruptedPromotionRecoveryChecked": True,
    "InheritedConfigurationIgnored": True, "EmptyHostChecked": True, "CommercialPluginsLoaded": False}
(diagnostics / "private-renderer-smoke.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps(report, indent=2))
PYTHON
echo "Private renderer diagnostics: $diagnostics"
