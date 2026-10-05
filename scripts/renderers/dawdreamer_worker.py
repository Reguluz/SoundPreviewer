"""Real VST2 FXP rendering through a pinned, optional local DawDreamer host."""

import argparse
import gc
import hashlib
import importlib.metadata
import json
import math
import os
from pathlib import Path
import struct
import sys
import tempfile
import time


ADAPTER_VERSION = "dawdreamer-fxp-v1"
HOST_VERSION = "0.8.3"
NUMPY_VERSION = "1.26.4"


def check_plugin_architecture(plugin_path):
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


def validate_request(request):
    preset = request["Preset"]
    plugin = request["Plugin"]
    template = request["Template"]
    if preset["Synth"] not in (1, 3) or plugin["Synth"] != preset["Synth"]:
        raise ValueError("DawDreamer bridge currently supports Serum 1 and Sylenth1 VST2 FXP only.")
    if preset.get("BankSlot") is not None or preset.get("InternalPath"):
        raise ValueError("Bank slots and package entries require a verified bank adapter; export a standalone FXP first.")
    source = Path(preset["SourcePath"]).resolve(strict=True)
    binary = Path(plugin["PluginPath"]).resolve(strict=True)
    if source.suffix.lower() != ".fxp" or binary.suffix.lower() != ".dll":
        raise ValueError("FXP requires the VST2 instrument DLL. VST3, SerumPreset and FXB banks are not supported by this bridge.")
    if "serumfx" in binary.stem.lower():
        raise ValueError("SerumFX is an effect; select the Serum instrument DLL.")
    if source.stat().st_size > 64 * 1024 * 1024:
        raise ValueError("Preset exceeds the 64 MB limit.")
    check_plugin_architecture(binary)
    pitches = [template["LowNote"], template["MiddleNote"], template["HighNote"]]
    if any(not isinstance(pitch, int) or not 0 <= pitch <= 127 for pitch in pitches):
        raise ValueError("MIDI pitches must be integers in [0, 127].")
    if not isinstance(template["Velocity"], int) or not 1 <= template["Velocity"] <= 127:
        raise ValueError("Velocity must be an integer in [1, 127].")
    hold, tail = float(template["HoldSeconds"]), float(template["TailSeconds"])
    if not math.isfinite(hold) or not 0.1 <= hold <= 30 or not math.isfinite(tail) or not 0 <= tail <= 30:
        raise ValueError("Hold / tail durations are outside the supported range.")
    if template["SampleRate"] not in (44100, 48000) or not 20 <= template["Bpm"] <= 300:
        raise ValueError("Sample rate or BPM is outside the supported range.")
    raw = source.read_bytes()
    if raw[:4] != b"CcnK" or raw[8:12] not in (b"FPCh", b"FxCk"):
        raise ValueError("Preset is not a standalone VST2 FXP program.")
    source_hash = hashlib.sha256(raw).hexdigest()
    if preset.get("ContentHash") and source_hash.lower() != preset["ContentHash"].lower():
        raise ValueError("Preset changed after scanning; scan it again before rendering.")
    return source, binary, pitches, raw


def render(request, report_phase=lambda phase: None):
    source, binary, pitches, raw = validate_request(request)
    if struct.calcsize("P") != 8 or sys.version_info[:2] not in ((3, 11), (3, 12)):
        raise ValueError("Pinned DawDreamer bridge requires 64-bit Python 3.11 or 3.12.")
    try:
        installed = importlib.metadata.version("dawdreamer")
        numpy_installed = importlib.metadata.version("numpy")
    except importlib.metadata.PackageNotFoundError as error:
        raise RuntimeError("Optional host is missing. Install scripts/renderers/requirements.txt into a local Python environment.") from error
    if installed != HOST_VERSION or numpy_installed != NUMPY_VERSION:
        raise ValueError(f"Expected DawDreamer {HOST_VERSION} and NumPy {NUMPY_VERSION}; found {installed} / {numpy_installed}.")

    # DawDreamer loads LLVM; import it before NumPy or other numerical runtimes.
    report_phase("导入本地宿主库")
    import dawdreamer as daw
    import numpy as np

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
        snapshot = Path(temporary) / source.name
        snapshot.write_bytes(raw)
        for pitch in pitches:
            report_phase(f"创建宿主，MIDI {pitch}")
            engine = daw.RenderEngine(sample_rate, 128)
            engine.set_bpm(float(template["Bpm"]))
            report_phase(f"实例化 VST2 音源，MIDI {pitch}")
            synth = engine.make_plugin_processor("synth", str(binary))
            if synth.get_num_output_channels() not in (1, 2):
                raise ValueError("Plugin must expose one or two output channels.")
            report_phase(f"加载 FXP，MIDI {pitch}")
            if not synth.load_preset(str(snapshot)):
                raise ValueError("Host rejected the FXP; target preset was not loaded.")
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
        "Message": "真实 VST2 FXP 已由宿主加载并演奏；新宿主组合须人工确认预制与外部资源，保留原始 float32 输出。" +
                   (" 存在静音或接近削波的音区。" if needs_review else ""),
        "NeedsReview": True,
        "Notes": notes,
        "AdapterVersion": ADAPTER_VERSION + "/host-" + HOST_VERSION,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--result", required=True, type=Path)
    arguments = parser.parse_args()
    arguments.result.parent.mkdir(parents=True, exist_ok=True)
    phase_file = arguments.result.with_suffix(arguments.result.suffix + ".phase")
    sys.stderr.reconfigure(encoding="utf-8")
    def report_phase(phase):
        phase_file.write_text(phase, encoding="utf-8")
        print(phase, file=sys.stderr, flush=True)
    try:
        if arguments.request.stat().st_size > 1024 * 1024:
            raise ValueError("Request exceeds the 1 MB limit.")
        request = json.loads(arguments.request.read_text(encoding="utf-8-sig"))
        result = render(request, report_phase)
    except Exception as error:
        result = {"Success": False, "Message": str(error), "AdapterVersion": ADAPTER_VERSION}
    temporary = arguments.result.with_suffix(arguments.result.suffix + ".tmp")
    temporary.write_text(json.dumps(result, ensure_ascii=False, allow_nan=False), encoding="utf-8")
    os.replace(temporary, arguments.result)
    return 0 if result["Success"] else 1


if __name__ == "__main__":
    sys.exit(main())
