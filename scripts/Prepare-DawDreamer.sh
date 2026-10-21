#!/bin/bash
# Prepare an installation-local host without changing the user's Python setup.
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: bash Prepare-DawDreamer.sh [options]

  --python PATH           Optional legacy base CPython 3.11 / 3.12 executable.
                          Omit this to prepare the private portable runtime.
  --app-path PATH         Existing application folder; defaults to this source
                          project or the published folder beside this script.
  --data-directory PATH   Persistent data folder; defaults to APP/Preview,
                          or Preview beside a macOS .app bundle.
  --environment-name NAME Environment folder name (default: dawdreamer-cpython).
  --include-serum2        Install the additional pinned Serum 2 state converter.
  --architecture NAME    arm64 or x64; defaults to this process architecture.
  --uv-path PATH          Explicit uv 0.13.4 executable (otherwise use bundled uv).
  --result PATH           Write the final setup JSON inside the data directory.
  --offline               Reuse installed files/cache without network access.
  --proxy URL            Explicit pip proxy; inherited pip configuration is ignored.
  --help                 Show this usage without installing or checking anything.

Preserve the Preview folder when updating or moving the application.
The setup probes an empty host; it never loads a commercial plugin or preset.
USAGE
}

fail() { echo "$*" >&2; exit 2; }
script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
python_path=""
application_path=""
data_directory=""
environment_name="dawdreamer-cpython"
include_serum2=0
proxy=""
architecture=""
uv_path=""
result_path=""
offline=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --python|--app-path|--data-directory|--environment-name|--proxy|--architecture|--uv-path|--result)
            [[ $# -ge 2 && -n "$2" ]] || fail "Missing value for $1"
            case "$1" in
                --python) python_path="$2" ;;
                --app-path) application_path="$2" ;;
                --data-directory) data_directory="$2" ;;
                --environment-name) environment_name="$2" ;;
                --proxy) proxy="$2" ;;
                --architecture) architecture="$2" ;;
                --uv-path) uv_path="$2" ;;
                --result) result_path="$2" ;;
            esac
            shift 2 ;;
        --include-serum2) include_serum2=1; shift ;;
        --offline) offline=1; shift ;;
        --help|-h) usage; exit 0 ;;
        *) fail "Unknown option: $1" ;;
    esac
