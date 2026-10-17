"""Serum 2 request contracts; synthetic fixtures never load a native plugin.

Only the bridge's request and binary-header validation is exercised here. The
opaque XferJson payload is deliberately not represented as a real Serum sound;
processor-state conversion and readback have their own adapter validation.
"""

import copy
import hashlib
import json
from pathlib import Path
import plistlib
import struct
import tempfile
import unittest
from unittest import mock

from test_dawdreamer_worker import worker


def xfer_fixture(metadata=None):
    """Build only an envelope fixture, with an intentionally opaque payload."""
    payload = b"TEST ONLY: opaque payload, never decompressed or rendered"
    if metadata is None:
        metadata = {"fileType": "SerumPreset", "product": "Serum2", "vendor": "Xfer Records",
                    "productVersion": "2.0.23", "version": 2,
                    "hash": hashlib.md5(payload, usedforsecurity=False).hexdigest()}
    encoded = json.dumps(metadata, separators=(",", ":")).encode("utf-8")
    return b"XferJson\x00" + struct.pack("<Q", len(encoded)) + encoded + struct.pack("<II", 1, 2) + payload


def write_pe(path, machine=0x8664):
    """Write the PE signature/machine header needed by the architecture guard."""
    path.parent.mkdir(parents=True, exist_ok=True)
    raw = bytearray(0x86)
    raw[:2] = b"MZ"
    struct.pack_into("<I", raw, 0x3C, 0x80)
    raw[0x80:0x84] = b"PE\x00\x00"
    struct.pack_into("<H", raw, 0x84, machine)
    path.write_bytes(raw)
    return path


def write_mac_bundle(path, machine=0x0100000C):
    """Match the existing Mac bridge fixture without creating a loadable dylib."""
    contents = path / "Contents"
    (contents / "MacOS").mkdir(parents=True)
    (contents / "Info.plist").write_bytes(plistlib.dumps({"CFBundleExecutable": "TEST-ONLY"}))
    (contents / "MacOS" / "TEST-ONLY").write_bytes(struct.pack("<IIIIIIII", 0xFEEDFACF, machine, 0, 6, 0, 0, 0, 0))
    return path


