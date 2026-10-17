"""Real VST2 FXP and Serum 2 VST3 state rendering through an optional local host."""

import argparse
import gc
import hashlib
import importlib.metadata
import json
import math
import os
import platform
import plistlib
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import time


ADAPTER_VERSION = "dawdreamer-fxp-v1"
SERUM2_ADAPTER_VERSION = "dawdreamer-serum2-state-v1"
HOST_VERSION = "0.8.3"
NUMPY_VERSION = "1.26.4"
MINIMUM_MSVC_VERSION = (14, 40)
PROBE_TIMEOUT_SECONDS = 8


def host_metadata():
    return {
        "PythonPath": sys.executable,
        "PythonVersion": sys.version.split()[0],
        "Architecture": "arm64" if platform.machine().lower() in ("arm64", "aarch64") else ("x64" if struct.calcsize("P") == 8 else "x86"),
        "Platform": sys.platform,
        "HostVersion": importlib.metadata.version("dawdreamer"),
        "NumpyVersion": importlib.metadata.version("numpy"),
    }


def loaded_runtime_info():
    import ctypes
    from ctypes import wintypes

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
    kernel.GetModuleHandleW.restype = wintypes.HMODULE
    kernel.GetModuleFileNameW.argtypes = [wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD]
    kernel.GetModuleFileNameW.restype = wintypes.DWORD
    version_api = ctypes.WinDLL("version", use_last_error=True)
    version_api.GetFileVersionInfoSizeW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)]
    version_api.GetFileVersionInfoSizeW.restype = wintypes.DWORD
    version_api.GetFileVersionInfoW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
    version_api.GetFileVersionInfoW.restype = wintypes.BOOL
    version_api.VerQueryValueW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR,
                                          ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.UINT)]
    version_api.VerQueryValueW.restype = wintypes.BOOL
    runtimes = []
    for name in ("MSVCP140.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll"):
        module = kernel.GetModuleHandleW(name)
        if not module:
            continue
        path = ctypes.create_unicode_buffer(32768)
        length = kernel.GetModuleFileNameW(module, path, len(path))
        if not length or length >= len(path):
            raise OSError(f"Cannot inspect the loaded runtime path: {name}.")
        ignored = wintypes.DWORD()
        size = version_api.GetFileVersionInfoSizeW(path.value, ctypes.byref(ignored))
        if not size or size > 1024 * 1024:
            raise OSError(f"Cannot inspect the loaded runtime version: {path.value}.")
        data = ctypes.create_string_buffer(size)
        if not version_api.GetFileVersionInfoW(path.value, 0, size, data):
            raise ctypes.WinError(ctypes.get_last_error())
        fixed_pointer = ctypes.c_void_p()
        fixed_length = wintypes.UINT()
        if not version_api.VerQueryValueW(data, "\\", ctypes.byref(fixed_pointer), ctypes.byref(fixed_length)):
            raise ctypes.WinError(ctypes.get_last_error())
        if fixed_length.value < 52:
            raise OSError(f"Runtime version resource is truncated: {path.value}.")
        fixed = ctypes.cast(fixed_pointer, ctypes.POINTER(wintypes.DWORD))
        if fixed[0] != 0xFEEF04BD:
            raise OSError(f"Runtime version resource is invalid: {path.value}.")
        version = (fixed[2] >> 16, fixed[2] & 0xFFFF, fixed[3] >> 16, fixed[3] & 0xFFFF)
        runtimes.append({"Name": name, "Path": path.value, "Version": ".".join(map(str, version))})
    return runtimes


def validate_runtime_info(runtimes):
    standard_library = next((item for item in runtimes if item["Name"].lower() == "msvcp140.dll"), None)
    if standard_library is None:
        raise RuntimeError("无法核对宿主实际加载的 MSVCP140.dll；未开始创建音源。请使用标准 x64 CPython 的独立环境。")
    for runtime in runtimes:
        version = tuple(int(part) for part in runtime["Version"].split("."))
        if version[:2] < MINIMUM_MSVC_VERSION:
            raise RuntimeError(
                f"宿主实际加载了过旧的 C++ 运行库 {runtime['Name']} {runtime['Version']}（{runtime['Path']}），"
                "可能导致宿主初始化崩溃。此桥接要求运行库至少 14.40；建议使用标准 x64 CPython 3.11/3.12 "
                "重新创建独立环境，不要覆盖 Anaconda 或系统 DLL。")


