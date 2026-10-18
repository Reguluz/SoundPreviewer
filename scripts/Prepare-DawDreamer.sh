#!/bin/bash
# Create an optional host in the application's persistent Preview folder.
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: bash Prepare-DawDreamer.sh --python /absolute/path/to/python3.12 [options]

  --python PATH           Explicit base CPython 3.11 or 3.12 executable (required).
  --app-path PATH         Existing application folder; defaults to this source
                          project or the published folder beside this script.
  --data-directory PATH   Persistent data folder; defaults to APP/Preview,
                          or Preview beside a macOS .app bundle.
  --environment-name NAME Environment folder name (default: dawdreamer-cpython).
  --include-serum2        Install the additional pinned Serum 2 state converter.
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
while [[ $# -gt 0 ]]; do
    case "$1" in
        --python|--app-path|--data-directory|--environment-name|--proxy)
            [[ $# -ge 2 && -n "$2" ]] || fail "Missing value for $1"
            case "$1" in
                --python) python_path="$2" ;;
                --app-path) application_path="$2" ;;
                --data-directory) data_directory="$2" ;;
                --environment-name) environment_name="$2" ;;
                --proxy) proxy="$2" ;;
            esac
            shift 2 ;;
        --include-serum2) include_serum2=1; shift ;;
        --help|-h) usage; exit 0 ;;
        *) fail "Unknown option: $1" ;;
    esac
done
[[ -n "$python_path" ]] || fail 'Select an explicit base Python with --python /absolute/path/to/python3.11 or python3.12.'
[[ "$python_path" == /* && -f "$python_path" && -x "$python_path" ]] || fail '--python must identify an existing executable by its absolute path.'
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

exec "$python_path" -I -B - "$application_path" "$environment_name" "$include_serum2" "$proxy" "$data_directory" <<'PYTHON'
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
    print(json.dumps({"Success": True, "EnvironmentPythonPath": str(environment_python),
                      "DataDirectory": str(data),
                      "ProbeResultPath": str(probe_result), "Architecture": architecture,
                      "IncludeSerum2": include_serum2,
                      "Message": "The isolated empty host is ready for separate real-plugin validation. No commercial plugin was loaded."},
                     ensure_ascii=False, indent=2))


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
