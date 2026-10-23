"""Bounded Serum 2 preset conversion and processor-state load verification.

The optional MIT-licensed serum2-preset-loader performs the structural mapping.
Its version defaults are intentionally not used: a fresh state captured from the
installed plugin supplies the processor schema and product version instead.
"""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
from importlib.metadata import PackageNotFoundError, version as package_version
import io
import json
import math
import re
import struct
from typing import Any
import xml.etree.ElementTree as ET


MAX_SOURCE_BYTES = 64 * 1024 * 1024
MAX_METADATA_BYTES = 1024 * 1024
MAX_DECOMPRESSED_BYTES = 128 * 1024 * 1024
MAX_STATE_BYTES = 128 * 1024 * 1024
MAX_TREE_NODES = 4_000_000
MAX_TREE_DEPTH = 64
_XFER_MAGIC = b"XferJson\x00"
_JUCE_MAGIC = 0x21324356
_PINNED_CONVERTER = "0.1.3"


@dataclass(frozen=True)
class Serum2StateProof:
    plugin_version: str
    processor_format_version: int | float
    wrapper_version: int
    expected: dict
    fresh: dict


def _dependencies():
    try:
        actual = package_version("serum2-preset-loader")
        if actual != _PINNED_CONVERTER:
            raise ValueError(f"Serum 2 conversion requires serum2-preset-loader=={_PINNED_CONVERTER}; found {actual}")
        import cbor2
        import zstandard
        from serum2_preset_loader import converter, wrappers
    except (ImportError, PackageNotFoundError) as error:
        raise ValueError("Serum 2 conversion needs the pinned optional requirements-serum2.txt dependencies") from error
    return cbor2, zstandard, converter, wrappers


def _bytes(blob: bytes, maximum: int, label: str) -> None:
    if not isinstance(blob, bytes) or not blob or len(blob) > maximum:
        raise ValueError(f"{label} must contain 1 to {maximum} bytes")


def _version(value: Any) -> tuple[int, ...]:
    if not isinstance(value, str) or re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,3}", value.strip()) is None:
        raise ValueError("Serum 2 productVersion must be an explicit numeric version")
    parts = tuple(int(part) for part in value.strip().split("."))
    if parts[0] != 2 or any(part > 65535 for part in parts):
        raise ValueError("Only explicit Serum 2 product versions are supported")
    return parts + (0,) * (4 - len(parts))


def _format_version(value: Any) -> int | float:
    if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
        raise ValueError("Serum 2 processor format version is missing or invalid")
    return value


def _json_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("XferJson metadata contains duplicate keys")
        result[key] = value
    return result


def _decompress(compressed: bytes, expected_size: int, zstd) -> bytes:
    if expected_size <= 0 or expected_size > MAX_DECOMPRESSED_BYTES:
        raise ValueError("XferJson decompressed size exceeds the 128 MiB limit")
    try:
        declared = zstd.frame_content_size(compressed)
        # The Python frame_content_size API returns -1 for unknown, while the
        # public CONTENTSIZE_UNKNOWN constant uses the unsigned C sentinel.
        if declared not in (-1, zstd.CONTENTSIZE_UNKNOWN) and declared != expected_size:
            raise ValueError("XferJson zstd frame size does not match its declared payload size")
        # Known frame sizes bypass max_output_size, so validate their size above
        # before allocation. Unknown-size frames are bounded by expected_size.
        # The single-frame API requires the complete frame (including checksum),
        # unlike stream_reader, which permits EOF after partial frame output.
        decoded = zstd.ZstdDecompressor(max_window_size=MAX_DECOMPRESSED_BYTES // 1024).decompress(
            compressed, max_output_size=expected_size, allow_extra_data=False)
        if len(decoded) != expected_size:
            raise ValueError("XferJson decompressed payload size does not match its header")
        return decoded
    except zstd.ZstdError as error:
        raise ValueError("XferJson zstd payload is incomplete, invalid, oversized, or does not match its header") from error