def load_host(report_phase, report_metadata=lambda metadata: None):
    if sys.platform not in ("win32", "darwin") or struct.calcsize("P") != 8 or sys.version_info[:2] not in ((3, 11), (3, 12)):
        raise ValueError("Pinned DawDreamer bridge requires Windows/macOS and 64-bit Python 3.11 or 3.12.")
    try:
        metadata = host_metadata()
    except importlib.metadata.PackageNotFoundError as error:
        raise RuntimeError("Optional host is missing. Install scripts/renderers/requirements.txt into a local Python environment.") from error
    report_metadata(metadata)
    if metadata["HostVersion"] != HOST_VERSION or metadata["NumpyVersion"] != NUMPY_VERSION:
        raise ValueError(f"Expected DawDreamer {HOST_VERSION} and NumPy {NUMPY_VERSION}; found "
                         f"{metadata['HostVersion']} / {metadata['NumpyVersion']}.")
    # DawDreamer loads LLVM; import it before NumPy or other numerical runtimes.
    report_phase("导入本地宿主库")
    import dawdreamer as daw

    if sys.platform == "win32":
        metadata["RuntimeDlls"] = loaded_runtime_info()
        report_metadata(metadata)
        validate_runtime_info(metadata["RuntimeDlls"])
    import numpy as np
    return daw, np, metadata


def write_result(path, result):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(result, ensure_ascii=False, allow_nan=False), encoding="utf-8")
    os.replace(temporary, path)


def probe_host(report_phase, report_metadata):
    daw, np, metadata = load_host(report_phase, report_metadata)
    report_phase("创建空宿主，不加载商业插件")
    engine = daw.RenderEngine(44100, 128)
    source = engine.make_playback_processor("__host_probe__", np.zeros((2, 256), dtype=np.float32))
    engine.load_graph([(source, [])])
    report_phase("检查内置零缓冲 DSP，不输出音频文件")
    if not engine.render(0.05):
        raise RuntimeError("Empty-host DSP rendering returned failure.")
    audio = np.asarray(engine.get_audio())
    if audio.shape != (2, 2205) or not np.isfinite(audio).all() or np.any(audio != 0):
        raise RuntimeError("Empty-host DSP returned unexpected dimensions or sample values.")
    return {
        **metadata,
        "Success": True,
        "Message": "宿主基础检查通过；未加载商业音源或预制，此结果不代表插件兼容性已验证。",
        "AdapterVersion": ADAPTER_VERSION,
        "ProbeKind": "EmptyHostDsp",
        "CommercialPluginsLoaded": False,
        "AudioWritten": False,
        "SampleRate": 44100,
        "Channels": int(audio.shape[0]),
        "Frames": int(audio.shape[1]),
    }


def isolated_probe(result_path, report_phase):
    report_phase("准备宿主自检，不加载商业插件")
    with tempfile.TemporaryDirectory(prefix="host-probe-", dir=result_path.parent) as temporary:
        child_result = Path(temporary) / "probe.json"
        command = [sys.executable, str(Path(__file__).resolve()), "--probe-internal", "--result", str(child_result)]
        try:
            process = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     timeout=PROBE_TIMEOUT_SECONDS,
                                     creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0)
            exit_code = process.returncode
        except subprocess.TimeoutExpired:
            exit_code = None
        result = {}
        if child_result.is_file() and child_result.stat().st_size <= 1024 * 1024:
            try:
                result = json.loads(child_result.read_text(encoding="utf-8"))
            except (ValueError, OSError):
                pass
        if not isinstance(result, dict):
            result = {}
        phase_path = child_result.with_suffix(child_result.suffix + ".phase")
        phase = phase_path.read_text(encoding="utf-8") if phase_path.is_file() else "启动自检进程"
        if exit_code == 0 and result.get("Success") is True:
            report_phase("宿主基础检查完成")
            return result
        if exit_code is None:
            message = f"宿主自检超过 {PROBE_TIMEOUT_SECONDS} 秒，已结束子进程；最后阶段：{phase}。"
        elif result.get("Message") and result.get("Success") is False and result.get("Finished") is True:
            message = result["Message"]
        else:
            message = f"宿主自检异常退出 0x{exit_code & 0xFFFFFFFF:08X}；最后阶段：{phase}。请检查独立 Python 环境和 C++ 运行库。"
        return {**result, "Success": False, "Message": message, "AdapterVersion": ADAPTER_VERSION,
                "ProbeKind": "EmptyHostDsp", "CommercialPluginsLoaded": False, "AudioWritten": False}