class Serum2BridgeTests(unittest.TestCase):
    def setUp(self):
        root = Path(__file__).resolve().parents[2] / "artifacts" / "tests"
        root.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="TEST-ONLY-serum2-bridge-", dir=root)
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.binary = self.directory / "TEST-ONLY-Serum2.vst3"
        self.binary.write_bytes(b"TEST ONLY parser placeholder, never loaded")
        self.source = self.directory / "TEST-ONLY.SerumPreset"
        self.source.write_bytes(xfer_fixture())
        self.request = {
            "Preset": {"Synth": 2, "SourcePath": str(self.source),
                       "ContentHash": hashlib.sha256(self.source.read_bytes()).hexdigest()},
            "Plugin": {"Synth": 2, "PluginPath": str(self.binary), "Version": "2.0.23"},
            "Template": {"LowNote": 36, "MiddleNote": 60, "HighNote": 84, "NoteOrder": 1,
                         "Velocity": 89, "HoldSeconds": 0.5, "TailSeconds": 0.08, "SampleRate": 44100, "Bpm": 120},
        }

    def parse(self, request=None):
        with mock.patch.object(worker, "check_plugin_architecture") as architecture, \
                mock.patch.object(worker, "load_host", side_effect=AssertionError("A request contract loaded a native host")):
            result = worker.validate_request(copy.deepcopy(self.request if request is None else request))
        architecture.assert_called_once()
        return result

    def test_native_preset_envelope_preserves_bytes_identity_and_note_order(self):
        source, binary, pitches, raw = self.parse()
        self.assertEqual(self.source.resolve(), source)
        self.assertEqual(self.binary.resolve(), binary)
        self.assertEqual([60, 84, 36], pitches)
        self.assertEqual(self.source.read_bytes(), raw)

    def test_source_sha256_must_match_when_provided(self):
        request = copy.deepcopy(self.request)
        request["Preset"]["ContentHash"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "changed after scanning"):
            self.parse(request)
        request["Preset"]["ContentHash"] = self.request["Preset"]["ContentHash"].upper()
        self.assertEqual(self.source.read_bytes(), self.parse(request)[3])
        request["Preset"].pop("ContentHash")
        self.assertEqual(self.source.read_bytes(), self.parse(request)[3])

    def test_source_change_after_scan_is_rejected(self):
        self.source.write_bytes(self.source.read_bytes() + b" changed after scan")
        with self.assertRaisesRegex(ValueError, "changed after scanning"):
            self.parse()

    def test_corrupt_or_missing_xfer_magic_is_rejected(self):
        for raw in (b"", b"XferJson\x00", b"CcnK" + bytes(80), b"BadMagic!" + xfer_fixture()[9:]):
            with self.subTest(raw=raw[:16]):
                self.source.write_bytes(raw)
                with self.assertRaisesRegex(ValueError, "XferJson envelope"):
                    self.parse()

    def test_truncated_or_oversized_metadata_is_rejected(self):
        for length in (0, 1024 * 1024 + 1, len(xfer_fixture())):
            with self.subTest(length=length):
                raw = bytearray(xfer_fixture())
                struct.pack_into("<Q", raw, 9, length)
                self.source.write_bytes(raw)
                with self.assertRaisesRegex(ValueError, "metadata"):
                    self.parse()

    def test_metadata_requires_the_native_serum2_product_and_file_type(self):
        invalid = [[], None, "not metadata", {"fileType": "OtherPreset", "product": "Serum2"},
                   {"fileType": "SerumPreset", "product": "Serum1"},
                   {"fileType": "SerumPreset"}, {"product": "Serum2"}]
        for metadata in invalid:
            with self.subTest(metadata=metadata):
                # Explicit JSON null differs from the helper's default metadata.
                raw = xfer_fixture(metadata) if metadata is not None else b"XferJson\x00" + struct.pack("<Q", 4) + b"null" + bytes(16)
                self.source.write_bytes(raw)
                with self.assertRaisesRegex(ValueError, "metadata"):
                    self.parse()
        raw = b"XferJson\x00" + struct.pack("<Q", 7) + b"{broken" + bytes(16)
        self.source.write_bytes(raw)
        with self.assertRaises(ValueError):
            self.parse()

    def test_legacy_fxp_is_rejected_instead_of_forwarded_to_vst3(self):
        source = self.directory / "legacy.fxp"
        source.write_bytes(b"CcnK" + bytes(80))
        request = copy.deepcopy(self.request)
        request["Preset"]["SourcePath"] = str(source)
        with self.assertRaisesRegex(ValueError, "import legacy FXP"):
            self.parse(request)

    def test_serum2_rejects_vst2_and_effect_plugin_paths(self):
        for name, error in (("Serum2.dll", "native VST3"), ("SerumFX.vst3", "effect")):
            with self.subTest(name=name):
                path = self.directory / name
                path.write_bytes(b"TEST ONLY")
                request = copy.deepcopy(self.request)
                request["Plugin"]["PluginPath"] = str(path)
                with self.assertRaisesRegex(ValueError, error):
                    self.parse(request)

    def test_mismatched_synths_and_spire_remain_unsupported(self):
        for preset_synth, plugin_synth in ((2, 1), (1, 2), (5, 5), (4, 4), (0, 0)):
            request = copy.deepcopy(self.request)
            request["Preset"]["Synth"], request["Plugin"]["Synth"] = preset_synth, plugin_synth
            with self.subTest(preset_synth=preset_synth, plugin_synth=plugin_synth), \
                    self.assertRaisesRegex(ValueError, "supports Serum 1"):
                self.parse(request)

    def test_bank_or_archive_entries_are_rejected(self):
        for key, value in (("BankSlot", 0), ("InternalPath", "Presets/test.SerumPreset")):
            request = copy.deepcopy(self.request)
            request["Preset"][key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "verified bank adapter"):
                self.parse(request)

    def test_initialization_baseline_needs_no_source_and_rejects_source_identity(self):
        request = copy.deepcopy(self.request)
        request["IsInitializationBaseline"] = True
        request["Preset"] = {"Synth": 2}
        source, binary, pitches, raw = self.parse(request)
        self.assertIsNone(source)
        self.assertIsNone(raw)
        self.assertEqual(self.binary.resolve(), binary)
        self.assertEqual([60, 84, 36], pitches)
        for key, value in (("SourcePath", str(self.source)), ("ContentHash", "0" * 64)):
            invalid = copy.deepcopy(request)
            invalid["Preset"][key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "must not contain a source"):
                self.parse(invalid)

    def test_mac_vst3_bundle_passes_matching_native_architecture_guard(self):
        for machine, host in ((0x0100000C, "arm64"), (0x01000007, "x86_64")):
            with self.subTest(host=host):
                bundle = write_mac_bundle(self.directory / (host + ".vst3"), machine)
                request = copy.deepcopy(self.request)
                request["Plugin"]["PluginPath"] = str(bundle)
                with mock.patch.object(worker.sys, "platform", "darwin"), \
                        mock.patch.object(worker.platform, "machine", return_value=host):
                    self.assertEqual(bundle.resolve(), worker.validate_request(request)[1])
                other_host = "x86_64" if host == "arm64" else "arm64"
                with mock.patch.object(worker.sys, "platform", "darwin"), \
                        mock.patch.object(worker.platform, "machine", return_value=other_host), \
                        self.assertRaisesRegex(ValueError, "does not match"):
                    worker.check_plugin_architecture(bundle)

    def test_windows_vst3_raw_and_single_binary_bundle_require_x64_pe(self):
        raw = write_pe(self.directory / "windows-raw.vst3")
        bundle = self.directory / "windows-bundle.vst3"
        native = write_pe(bundle / "Contents" / "x86_64-win" / "Serum2.vst3")
        with mock.patch.object(worker.sys, "platform", "win32"):
            for path in (raw, bundle):
                with self.subTest(path=path.name):
                    request = copy.deepcopy(self.request)
                    request["Plugin"]["PluginPath"] = str(path)
                    self.assertEqual(path.resolve(), worker.validate_request(request)[1])
            write_pe(native, machine=0x14C)
            with self.assertRaisesRegex(ValueError, "32-bit"):
                worker.check_plugin_architecture(bundle)
            write_pe(native)
            write_pe(native.with_name("ambiguous.vst3"))
            with self.assertRaisesRegex(ValueError, "one unambiguous"):
                worker.check_plugin_architecture(bundle)


if __name__ == "__main__":
    unittest.main()