def _validate_tree(value: Any) -> None:
    pending, nodes = [(iter([value]), 0)], 0
    while pending:
        iterator, depth = pending[-1]
        try:
            current = next(iterator)
        except StopIteration:
            pending.pop()
            continue
        nodes += 1
        if nodes > MAX_TREE_NODES or depth > MAX_TREE_DEPTH:
            raise ValueError("Serum 2 state is too deeply nested or contains too many values")
        if isinstance(current, dict):
            if any(not isinstance(key, str) for key in current):
                raise ValueError("Serum 2 state maps must use string keys")
            pending.append((iter(current.values()), depth + 1))
        elif isinstance(current, list):
            pending.append((iter(current), depth + 1))
        elif isinstance(current, float):
            if not math.isfinite(current):
                raise ValueError("Serum 2 state contains a nonfinite number")
        elif current is not None and type(current) not in (str, bytes, int, bool):
            raise ValueError("Serum 2 state contains an unsupported CBOR value")


def _read_xfer(blob: bytes, cbor, zstd) -> tuple[dict, int, dict]:
    _bytes(blob, MAX_SOURCE_BYTES, "XferJson source")
    if len(blob) < 26 or blob[:9] != _XFER_MAGIC:
        raise ValueError("Expected a complete Serum 2 XferJson container")
    length = struct.unpack_from("<Q", blob, 9)[0]
    if length == 0 or length > MAX_METADATA_BYTES or length > len(blob) - 25:
        raise ValueError("XferJson metadata is truncated or exceeds the 1 MiB limit")
    try:
        metadata = json.loads(blob[17:17 + length].decode("utf-8"), object_pairs_hook=_json_object)
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise ValueError("XferJson metadata is not valid UTF-8 JSON") from error
    if not isinstance(metadata, dict):
        raise ValueError("XferJson metadata must be an object")
    size, wrapper = struct.unpack_from("<II", blob, 17 + length)
    if wrapper != 2:
        raise ValueError(f"Unsupported Serum 2 XferJson wrapper version {wrapper}; expected 2")
    compressed = blob[25 + length:]
    digest = metadata.get("hash")
    if digest is not None and (not isinstance(digest, str) or
            digest.lower() != hashlib.md5(compressed, usedforsecurity=False).hexdigest()):
        raise ValueError("XferJson compressed payload hash does not match its metadata")
    decoded = _decompress(compressed, size, zstd)
    try:
        source = io.BytesIO(decoded)
        value = cbor.CBORDecoder(source).decode()
        if source.read(1):
            raise ValueError("Serum 2 CBOR contains trailing data")
    except (cbor.CBORDecodeError, RecursionError) as error:
        raise ValueError("Serum 2 CBOR payload is malformed") from error
    if not isinstance(value, dict):
        raise ValueError("Serum 2 CBOR root must be a map")
    _validate_tree(value)
    return metadata, wrapper, value


def _identity(metadata: dict, payload: dict, *, processor: bool) -> None:
    if metadata.get("product") != "Serum2" or metadata.get("vendor") != "Xfer Records":
        raise ValueError("XferJson does not identify the Serum2 product from Xfer Records")
    expected_key, expected_value = ("component", "processor") if processor else ("fileType", "SerumPreset")
    if metadata.get(expected_key) != expected_value:
        raise ValueError(f"XferJson must identify {expected_key}={expected_value}")
    for key in ("product", "vendor", expected_key):
        if key in payload and payload[key] != metadata[key]:
            raise ValueError(f"Serum 2 CBOR identity conflicts with metadata: {key}")
    if "productVersion" in payload and _version(payload["productVersion"]) != _version(metadata.get("productVersion")):
        raise ValueError("Serum 2 CBOR productVersion conflicts with its metadata")


def _read_juce(blob: bytes, cbor, zstd, wrappers) -> tuple[dict, int, dict]:
    _bytes(blob, MAX_STATE_BYTES, "JUCE VST3 state")
    if len(blob) < 9:
        raise ValueError("JUCE VST3 state is truncated")
    magic, length = struct.unpack_from("<II", blob)
    if magic != _JUCE_MAGIC or length == 0 or len(blob) not in (8 + length, 9 + length):
        raise ValueError("JUCE VST3 state magic or XML length is invalid")
    if len(blob) == 9 + length and blob[-1] != 0:
        raise ValueError("JUCE VST3 state has an invalid terminator")
    xml = blob[8:8 + length]
    if b"<!DOCTYPE" in xml.upper() or b"<!ENTITY" in xml.upper():
        raise ValueError("JUCE VST3 state must not contain XML entities or a DTD")
    try:
        root = ET.fromstring(xml.decode("utf-8"))
    except (UnicodeError, ET.ParseError, RecursionError) as error:
        raise ValueError("JUCE VST3 state XML is invalid") from error
    components = root.findall("IComponent")
    if root.tag != "VST3PluginState" or len(components) != 1 or list(components[0]):
        raise ValueError("JUCE VST3 state must contain exactly one IComponent")
    text = components[0].text or ""
    prefix = text.partition(".")[0]
    if not prefix.isascii() or not prefix.isdecimal() or len(prefix) > 10 or int(prefix) > MAX_SOURCE_BYTES:
        raise ValueError("JUCE IComponent length exceeds the source limit or is invalid")
    try:
        component = wrappers.juce_memoryblock_b64decode(text)
    except (ValueError, UnicodeError) as error:
        raise ValueError("JUCE IComponent encoding is invalid") from error
    metadata, wrapper, payload = _read_xfer(component, cbor, zstd)
    _identity(metadata, payload, processor=True)
    return metadata, wrapper, payload