def check_plugin_architecture(plugin_path):
    if sys.platform == "darwin":
        check_mac_plugin_architecture(plugin_path)
        return
    if plugin_path.is_dir():
        binaries = plugin_path / "Contents" / "x86_64-win"
        if plugin_path.suffix.lower() != ".vst3" or binaries.is_symlink():
            raise ValueError("Expected a Windows VST3 x64 bundle.")
        candidates = [p for p in binaries.iterdir() if p.is_file() and not p.is_symlink() and p.suffix.lower() == ".vst3"]
        if len(candidates) != 1:
            raise ValueError("Windows VST3 bundle must contain one unambiguous x64 binary.")
        plugin_path = candidates[0]
    with plugin_path.open("rb") as stream:
        if stream.read(2) != b"MZ":
            raise ValueError("Plugin is not a Windows PE binary.")
        stream.seek(0x3C)
        offset_bytes = stream.read(4)
        if len(offset_bytes) != 4:
            raise ValueError("Plugin PE header is truncated.")
        stream.seek(struct.unpack("<I", offset_bytes)[0])
        if stream.read(4) != b"PE\0\0":
            raise ValueError("Plugin PE signature is invalid.")
        machine_bytes = stream.read(2)
        if len(machine_bytes) != 2:
            raise ValueError("Plugin PE machine header is truncated.")
        machine = struct.unpack("<H", machine_bytes)[0]
    if machine == 0x14C:
        raise ValueError("Plugin is x86 / 32-bit. DawDreamer requires a genuine x64 plugin; the folder name does not establish architecture.")
    if machine != 0x8664:
        raise ValueError(f"Unsupported plugin architecture: PE machine 0x{machine:04X}.")