done
if [[ -n "$python_path" ]]; then
    [[ "$python_path" == /* && -f "$python_path" && -x "$python_path" ]] || fail '--python must identify an existing executable by its absolute path.'
fi
[[ "$environment_name" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] || fail 'Invalid environment name.'
if [[ -z "$application_path" ]]; then
    if [[ -f "$script_dir/Renderers/dawdreamer_worker.py" ]]; then
        application_path="$script_dir"
    elif [[ -f "$script_dir/renderers/dawdreamer_worker.py" ]]; then
        application_path="$(cd -- "$script_dir/.." && pwd)"
    else
        fail 'Cannot find renderer resources beside this setup script.'
    fi
fi

if [[ -z "$python_path" ]]; then
    [[ "$(uname -s)" == Darwin ]] || fail 'The private runtime setup supports macOS only.'
    application_path="$(cd -- "$application_path" && pwd -P)"
    resources=""
    for candidate in "$application_path/Renderers" "$application_path/Contents/MacOS/Renderers" \
                     "$application_path/Contents/Resources/Renderers" "$application_path/scripts/renderers"; do
        if [[ -f "$candidate/dawdreamer_worker.py" && -f "$candidate/requirements.txt" ]]; then resources="$candidate"; break; fi
    done
    [[ -n "$resources" ]] || fail 'The renderer requirements or worker script is missing.'
    if [[ -z "$architecture" ]]; then
        case "$(uname -m)" in arm64|aarch64) architecture=arm64 ;; x86_64) architecture=x64 ;; *) fail 'Unsupported macOS architecture.' ;; esac
    fi
    case "$architecture" in
        arm64) target=aarch64-apple-darwin; python_arch=aarch64; uv_sha=e0b1d4743ce64c55d9f0f4a444afa6b81125680070ee04e853fcd0f6c1ebf8f1 ;;
        x64) target=x86_64-apple-darwin; python_arch=x86_64; uv_sha=7be2dcd316122f4b3e5a0a7889f6248266c14425374e105ff7dc9779184603a6 ;;
        *) fail '--architecture must be arm64 or x64.' ;;
    esac
    portable_app="$application_path"
    ancestor="$application_path"
    while [[ "$ancestor" != / ]]; do
        if [[ "$ancestor" == *.app ]]; then portable_app="$(dirname -- "$ancestor")"; break; fi
        ancestor="$(dirname -- "$ancestor")"
    done
    if [[ -z "$data_directory" ]]; then data_directory="$portable_app/Preview"; fi
    [[ "$data_directory" == /* ]] || fail '--data-directory must be absolute.'
    # Reject linked containing directories before any download or output is created.
    ancestor="$data_directory/renderers"
    while [[ "$ancestor" != / ]]; do
        [[ ! -L "$ancestor" ]] || fail "Refusing a linked renderer data path: $ancestor"
        ancestor="$(dirname -- "$ancestor")"
    done
    renderers="$data_directory/renderers"
    temporary="$renderers/tmp"
    mkdir -p -- "$temporary"
    data_directory="$(cd -- "$data_directory" && pwd -P)"
    renderers="$data_directory/renderers"
    temporary="$renderers/tmp"
    runtime="$renderers/dawdreamer-portable-osx-$architecture"
    for protected in "$runtime" "$temporary" "$renderers/uv-0.13.4-$architecture" \
                     "$renderers/uv-cache" "$renderers/python-downloads" "$renderers/uv-bin" \
                     "$renderers/uv-tools" "$renderers/cache" "$renderers/config" "$renderers/data"; do
        ancestor="$protected"
        while [[ "$ancestor" != / ]]; do
            [[ ! -L "$ancestor" ]] || fail "Refusing a linked renderer data path: $ancestor"
            ancestor="$(dirname -- "$ancestor")"
        done
    done
    if [[ -n "$result_path" ]]; then [[ "$result_path" == /* ]] || fail '--result must be an absolute path inside the data directory.'; fi
    # uv must not inherit a user index, Python mirror, project or activation.
    while IFS='=' read -r variable _; do
        case "$variable" in UV_*|PIP_*|PYTHON*|VIRTUAL_ENV|CONDA_PREFIX|CONDA_DEFAULT_ENV|DYLD_*) unset "$variable" ;; esac
    done < <(env)
    export UV_NO_CONFIG=1 UV_NO_PROGRESS=1 UV_PYTHON_INSTALL_BIN=0 UV_PYTHON_INSTALL_REGISTRY=0 UV_PYTHON_NO_REGISTRY=1
    export UV_CACHE_DIR="$renderers/uv-cache" UV_PYTHON_INSTALL_DIR="$renderers/python-downloads"
    export UV_PYTHON_BIN_DIR="$renderers/uv-bin" UV_TOOL_DIR="$renderers/uv-tools" UV_TOOL_BIN_DIR="$renderers/uv-bin"
    export XDG_CACHE_HOME="$renderers/cache" XDG_CONFIG_HOME="$renderers/config" XDG_DATA_HOME="$renderers/data"
    export TMPDIR="$temporary" TMP="$temporary" TEMP="$temporary" MACOSX_DEPLOYMENT_TARGET=12.0
    export PYTHONDONTWRITEBYTECODE=1 PYTHONNOUSERSITE=1
    if [[ -n "$proxy" ]]; then export HTTPS_PROXY="$proxy" HTTP_PROXY="$proxy"; fi
    bootstrap="$(mktemp -d "$temporary/setup-mac-XXXXXXXX")"
    cleanup_private_setup() {
        case "$bootstrap" in "$temporary"/setup-mac-*) [[ ! -L "$bootstrap" ]] && rm -rf -- "$bootstrap" ;; esac
    }
    trap cleanup_private_setup EXIT
    if [[ -z "$uv_path" && -x "$resources/uv/uv" ]]; then uv_path="$resources/uv/uv"; fi
    if [[ -z "$uv_path" && -x "$renderers/uv-0.13.4-$architecture/uv" ]]; then uv_path="$renderers/uv-0.13.4-$architecture/uv"; fi
    if [[ -z "$uv_path" && "$offline" == 0 ]]; then
        echo 'SP_SETUP:准备私有下载工具'
        archive="$bootstrap/uv.tar.gz"
        curl -q --fail --location --proto '=https' --tlsv1.2 --retry 2 \
            "https://github.com/astral-sh/uv/releases/download/0.13.4/uv-$target.tar.gz" -o "$archive"
        [[ "$(shasum -a 256 "$archive" | awk '{print $1}')" == "$uv_sha" ]] || fail 'The official uv archive checksum did not match.'
        tar -xzf "$archive" -C "$bootstrap" "uv-$target/uv"
        mkdir -p -- "$renderers/uv-0.13.4-$architecture"
        uv_staged="$renderers/uv-0.13.4-$architecture/uv-$(basename -- "$bootstrap")"
        cp -- "$bootstrap/uv-$target/uv" "$uv_staged"
        chmod +x "$uv_staged"
        mv -f -- "$uv_staged" "$renderers/uv-0.13.4-$architecture/uv"
        uv_path="$renderers/uv-0.13.4-$architecture/uv"
    fi
    if [[ -n "$uv_path" ]]; then
        [[ "$uv_path" == /* && -x "$uv_path" ]] || fail '--uv-path must identify an absolute executable path.'
        uv_version="$("$uv_path" --version)"
        [[ "$uv_version" == 'uv 0.13.4' || "$uv_version" == 'uv 0.13.4 '* ]] || fail 'This setup requires the bundled uv 0.13.4.'
    fi
    python_path="$runtime/bin/python3.12"
    staged_runtime=""
    python_ready=0
    if [[ -f "$runtime/runtime.json" ]] && "$python_path" -I -B -c 'import json, sys; assert sys.version_info[:3] == (3, 12, 15)' >/dev/null 2>&1; then
        python_ready=1
    fi
    # A killed promotion can leave only our old, intact backup. macOS's own
    # JSON reader lets that private interpreter start recovery without Python
    # from the user's machine or a fresh download. Restore only after the
    # locked Python stage validates the complete manifest and empty host.
    if [[ "$python_ready" == 0 && ! -e "$runtime" && ! -L "$runtime" ]]; then
        for backup_candidate in "$runtime"-before-setup-*; do
            suffix="${backup_candidate#"$runtime"-before-setup-}"
            [[ "$suffix" =~ ^[0-9a-f]{16}$ && -d "$backup_candidate" ]] || continue
            linked=0
            for protected in "$backup_candidate/runtime.json" "$backup_candidate/bin/python3.12"; do
                ancestor="$protected"
                while [[ "$ancestor" != / ]]; do
                    if [[ -L "$ancestor" ]]; then linked=1; break; fi
                    ancestor="$(dirname -- "$ancestor")"
                done
            done
            [[ "$linked" == 0 && -f "$backup_candidate/runtime.json" && -x "$backup_candidate/bin/python3.12" ]] || continue
            backup_marker="$backup_candidate/runtime.json"
            [[ "$(/usr/bin/plutil -extract SchemaVersion raw -o - "$backup_marker" 2>/dev/null)" == 1 &&
               "$(/usr/bin/plutil -extract RuntimeKind raw -o - "$backup_marker" 2>/dev/null)" == uv-managed-cpython &&
               "$(/usr/bin/plutil -extract Platform raw -o - "$backup_marker" 2>/dev/null)" == "osx-$architecture" &&
               "$(/usr/bin/plutil -extract PythonExecutable raw -o - "$backup_marker" 2>/dev/null)" == bin/python3.12 &&
               "$(/usr/bin/plutil -extract PythonVersion raw -o - "$backup_marker" 2>/dev/null)" == 3.12.15 &&
               "$(/usr/bin/plutil -extract UvVersion raw -o - "$backup_marker" 2>/dev/null)" == 0.13.4 ]] || continue
            if "$backup_candidate/bin/python3.12" -I -B -c 'import json, sys; assert sys.version_info[:3] == (3, 12, 15)' >/dev/null 2>&1; then
                python_path="$backup_candidate/bin/python3.12"
                python_ready=1
                break
            fi
        done
    fi
    if [[ "$python_ready" == 0 ]]; then
        [[ -n "$uv_path" ]] || fail 'The private Python runtime is unavailable; connect once to prepare it.'
        echo 'SP_SETUP:准备私有 Python 运行环境'
        uv_arguments=(--no-config --no-progress --color never)
        if [[ "$offline" == 1 ]]; then uv_arguments+=(--offline); fi
        "$uv_path" "${uv_arguments[@]}" python install "cpython-3.12.15-macos-$python_arch-none" \
            --install-dir "$bootstrap/python" --no-bin --no-registry
        staged_runtime="$bootstrap/python/cpython-3.12.15-macos-$python_arch-none"
        python_path="$staged_runtime/bin/python3.12"
        [[ -x "$python_path" ]] || fail 'The private Python installation did not produce the requested interpreter.'
    fi
    "$python_path" -I -B - "$application_path" "$resources" "$data_directory" "$architecture" \
        "$uv_path" "$include_serum2" "$result_path" "$offline" "$runtime" "$staged_runtime" <<'PRIVATE_PYTHON'
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile

BASE_PINS = {"dawdreamer": "0.8.3", "numpy": "1.26.4"}
SERUM2_PINS = {"serum2-preset-loader": "0.1.3", "cbor2": "5.6.5", "zstandard": "0.23.0"}
PYTHON_VERSION = "3.12.15"
UV_VERSION = "0.13.4"


def phase(message):
    print("SP_SETUP:" + message, flush=True)


def local(path, root):
    path, root = Path(os.path.abspath(path)), Path(os.path.abspath(root))
    if path != root and root not in path.parents:
        raise ValueError("Setup output must remain in the selected Preview directory.")
    for current in (path, *path.parents):
        if current.is_symlink():
            raise ValueError("A renderer data directory is linked; no files were replaced.")
    return path


def python_info(python):
    code = r'''
import importlib.metadata, json, platform, sys
versions = {}
for name in ('dawdreamer', 'numpy', 'serum2-preset-loader', 'cbor2', 'zstandard'):
    try: versions[name] = importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError: versions[name] = None
print(json.dumps({'Version': '.'.join(map(str, sys.version_info[:3])), 'Platform': sys.platform,
    'Architecture': platform.machine(), 'Prefix': sys.prefix, 'IsVenv': sys.prefix != sys.base_prefix, 'Versions': versions}))
'''
    result = subprocess.run([str(python), "-I", "-B", "-c", code], check=True, capture_output=True,
                            text=True, stdin=subprocess.DEVNULL, timeout=30)
    return json.loads(result.stdout)


def validate_python(python, runtime, architecture):
    info = python_info(python)
    expected = "arm64" if architecture == "arm64" else "x86_64"
    if (info["Platform"] != "darwin" or info["Version"] != PYTHON_VERSION or info["Architecture"] != expected or
            info["IsVenv"] or Path(info["Prefix"]).resolve() != runtime.resolve()):
        raise ValueError("The private interpreter does not match this application platform and runtime directory.")
    return info


def write_json(path, value):
    content = json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_file() and path.read_text(encoding="utf-8") == content:
        return
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(content, encoding="utf-8")
    temporary.replace(path)


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def runtime_files(runtime, dependencies):
    # Native modules, distribution metadata and the converter define this host;
    # do not enumerate the entire Python standard library or write absolute paths.
    selected = {runtime / "bin" / "python3.12"}
    selected.update((runtime / "lib").glob("libpython3.12*.dylib"))
    site = runtime / "lib" / "python3.12" / "site-packages"
    for name in dependencies:
        distribution = importlib.metadata.Distribution.at(next(site.glob(name.replace('-', '_') + "-*.dist-info")))
        for item in distribution.files or ():
            path = Path(distribution.locate_file(item))
            if (path.name == "METADATA" or path.suffix in (".so", ".dylib") or
                    name == "serum2-preset-loader" and path.suffix == ".py"):
                selected.add(path)
    files = {}
    for path in sorted(selected):
        path = Path(os.path.abspath(path))
        if runtime not in path.parents or not path.is_file():
            raise ValueError("The private runtime contains an invalid required file.")
        if path.is_symlink():
            actual = path.resolve(strict=True)
            if runtime.resolve() not in actual.parents:
                raise ValueError("The private runtime contains an external executable/library link.")
            copied = path.with_name(path.name + ".portable-copy")
            shutil.copy2(actual, copied)
            copied.replace(path)
        local(path, runtime)
        files[path.relative_to(runtime).as_posix()] = file_hash(path)
    if not files or len(files) > 64:
        raise ValueError("The private runtime file manifest exceeds its supported limit.")
    return files


def probe(python, resources, data, platform_name):
    phase("检查试听生成环境")
    result_path = local(data / "diagnostics" / ("host-probe-" + platform_name + ".json"), data)
    result_path.parent.mkdir(parents=True, exist_ok=True)
    if result_path.exists():
        result_path.unlink()
    process = subprocess.run([str(python), "-I", "-B", str(resources / "dawdreamer_worker.py"),
                              "--probe", "--result", str(result_path)], stdin=subprocess.DEVNULL, timeout=30)
    if not result_path.is_file() or result_path.stat().st_size > 1024 * 1024:
        raise ValueError("The private host did not return a bounded probe report.")
    report = json.loads(result_path.read_text(encoding="utf-8"))
    if (process.returncode != 0 or not isinstance(report, dict) or report.get("Success") is not True or
            report.get("Finished") is not True or report.get("CommercialPluginsLoaded") is not False or
            report.get("AudioWritten") is not False or report.get("ProbeKind") != "EmptyHostDsp" or
            report.get("HostVersion") != BASE_PINS["dawdreamer"] or report.get("NumpyVersion") != BASE_PINS["numpy"]):
        raise ValueError("The private empty-host check failed; existing data was retained.")
    return result_path


def recover_interrupted_promotion(runtime, resources, data, architecture, platform_name):
    # Only our exact backup names and complete, pinned manifests are eligible.
    # Unrecognized or damaged folders are retained for manual inspection.
    if runtime.exists() or runtime.is_symlink():
        return False
    pattern = re.compile(re.escape(runtime.name) + r"-before-setup-[0-9a-f]{16}")
    backups = [path for path in runtime.parent.iterdir() if pattern.fullmatch(path.name)]
    for backup in sorted(backups, key=lambda path: path.lstat().st_mtime, reverse=True):
        try:
            local(backup, data)
            marker_path = local(backup / "runtime.json", backup)
            if not marker_path.is_file() or marker_path.stat().st_size > 1024 * 1024:
                continue
            marker = json.loads(marker_path.read_text(encoding="utf-8"))
            if (not isinstance(marker, dict) or marker.get("SchemaVersion") != 1 or
                    marker.get("RuntimeKind") != "uv-managed-cpython" or marker.get("Platform") != platform_name or
                    marker.get("PythonExecutable") != "bin/python3.12" or marker.get("PythonVersion") != PYTHON_VERSION or
                    marker.get("UvVersion") != UV_VERSION or marker.get("Dependencies") not in
                    (BASE_PINS, {**BASE_PINS, **SERUM2_PINS})):
                continue
            files = marker.get("Files")
            if not isinstance(files, dict) or not 0 < len(files) <= 64 or "bin/python3.12" not in files:
                continue
            for relative, digest in files.items():
                if (not isinstance(relative, str) or not relative or "\\" in relative or
                        relative.startswith("/") or any(part in ("", ".", "..") for part in relative.split("/")) or
                        not isinstance(digest, str) or re.fullmatch(r"[0-9a-fA-F]{64}", digest) is None or
                        file_hash(local(backup / relative, backup)).lower() != digest.lower()):
                    raise ValueError("The interrupted setup backup manifest is invalid.")
            python = local(backup / "bin" / "python3.12", backup)
            info = validate_python(python, backup, architecture)
            if not all(info["Versions"].get(name) == version for name, version in marker["Dependencies"].items()):
                continue
            probe(python, resources, data, platform_name)
        except (OSError, ValueError, subprocess.SubprocessError):
            continue
        if runtime.exists() or runtime.is_symlink():
            return False
        phase("恢复中断前的试听生成环境")
        backup.rename(runtime)
        return True
    return False


def setup_private():
    import fcntl
    data = Path(sys.argv[3])
    lock_path = local(data / "renderers" / (".dawdreamer-setup-osx-" + sys.argv[4] + ".lock"), data)
    with lock_path.open("a+b") as lock:
        try:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise ValueError("Another private runtime setup is already running; wait for it to finish.") from None
        return setup_private_locked()


def setup_private_locked():
    app, resources, data = (Path(value) for value in sys.argv[1:4])
    architecture, uv_path = sys.argv[4:6]
    include_serum2, output, offline = sys.argv[6] == "1", sys.argv[7], sys.argv[8] == "1"
    runtime, downloaded = Path(sys.argv[9]), sys.argv[10]
    runtime = local(runtime, data)
    output_path = local(Path(output), data) if output else None
    platform_name = "osx-" + architecture
    requirements = resources / ("requirements-serum2.txt" if include_serum2 else "requirements.txt")
    wanted = dict(BASE_PINS)
    if include_serum2:
        wanted.update(SERUM2_PINS)
    for name, expected in (("requirements.txt", [f"{key}=={value}" for key, value in BASE_PINS.items()]),
                           ("requirements-serum2.txt", ["-r requirements.txt"] + [f"{key}=={value}" for key, value in SERUM2_PINS.items()])):
        actual = [line.strip() for line in (resources / name).read_text(encoding="utf-8").splitlines()
                  if line.strip() and not line.strip().startswith("#")]
        if sorted(actual) != sorted(expected):
            raise ValueError("The renderer requirements do not match the fixed supported versions.")
    if recover_interrupted_promotion(runtime, resources, data, architecture, platform_name):
        downloaded = ""
    marker_path = runtime / "runtime.json"
    marker = None
    if runtime.exists():
        if not marker_path.is_file() or marker_path.stat().st_size > 1024 * 1024:
            raise ValueError("The runtime folder is not a recognized managed installation; no files were replaced.")
        marker = json.loads(marker_path.read_text(encoding="utf-8"))
        if (not isinstance(marker, dict) or not isinstance(marker.get("Dependencies"), dict) or
                marker.get("SchemaVersion") != 1 or marker.get("RuntimeKind") != "uv-managed-cpython" or
                marker.get("Platform") != platform_name or marker.get("PythonExecutable") != "bin/python3.12"):
            raise ValueError("The existing runtime marker is unsupported; no files were replaced.")
        if all(marker.get("Dependencies", {}).get(name) == version for name, version in SERUM2_PINS.items()):
            wanted.update(SERUM2_PINS)
            include_serum2 = True
            requirements = resources / "requirements-serum2.txt"

    current = runtime / "bin" / "python3.12"
    ready = False
    if marker and not downloaded:
        try:
            info = validate_python(current, runtime, architecture)
            ready = marker.get("PythonVersion") == PYTHON_VERSION and marker.get("UvVersion") == UV_VERSION and all(
                info["Versions"].get(name) == version for name, version in wanted.items())
            files = marker.get("Files", {})
            ready = ready and isinstance(files, dict) and 0 < len(files) <= 64 and all(
                isinstance(relative, str) and isinstance(digest, str) and
                file_hash(local(runtime / relative, runtime)) == digest for relative, digest in files.items())
        except (OSError, ValueError, subprocess.SubprocessError):
            ready = False
    stage_container = None
    prepared = Path(downloaded) if downloaded else None
    backup = None
    promoted = False
    try:
        if not ready:
            if not uv_path:
                raise ValueError("The runtime needs preparation; connect once or provide the bundled uv tool.")
            if prepared is None:
                stage_container = Path(tempfile.mkdtemp(prefix="runtime-mac-", dir=data / "renderers" / "tmp"))
                installation = stage_container / "python"
                command = [uv_path, "--no-config", "--no-progress", "--color", "never"]
                if offline:
                    command.append("--offline")
                phase("准备私有 Python 运行环境")
                subprocess.run(command + ["python", "install", "cpython-" + PYTHON_VERSION + "-macos-" +
                    ("aarch64" if architecture == "arm64" else "x86_64") + "-none", "--install-dir", str(installation),
                    "--no-bin", "--no-registry"], check=True, stdin=subprocess.DEVNULL)
                prepared = installation / ("cpython-" + PYTHON_VERSION + "-macos-" +
                    ("aarch64" if architecture == "arm64" else "x86_64") + "-none")
            python = prepared / "bin" / "python3.12"
            validate_python(python, prepared, architecture)
            phase("准备试听生成组件")
            command = [uv_path, "--no-config", "--no-progress", "--color", "never"]
            if offline:
                command.append("--offline")
            subprocess.run(command + ["pip", "install", "--python", str(python), "--break-system-packages",
                "--only-binary", ":all:", "--link-mode", "copy", "--index-url", "https://pypi.org/simple",
                "-r", str(requirements)], check=True, stdin=subprocess.DEVNULL)
            info = validate_python(python, prepared, architecture)
            if not all(info["Versions"].get(name) == version for name, version in wanted.items()):
                raise ValueError("The installed private host versions do not match the selected pins.")
            probe(python, resources, data, platform_name)
            files = runtime_files(prepared, wanted)
            # The marker and its file names contain no former installation prefix.
            manifest = {"SchemaVersion": 1, "RuntimeKind": "uv-managed-cpython", "Platform": platform_name,
                "PythonExecutable": "bin/python3.12", "PythonVersion": PYTHON_VERSION, "UvVersion": UV_VERSION,
                "Dependencies": wanted, "Files": files}
            write_json(prepared / "runtime.json", manifest)
            if runtime.exists():
                backup = runtime.with_name(runtime.name + "-before-setup-" + os.urandom(8).hex())
                runtime.rename(backup)
            prepared.rename(runtime)
            promoted = True
        probe_path = probe(current, resources, data, platform_name)
        # Re-check after promotion: this is what proves the base Python prefix relocates.
        info = validate_python(current, runtime, architecture)
        if not all(info["Versions"].get(name) == version for name, version in wanted.items()):
            raise ValueError("The moved private runtime did not retain its supported dependencies.")
        result = {"Success": True, "EnvironmentPythonPath": str(current), "DataDirectory": str(data),
            "ProbeResultPath": str(probe_path), "Architecture": architecture, "IncludeSerum2": include_serum2,
            "ManagedRuntime": True, "RuntimeKind": "uv-managed-cpython", "UvVersion": UV_VERSION,
            "PythonVersion": PYTHON_VERSION, "Message": "The installation-local empty host is ready for separate real-plugin validation."}
        if output_path:
            write_json(output_path, result)
        phase("试听生成环境已准备好")
        print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)
    except BaseException:
        if promoted:
            runtime.rename(prepared)
        if backup and backup.exists() and not runtime.exists():
            backup.rename(runtime)
        raise
    finally:
        if stage_container and stage_container.exists():
            local(stage_container, data / "renderers" / "tmp")
            shutil.rmtree(stage_container)


try:
    setup_private()
except subprocess.CalledProcessError as error:
    print(f"Private runtime command failed (exit {error.returncode}); existing user data was retained.", file=sys.stderr)
    sys.exit(1)
except (OSError, ValueError, StopIteration, subprocess.SubprocessError) as error:
    print(f"Private runtime setup failed: {error}", file=sys.stderr)
    sys.exit(1)
PRIVATE_PYTHON
    exit $?
fi

exec "$python_path" -I -B - "$application_path" "$environment_name" "$include_serum2" "$proxy" "$data_directory" "$result_path" "$offline" <<'PYTHON'
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import struct
import subprocess
import sys

BASE_PINS = {"dawdreamer": "0.8.3", "numpy": "1.26.4"}
SERUM2_PINS = {"serum2-preset-loader": "0.1.3", "cbor2": "5.6.5", "zstandard": "0.23.0"}


def fail(message):
    raise ValueError(message)


def python_info(executable, environment):
    inspection = r'''
import importlib.metadata, json, platform, struct, sys
from pathlib import Path
versions = {}
for name in ('dawdreamer', 'numpy', 'serum2-preset-loader', 'cbor2', 'zstandard'):
    try:
        versions[name] = importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        versions[name] = None
print(json.dumps({'Executable': sys.executable, 'BaseExecutable': getattr(sys, '_base_executable', sys.executable),
    'Prefix': sys.prefix, 'Major': sys.version_info.major, 'Minor': sys.version_info.minor,
    'Bits': struct.calcsize('P') * 8, 'Implementation': platform.python_implementation(),
    'Platform': sys.platform, 'Architecture': platform.machine(), 'IsVenv': sys.prefix != sys.base_prefix,
    'IsConda': (Path(sys.base_prefix) / 'conda-meta').is_dir() or 'anaconda' in sys.version.lower(), 'Versions': versions}))
'''
    result = subprocess.run([str(executable), "-I", "-B", "-c", inspection], check=True,
                            stdin=subprocess.DEVNULL, capture_output=True, text=True, env=environment)
    return json.loads(result.stdout)


def compatible(info):
    if (info["Platform"] != "darwin" or info["Implementation"] != "CPython" or info["Bits"] != 64 or
            info["Major"] != 3 or info["Minor"] not in (11, 12) or info["Architecture"] not in ("arm64", "x86_64")):
        fail("This setup requires macOS with explicitly selected 64-bit CPython 3.11 or 3.12 (arm64 or x86_64).")
    if info["IsConda"]:
        fail("Select a standard CPython installation rather than Conda; no global runtime or DLL is replaced.")


def requirement_lines(path):
    return [line.strip() for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.strip().startswith("#")]


def setup():
    app = Path(sys.argv[1]).expanduser().resolve(strict=True)
    name, include_serum2, proxy = sys.argv[2], sys.argv[3] == "1", sys.argv[4]
    data_argument = sys.argv[5]
    result_argument = sys.argv[6] if len(sys.argv) > 6 else ""
    offline = len(sys.argv) > 7 and sys.argv[7] == "1"
    bundle = next((parent for parent in (app, *app.parents) if parent.name.lower().endswith(".app")), None)
    portable_app = bundle.parent if bundle is not None else app
    data = Path(data_argument).expanduser() if data_argument else portable_app / "Preview"
    if not data.is_absolute():
        fail("--data-directory must be an absolute path to the persistent user data folder.")
    data = Path(os.path.abspath(data))
    if not app.is_dir():
        fail("--app-path must identify an existing application folder.")
    if sys.platform != "darwin":
        fail("This setup script supports macOS only; use Prepare-DawDreamer.ps1 on Windows.")
    source_info = python_info(Path(sys.executable), os.environ.copy())
    compatible(source_info)
    if source_info["IsVenv"]:
        fail("--python must identify the base CPython installation, not an existing virtual environment.")

    # Published copies use Renderers; repository scripts use scripts/renderers.
    resources = app / "Renderers"
    if not (resources / "dawdreamer_worker.py").is_file():
        resources = app / "Contents" / "MacOS" / "Renderers"
    if not (resources / "dawdreamer_worker.py").is_file():
        resources = app / "Contents" / "Resources" / "Renderers"
    if not (resources / "dawdreamer_worker.py").is_file():
        resources = app / "scripts" / "renderers"
    requirements = resources / "requirements.txt"
    worker = resources / "dawdreamer_worker.py"
    if not requirements.is_file() or not worker.is_file():
        fail("The renderer requirements or worker script is missing.")
    expected_base = [f"{package}=={version}" for package, version in BASE_PINS.items()]
    if sorted(requirement_lines(requirements)) != sorted(expected_base):
        fail("Expected exactly dawdreamer==0.8.3 and numpy==1.26.4; review setup before changing dependencies.")
    wanted = dict(BASE_PINS)
    if include_serum2:
        requirements = resources / "requirements-serum2.txt"
        expected = ["-r requirements.txt"] + [f"{package}=={version}" for package, version in SERUM2_PINS.items()]
        if not requirements.is_file() or sorted(requirement_lines(requirements)) != sorted(expected):
            fail("The optional Serum 2 requirements are missing or do not contain the audited fixed pins.")
        wanted.update(SERUM2_PINS)

    def local(relative):
        path = data / relative
        current = path
        while current != current.parent:
            if current.is_symlink():
                fail(f"Refusing a linked renderer data path: {current}")
            current = current.parent
        return path

    environment = local(Path("renderers") / name)
    architecture = source_info["Architecture"]
    wheel_cache = local(Path("renderers") / f"wheels-cp3{source_info['Minor']}-{architecture}")
    diagnostics = local(Path("diagnostics"))
    temporary = local(Path("renderers") / "tmp")
    for directory in (wheel_cache, diagnostics, temporary):
        directory.mkdir(parents=True, exist_ok=True)
    child_environment = os.environ.copy()
    child_environment.update({"TMPDIR": str(temporary), "XDG_CACHE_HOME": str(wheel_cache / "cache"),
                              "PIP_CACHE_DIR": str(wheel_cache / "pip-cache")})
    environment_python = environment / "bin" / "python"
    if not environment.exists():
        print("Creating a virtual environment in the persistent user data folder...", flush=True)
        subprocess.run([sys.executable, "-I", "-B", "-m", "venv", str(environment)], check=True,
                       stdin=subprocess.DEVNULL, env=child_environment)
    # A venv's Python symlink is expected; its containing directories must stay local.
    local(Path("renderers") / name / "bin")
    if not environment_python.is_file():
        fail("The environment directory already exists but has no Python executable. Choose a new --environment-name; nothing was deleted.")
    info = python_info(environment_python, child_environment)
    compatible(info)
    if (not info["IsVenv"] or Path(info["Prefix"]).resolve() != environment or
            Path(info["BaseExecutable"]).resolve() != Path(source_info["Executable"]).resolve()):
        fail("The existing environment belongs to a different Python installation. Choose a new --environment-name; nothing was replaced.")
    if all(info["Versions"].get(package) == version for package, version in wanted.items()):
        print("The selected fixed dependencies are already installed; skipping package download and installation.", flush=True)
    else:
        pip = [str(environment_python), "-I", "-B", "-m", "pip", "--isolated", "--disable-pip-version-check",
               "--no-input", "--cache-dir", str(wheel_cache / "pip-cache")]
        download = pip + ["download", "--only-binary=:all:", "--index-url", "https://pypi.org/simple",
                          "--dest", str(wheel_cache), "-r", str(requirements)]
        if proxy:
            download.append("--proxy=" + proxy)
        if not offline:
            print("Downloading the selected fixed wheels from the official Python package index...", flush=True)
            subprocess.run(download, check=True, stdin=subprocess.DEVNULL, env=child_environment)
        print("Installing only from the persistent wheel cache...", flush=True)
        subprocess.run(pip + ["install", "--no-index", "--find-links", str(wheel_cache), "-r", str(requirements)],
                       check=True, stdin=subprocess.DEVNULL, env=child_environment)
        info = python_info(environment_python, child_environment)
        if not all(info["Versions"].get(package) == version for package, version in wanted.items()):
            fail("The installed versions do not match the selected fixed requirements.")

    probe_result = local(Path("diagnostics") / f"host-probe-{name}.json")
    print("Running the bounded empty-host probe without a commercial plugin...", flush=True)
    process = subprocess.run([str(environment_python), "-I", "-B", str(worker), "--probe", "--result", str(probe_result)],
                             stdin=subprocess.DEVNULL, env=child_environment, timeout=20)
    if not probe_result.is_file() or probe_result.stat().st_size > 1024 * 1024:
        fail("The host probe returned no bounded JSON report.")
    probe = json.loads(probe_result.read_text(encoding="utf-8"))
    if (not isinstance(probe, dict) or process.returncode != 0 or probe.get("Success") is not True or
            probe.get("Finished") is not True or probe.get("CommercialPluginsLoaded") is not False or
            probe.get("AudioWritten") is not False or probe.get("ProbeKind") != "EmptyHostDsp" or
            probe.get("HostVersion") != BASE_PINS["dawdreamer"] or probe.get("NumpyVersion") != BASE_PINS["numpy"]):
        fail(f"The empty-host probe did not pass. Report: {probe_result}")
    result = {"Success": True, "EnvironmentPythonPath": str(environment_python),
                      "DataDirectory": str(data),
                      "ProbeResultPath": str(probe_result), "Architecture": architecture,
                      "IncludeSerum2": include_serum2,
                      "ManagedRuntime": False,
                      "Message": "The isolated empty host is ready for separate real-plugin validation. No commercial plugin was loaded."}
    if result_argument:
        result_path = Path(os.path.abspath(result_argument))
        if result_path != data and data not in result_path.parents:
            fail("--result must remain inside the selected persistent data folder.")
        result_path = local(result_path.relative_to(data))
        result_path.parent.mkdir(parents=True, exist_ok=True)
        result_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))


try:
    setup()
except subprocess.CalledProcessError as error:
    # Command arguments can include an authenticated proxy; do not echo them.
    print(f"Renderer setup command failed with exit code {error.returncode}; local files were retained for retry.", file=sys.stderr)
    sys.exit(1)
except (OSError, ValueError, subprocess.SubprocessError) as error:
    print(f"Renderer setup failed: {error}", file=sys.stderr)
    sys.exit(1)
PYTHON