_TOPLEVEL_METADATA = frozenset({"component", "fileType", "hash", "product", "vendor", "url", "productVersion",
    "version", "presetName", "presetAuthor", "presetDescription", "tags", "killEnvsGracefullyCompat"})
_TRANSIENT_FIELDS = frozenset({"lfophasor", "curveDisplayName"})


def _meaningful(value: Any, *, root: bool = False) -> Any:
    if isinstance(value, dict):
        return {key: ({} if key == "plainParams" and item == "default" else _meaningful(item)) for key, item in value.items()
                if not key.startswith("kUI") and key not in _TRANSIENT_FIELDS and not (root and key in _TOPLEVEL_METADATA)}
    if isinstance(value, list):
        return [_meaningful(item) for item in value]
    return value


def _uses_default_sentinel(value: Any) -> bool:
    if isinstance(value, dict):
        return value.get("plainParams") == "default" or any(_uses_default_sentinel(item) for item in value.values())
    return isinstance(value, list) and any(_uses_default_sentinel(item) for item in value)


def _adapt_native_defaults(converted: dict, preset: dict, fresh: dict) -> None:
    """Adapt the converter's 2.1.4-only defaults to the captured native schema."""
    if _uses_default_sentinel(fresh):
        def restore(value):
            if isinstance(value, dict):
                for key, item in value.items():
                    if key == "plainParams" and item == {}:
                        value[key] = "default"
                    else:
                        restore(item)
            elif isinstance(value, list):
                for item in value:
                    restore(item)
        restore(converted)
    if "killEnvsGracefullyCompat" not in preset:
        if "killEnvsGracefullyCompat" in fresh:
            converted["killEnvsGracefullyCompat"] = fresh["killEnvsGracefullyCompat"]
        else:
            converted.pop("killEnvsGracefullyCompat", None)
    for key, module in converted.items():
        if re.fullmatch(r"MidiClip[0-9]+", key) and isinstance(module, dict):
            # Native 2.0.23 readback drops this preset-browser grid height.
            # It is UI layout alongside the converter's gridWidth_Beats and
            # gridYOffset_Rows exclusions; MIDI clip contents remain intact.
            module.pop("gridHeight_Rows", None)
        if re.fullmatch(r"Arp[0-9]+", key) and isinstance(module, dict) and "activeClip" not in preset.get(key, {}):
            template = fresh.get(key, {})
            if isinstance(template, dict) and "activeClip" in template:
                module["activeClip"] = template["activeClip"]
            else:
                module.pop("activeClip", None)


def _difference(expected: Any, actual: Any, path: str = "processor") -> str | None:
    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            return path
        for key, value in expected.items():
            # Empty default-parameter maps carry no evidence of an actual load.
            if isinstance(value, dict) and not value:
                continue
            if key not in actual:
                return f"{path}.{key}"
            mismatch = _difference(value, actual[key], f"{path}.{key}")
            if mismatch:
                return mismatch
        return None
    if isinstance(expected, list):
        if not isinstance(actual, list) or len(expected) != len(actual):
            return path
        for index, value in enumerate(expected):
            mismatch = _difference(value, actual[index], f"{path}[{index}]")
            if mismatch:
                return mismatch
        return None
    if type(expected) in (int, float) and type(actual) in (int, float):
        return None if math.isclose(expected, actual, rel_tol=1e-6, abs_tol=1e-8) else path
    return None if type(expected) is type(actual) and expected == actual else path