def check_mac_plugin_architecture(bundle):
    if bundle.suffix.lower() not in (".vst", ".vst3") or not bundle.is_dir() or bundle.is_symlink():
        raise ValueError("macOS rendering requires a native VST2 .vst or VST3 .vst3 bundle; Windows DLLs cannot run on macOS.")
    contents = bundle / "Contents"
    plist_path = contents / "Info.plist"
    if contents.is_symlink() or plist_path.is_symlink() or plist_path.stat().st_size > 1024 * 1024:
        raise ValueError("Invalid or linked VST2 bundle metadata.")
    metadata = plistlib.loads(plist_path.read_bytes())
    name = metadata.get("CFBundleExecutable", "")
    if not isinstance(name, str) or not name or name in (".", "..") or "/" in name or "\\" in name:
        raise ValueError("VST2 bundle executable must be a filename inside Contents/MacOS.")
    binary = contents / "MacOS" / name
    if binary.parent.is_symlink() or binary.is_symlink() or not binary.is_file():
        raise ValueError("VST2 bundle executable is missing or linked outside the bundle.")
    required = 0x0100000C if platform.machine().lower() in ("arm64", "aarch64") else 0x01000007
    with binary.open("rb") as stream:
        header = stream.read(8)
        if len(header) != 8:
            raise ValueError("Mach-O header is truncated.")
        magic = header[:4]
        if magic in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
            endian = "<" if magic[0] == 0xCF else ">"
            machines = {struct.unpack(endian + "I", header[4:8])[0]}
        elif magic in (b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca"):
            endian = ">" if magic[0] == 0xCA else "<"
            count = struct.unpack(endian + "I", header[4:8])[0]
            if not 1 <= count <= 32:
                raise ValueError("Invalid Mach-O universal architecture count.")
            wide = magic in (b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca")
            size = 32 if wide else 20
            machines = set()
            for _ in range(count):
                record = stream.read(size)
                if len(record) != size:
                    raise ValueError("Mach-O universal header is truncated.")
                machine = struct.unpack(endian + "I", record[:4])[0]
                offset, length = struct.unpack(endian + ("QQ" if wide else "II"), record[8:24] if wide else record[8:16])
                if offset < 8 + count * size or length < 32 or offset + length > binary.stat().st_size:
                    raise ValueError("Mach-O universal slice is outside the binary.")
                machines.add(machine)
        else:
            raise ValueError("Plugin is not a native 64-bit Mach-O binary.")
    if required not in machines:
        raise ValueError(f"Plugin architecture does not match Python {platform.machine()}; use a matching native host and plugin.")


def validate_request(request):
    preset = request["Preset"]
    plugin = request["Plugin"]
    template = request["Template"]
    if preset["Synth"] not in (1, 2, 3) or plugin["Synth"] != preset["Synth"]:
        raise ValueError("DawDreamer bridge supports Serum 1 / Sylenth1 VST2 FXP and Serum 2 VST3 .SerumPreset.")
    if preset.get("BankSlot") is not None or preset.get("InternalPath"):
        raise ValueError("Bank slots and package entries require a verified bank adapter; export a standalone FXP first.")
    binary = Path(plugin["PluginPath"]).resolve(strict=True)
    baseline = request.get("IsInitializationBaseline") is True
    if baseline and (preset.get("SourcePath") or preset.get("ContentHash")):
        raise ValueError("Default-startup baseline must not contain a source preset identity.")
    source = None if baseline else Path(preset["SourcePath"]).resolve(strict=True)
    serum2 = preset["Synth"] == 2
    if serum2:
        if binary.suffix.lower() != ".vst3":
            raise ValueError("Serum 2 requires its native VST3 instrument.")
        if source is not None and source.suffix.lower() != ".serumpreset":
            raise ValueError("Serum 2 VST3 rendering requires .SerumPreset; import legacy FXP in Serum 2 and save it in the native format first.")
    elif (source is not None and source.suffix.lower() != ".fxp") or binary.suffix.lower() not in (".dll", ".vst"):
        raise ValueError("FXP requires a VST2 instrument (.dll on Windows, .vst on macOS); VST3 and banks require separate adapters.")
    if "serumfx" in binary.stem.lower():
        raise ValueError("SerumFX is an effect; select the Serum instrument DLL.")
    if source is not None and source.stat().st_size > 64 * 1024 * 1024:
        raise ValueError("Preset exceeds the 64 MB limit.")
    check_plugin_architecture(binary)
    note_order = template.get("NoteOrder", 0)
    if type(note_order) is not int or note_order not in (0, 1):
        raise ValueError("NoteOrder must be LowMiddleHigh (0) or MiddleHighLow (1).")
    pitches = ([template["MiddleNote"], template["HighNote"], template["LowNote"]] if note_order == 1 else
               [template["LowNote"], template["MiddleNote"], template["HighNote"]])
    if any(not isinstance(pitch, int) or not 0 <= pitch <= 127 for pitch in pitches):
        raise ValueError("MIDI pitches must be integers in [0, 127].")
    if not isinstance(template["Velocity"], int) or not 1 <= template["Velocity"] <= 127:
        raise ValueError("Velocity must be an integer in [1, 127].")
    hold, tail = float(template["HoldSeconds"]), float(template["TailSeconds"])
    if not math.isfinite(hold) or not 0.1 <= hold <= 30 or not math.isfinite(tail) or not 0 <= tail <= 30:
        raise ValueError("Hold / tail durations are outside the supported range.")
    if template["SampleRate"] not in (44100, 48000) or not 20 <= template["Bpm"] <= 300:
        raise ValueError("Sample rate or BPM is outside the supported range.")
    if baseline:
        return None, binary, pitches, None
    raw = source.read_bytes()
    if serum2:
        if len(raw) < 25 or raw[:9] != b"XferJson\x00":
            raise ValueError("Serum 2 preset is missing the XferJson envelope.")
        metadata_bytes = struct.unpack("<Q", raw[9:17])[0]
        if not 1 <= metadata_bytes <= 1024 * 1024 or 25 + metadata_bytes >= len(raw):
            raise ValueError("Serum 2 preset metadata is truncated or oversized.")
        metadata = json.loads(raw[17:17 + metadata_bytes])
        if not isinstance(metadata, dict) or metadata.get("fileType") != "SerumPreset" or metadata.get("product") != "Serum2":
            raise ValueError("Serum 2 preset metadata does not identify a native SerumPreset.")
    elif raw[:4] != b"CcnK" or raw[8:12] not in (b"FPCh", b"FxCk"):
        raise ValueError("Preset is not a standalone VST2 FXP program.")
    source_hash = hashlib.sha256(raw).hexdigest()
    if preset.get("ContentHash") and source_hash.lower() != preset["ContentHash"].lower():
        raise ValueError("Preset changed after scanning; scan it again before rendering.")
    return source, binary, pitches, raw


def render(request, report_phase=lambda phase: None):
    source, binary, pitches, raw = validate_request(request)
    daw, np, _ = load_host(report_phase)
    serum2 = request["Preset"]["Synth"] == 2

    template = request["Template"]
    sample_rate = template["SampleRate"]
    hold, tail = float(template["HoldSeconds"]), float(template["TailSeconds"])
    velocity = template["Velocity"]
    output = Path(request["OutputPath"]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    segments, notes = [], []
    start_frame = 0
    needs_review = False
    with tempfile.TemporaryDirectory(prefix="fxp-", dir=output.parent) as temporary:
        snapshot = None
        if source is not None:
            snapshot = Path(temporary) / source.name
            snapshot.write_bytes(raw)
        for pitch in pitches:
            report_phase(f"创建宿主，MIDI {pitch}")
            engine = daw.RenderEngine(sample_rate, 128)
            engine.set_bpm(float(template["Bpm"]))
            report_phase(f"实例化 {'Serum 2 VST3' if serum2 else 'VST2'} 音源，MIDI {pitch}")
            synth = engine.make_plugin_processor("synth", str(binary))
            if synth.get_num_output_channels() not in (1, 2):
                raise ValueError("Plugin must expose one or two output channels.")
            if snapshot is not None and serum2:
                report_phase(f"转换并核对 Serum 2 状态，MIDI {pitch}")
                try:
                    from serum2_state import prepare_serum2_state, verify_serum2_state
                except ImportError as error:
                    raise RuntimeError("Serum 2 requires the bundled state adapter and requirements-serum2.txt dependencies.") from error
                fresh = Path(temporary) / f"startup-{pitch}.state"
                synth.save_state(str(fresh))
                if not fresh.is_file() or fresh.stat().st_size > 128 * 1024 * 1024:
                    raise ValueError("Serum 2 startup state is missing or exceeds the adapter limit.")
                state, proof = prepare_serum2_state(raw, fresh.read_bytes(), request["Plugin"]["Version"])
                converted = Path(temporary) / f"preset-{pitch}.state"
                converted.write_bytes(state)
                synth.load_state(str(converted))
                restored = Path(temporary) / f"verified-{pitch}.state"
                synth.save_state(str(restored))
                if not restored.is_file() or restored.stat().st_size > 128 * 1024 * 1024:
                    raise ValueError("Serum 2 did not return a bounded state snapshot after loading.")
                verify_serum2_state(restored.read_bytes(), proof)
            elif snapshot is not None:
                report_phase(f"加载 FXP，MIDI {pitch}")
                if not synth.load_preset(str(snapshot)):
                    raise ValueError("Host rejected the FXP; target preset was not loaded.")
            else:
                report_phase(f"默认启动基准，未加载任何预制，MIDI {pitch}")
            synth.clear_midi()
            engine.load_graph([(synth, [])])
            time.sleep(0.15)
            report_phase(f"预热音源，MIDI {pitch}")
            engine.render(0.25)
            synth.clear_midi()
            synth.add_midi_note(pitch, velocity, 0.0, hold)
            report_phase(f"演奏并采集，MIDI {pitch}")
            if not engine.render(hold + tail):
                raise RuntimeError("Host reported a rendering failure.")
            audio = np.array(engine.get_audio(), dtype=np.float32, copy=True)
            if audio.ndim != 2 or audio.shape[0] not in (1, 2) or audio.shape[1] == 0:
                raise ValueError("Host returned invalid audio dimensions.")
            if not np.isfinite(audio).all():
                raise ValueError("Host audio contains NaN / Infinity.")
            if abs(audio.shape[1] / sample_rate - hold - tail) > 0.01:
                raise ValueError("Host returned a duration inconsistent with the requested template.")
            rms = float(np.sqrt(np.mean(audio.astype(np.float64) ** 2)))
            peak = float(np.max(np.abs(audio)))
            needs_review |= rms < 0.00001 or peak >= 0.999
            actual_hold = int(hold * sample_rate) / sample_rate
            duration = audio.shape[1] / sample_rate
            notes.append({
                "MidiNote": pitch,
                "StartSeconds": start_frame / sample_rate,
                "DurationSeconds": duration,
                "ActualHoldSeconds": actual_hold,
                "ActualTailSeconds": max(0, duration - actual_hold),
                "ActualVelocity": velocity,
                "ActualNormalizedVelocity": velocity / 127,
            })
            segments.append(audio)
            start_frame += audio.shape[1]
            del synth, engine
            gc.collect()
        if any(segment.shape[0] != segments[0].shape[0] for segment in segments):
            raise ValueError("Plugin output channel count changed between pitches.")
        audio = np.concatenate(segments, axis=1)
        samples = np.ascontiguousarray(audio.T, dtype="<f4").tobytes(order="C")
        if output.exists():
            raise ValueError("Staging output already exists; refusing to overwrite.")
        channels = audio.shape[0]
        fmt = struct.pack("<HHIIHH", 3, channels, sample_rate, sample_rate * channels * 4, channels * 4, 32)
        with output.open("xb") as writer:
            writer.write(b"RIFF" + struct.pack("<I", len(samples) + 48) + b"WAVE")
            writer.write(b"fmt " + struct.pack("<I", len(fmt)) + fmt)
            writer.write(b"fact" + struct.pack("<II", 4, audio.shape[1]))
            writer.write(b"data" + struct.pack("<I", len(samples)) + samples)
    return {
        "Success": True,
        "OutputPath": str(output),
        "Message": ("默认启动基准：每音使用全新插件实例，未加载任何预制，保留原始 float32 输出。" if source is None else
                    "Serum 2 原生预制已转换为当前插件版本的 VST3 状态，并通过加载后状态回读核对；仍须人工核对音色及外部资源。" if serum2 else
                    "真实 VST2 FXP 已由宿主加载并演奏；新宿主组合须人工确认预制与外部资源，保留原始 float32 输出。") +
                   (" 存在静音或接近削波的音区。" if needs_review else ""),
        "NeedsReview": True,
        "Notes": notes,
        "AdapterVersion": (SERUM2_ADAPTER_VERSION if serum2 else ADAPTER_VERSION) + "/host-" + HOST_VERSION,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", type=Path)
    parser.add_argument("--result", required=True, type=Path)
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--probe-internal", action="store_true", help=argparse.SUPPRESS)
    arguments = parser.parse_args()
    if bool(arguments.request) == bool(arguments.probe or arguments.probe_internal) or (arguments.probe and arguments.probe_internal):
        parser.error("Use either --request <json> or --probe, together with --result <json>.")
    arguments.result.parent.mkdir(parents=True, exist_ok=True)
    phase_file = arguments.result.with_suffix(arguments.result.suffix + ".phase")
    sys.stderr.reconfigure(encoding="utf-8")
    probe_metadata = {"Success": False, "ProbeKind": "EmptyHostDsp", "AdapterVersion": ADAPTER_VERSION,
                      "CommercialPluginsLoaded": False, "AudioWritten": False}
    def report_phase(phase):
        phase_file.write_text(phase, encoding="utf-8")
        print(phase, file=sys.stderr, flush=True)
    def report_metadata(metadata):
        probe_metadata.update(metadata)
        write_result(arguments.result, probe_metadata)
    try:
        if arguments.probe:
            result = isolated_probe(arguments.result, report_phase)
        elif arguments.probe_internal:
            result = probe_host(report_phase, report_metadata)
        else:
            if arguments.request.stat().st_size > 1024 * 1024:
                raise ValueError("Request exceeds the 1 MB limit.")
            request = json.loads(arguments.request.read_text(encoding="utf-8-sig"))
            result = render(request, report_phase)
    except Exception as error:
        result = {**(probe_metadata if arguments.probe or arguments.probe_internal else {}),
                  "Success": False, "Message": str(error), "AdapterVersion": ADAPTER_VERSION}
    if arguments.probe or arguments.probe_internal:
        result["Finished"] = True
    write_result(arguments.result, result)
    return 0 if result["Success"] else 1


if __name__ == "__main__":
    sys.exit(main())