def prepare_serum2_state(preset_bytes: bytes, fresh_state_bytes: bytes, plugin_version: str) -> tuple[bytes, Serum2StateProof]:
    """Convert a native preset using schema markers from this installed plugin.

    The returned proof must be checked against save_state *after* load_state and
    before rendering. A successful load_state call alone is not load evidence.
    """
    _bytes(preset_bytes, MAX_SOURCE_BYTES, "Serum 2 preset")
    installed = _version(plugin_version)
    cbor, zstd, converter, wrappers = _dependencies()
    metadata, wrapper, preset = _read_xfer(preset_bytes, cbor, zstd)
    _identity(metadata, preset, processor=False)
    fresh_metadata, fresh_wrapper, fresh = _read_juce(fresh_state_bytes, cbor, zstd, wrappers)
    if _version(fresh_metadata.get("productVersion")) != installed:
        raise ValueError("Fresh Serum 2 state does not match the configured installed plugin version")
    if _version(metadata.get("productVersion")) > installed:
        raise ValueError(f"需要更新 Serum 2：当前插件版本 {fresh_metadata['productVersion']} 低于音色制作版本 {metadata['productVersion']}，请更新后重试。")
    if wrapper != fresh_wrapper:
        raise ValueError("Preset and installed Serum 2 XferJson wrappers differ")
    processor_version = _format_version(fresh.get("version"))
    if _format_version(fresh_metadata.get("version")) != processor_version:
        raise ValueError("Fresh Serum 2 processor schema markers are inconsistent")
    if _format_version(metadata.get("version")) > processor_version:
        raise ValueError(f"需要更新 Serum 2：音色使用更高的状态格式版本（音色 {metadata['version']}，当前插件 {processor_version}），请更新后重试。")

    converted = converter.preset_cbor_to_processor_cbor(preset)
    _adapt_native_defaults(converted, preset, fresh)
    converted["productVersion"] = fresh_metadata["productVersion"]
    converted["version"] = processor_version
    expected = _meaningful(converted, root=True)
    fresh_meaningful = _meaningful(fresh, root=True)
    if _difference(expected, fresh_meaningful) is None:
        raise ValueError("The preset has no verifiable parameter or resource differences from the fresh Init state")
    encoded = cbor.dumps(converted)
    if len(encoded) > MAX_DECOMPRESSED_BYTES:
        raise ValueError("Converted Serum 2 state exceeds the decompressed size limit")
    compressed = zstd.ZstdCompressor().compress(encoded)
    processor_metadata = dict(fresh_metadata)
    processor_metadata["hash"] = hashlib.md5(compressed, usedforsecurity=False).hexdigest()
    component = wrappers.wrap_xferjson_precompressed(processor_metadata, compressed, uncompressed_size=len(encoded), version=fresh_wrapper)
    _bytes(component, MAX_SOURCE_BYTES, "Converted Serum 2 processor state")
    # Follow the converter's documented component-only envelope. The fresh Init
    # controller state does not describe the requested target preset.
    state = wrappers.build_juce_vst3_state(component)
    _bytes(state, MAX_STATE_BYTES, "Converted JUCE VST3 state")
    proof = Serum2StateProof(fresh_metadata["productVersion"], processor_version, fresh_wrapper, expected, fresh_meaningful)
    return state, proof


def verify_serum2_state(after_state_bytes: bytes, proof: Serum2StateProof) -> None:
    """Reject a silent Init fallback or a partially applied parameter/resource set."""
    if not isinstance(proof, Serum2StateProof):
        raise ValueError("A Serum 2 state proof from prepare_serum2_state is required")
    cbor, zstd, _, wrappers = _dependencies()
    metadata, wrapper, payload = _read_juce(after_state_bytes, cbor, zstd, wrappers)
    if _version(metadata.get("productVersion")) != _version(proof.plugin_version) or wrapper != proof.wrapper_version or \
            _format_version(payload.get("version")) != proof.processor_format_version or \
            _format_version(metadata.get("version")) != proof.processor_format_version:
        raise ValueError("Serum 2 readback state version changed after loading")
    actual = _meaningful(payload, root=True)
    difference = _difference(proof.expected, actual)
    if difference is not None:
        if _difference(proof.fresh, actual) is None and _difference(actual, proof.fresh) is None:
            raise ValueError("Serum 2 silently retained its fresh Init state instead of loading the requested preset")
        raise ValueError(f"Serum 2 did not retain the requested preset parameter/resource state at {difference}")
