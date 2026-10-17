"""Pure Serum 2 state contracts; these fixtures never load a plugin."""
import copy
import hashlib
import importlib.util
import struct
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

import serum2_state as state


_OPTIONAL = all(importlib.util.find_spec(name) is not None for name in ("cbor2", "zstandard", "serum2_preset_loader"))


class Serum2InputTests(unittest.TestCase):
    def test_versions_must_identify_serum2_and_have_explicit_numeric_components(self):
        self.assertEqual((2, 0, 23, 0), state._version("2.0.23"))
        for value in ("", "2", "1.0.0", "2.0.23beta", "2.70000", None, 2):
            with self.subTest(value=value), self.assertRaises(ValueError):
                state._version(value)

    def test_oversized_source_is_rejected_before_optional_dependencies_are_loaded(self):
        with mock.patch.object(state, "MAX_SOURCE_BYTES", 4), \
                mock.patch.object(state, "_dependencies", side_effect=AssertionError("dependency import should not occur")):
            with self.assertRaisesRegex(ValueError, "1 to 4 bytes"):
                state.prepare_serum2_state(b"12345", b"fresh", "2.0.23")


@unittest.skipUnless(_OPTIONAL, "Serum 2 optional CBOR/zstd/converter dependencies are not installed")
class Serum2StateTests(unittest.TestCase):
    def setUp(self):
        self.cbor, self.zstd, _, self.wrappers = state._dependencies()
        self.native_meta = {"component": "processor", "product": "Serum2", "vendor": "Xfer Records",
                            "productVersion": "2.0.23", "version": 9.0, "url": "https://xferrecords.com/"}
        self.native = {**self.native_meta,
                       "Oscillator0": {"plainParams": "default", "WTOsc0": {"plainParams": "default", "relativePathToWT": "Factory/Init.wav"}},
                       "Env0": {"plainParams": "default"}, "Arp0": {"plainParams": "default", "activeClip": -1},
                       "FXRack0": {"plainParams": "default", "FX": []}}
        self.preset_meta = {"fileType": "SerumPreset", "product": "Serum2", "vendor": "Xfer Records",
                            "productVersion": "2.0.11", "version": 4.0, "presetName": "TEST-ONLY"}
        self.preset = {**self.preset_meta,
                       "Oscillator0": {"plainParams": {"kParamVolume": 0.7},
                                       "WTOsc0": {"plainParams": "default", "relativePathToWT": "Factory/Test.wav", "sampleData": b"sample"}},
                       "Env0": {"plainParams": {"kParamAttack": 0.005}},
                       "Arp0": {"plainParams": "default"},
                       "FXRack0": {"plainParams": "default", "displayName": "UI only", "FX": []},
                       "Macro0": {"plainParams": "default", "name": "UI only"},
                       "FutureModule0": {"plainParams": {"kParamTest": 0.25}, "customResource": {"bytes": b"kept", "data": [1.0, 2.0]}}}

    def wrap(self, metadata, payload, *, wrapper=2, raw=None, content_size=True):
        encoded = self.cbor.dumps(payload) if raw is None else raw
        compressed = self.zstd.ZstdCompressor(write_content_size=content_size).compress(encoded)
        meta = dict(metadata, hash=hashlib.md5(compressed).hexdigest())
        return self.wrappers.wrap_xferjson_precompressed(meta, compressed, uncompressed_size=len(encoded), version=wrapper)

    def native_state(self, payload=None, metadata=None):
        return self.wrappers.build_juce_vst3_state(self.wrap(metadata or self.native_meta, self.native if payload is None else payload), b"TEST-ONLY-controller")

    def prepare(self):
        return state.prepare_serum2_state(self.wrap(self.preset_meta, self.preset), self.native_state(), "2.0.23")

    def read(self, blob):
        return state._read_juce(blob, self.cbor, self.zstd, self.wrappers)

    def test_native_version_schema_defaults_and_unknown_resource_fields_are_preserved(self):
        converted, proof = self.prepare()
        meta, wrapper, payload = self.read(converted)
        self.assertEqual(("2.0.23", 9.0, 2), (meta["productVersion"], payload["version"], wrapper))
        self.assertIsInstance(payload["version"], float)
        self.assertEqual("default", payload["Macro0"]["plainParams"])
        self.assertEqual(-1, payload["Arp0"]["activeClip"])
        self.assertNotIn("killEnvsGracefullyCompat", payload)
        self.assertNotIn("displayName", payload["FXRack0"])
        self.assertNotIn("name", payload["Macro0"])
        self.assertEqual(self.preset["FutureModule0"], payload["FutureModule0"])
        self.assertEqual(b"sample", payload["Oscillator0"]["WTOsc0"]["sampleData"])
        length = struct.unpack_from("<I", converted, 4)[0]
        self.assertIsNone(ET.fromstring(converted[8:8 + length]).find("IEditController"))
        state.verify_serum2_state(converted, proof)

    def test_newer_native_schema_keeps_native_empty_defaults_and_compatibility_flag(self):
        self.native_meta.update(productVersion="2.1.4", version=10.0)
        self.native.update(productVersion="2.1.4", version=10.0, killEnvsGracefullyCompat=True)
        for module in self.native.values():
            if isinstance(module, dict) and module.get("plainParams") == "default":
                module["plainParams"] = {}
                if "WTOsc0" in module:
                    module["WTOsc0"]["plainParams"] = {}
        self.native["Arp0"]["activeClip"] = 0
        converted, proof = state.prepare_serum2_state(self.wrap(self.preset_meta, self.preset), self.native_state(), "2.1.4")
        _, _, payload = self.read(converted)
        self.assertEqual({}, payload["Macro0"]["plainParams"])
        self.assertEqual(0, payload["Arp0"]["activeClip"])
        self.assertTrue(payload["killEnvsGracefullyCompat"])
        state.verify_serum2_state(converted, proof)

    def test_silent_init_fallback_is_rejected(self):
        _, proof = self.prepare()
        with self.assertRaisesRegex(ValueError, "silently retained.*Init"):
            state.verify_serum2_state(self.native_state(), proof)

    def test_partial_parameter_and_resource_loads_are_rejected(self):
        converted, proof = self.prepare()
        meta, _, payload = self.read(converted)
        for field in ("parameter", "resource", "custom"):
            changed = copy.deepcopy(payload)
            if field == "parameter":
                changed["Env0"]["plainParams"]["kParamAttack"] = 0.5
            elif field == "resource":
                changed["Oscillator0"]["WTOsc0"]["relativePathToWT"] = "Factory/Init.wav"
            else:
                changed["FutureModule0"]["customResource"]["bytes"] = b"dropped"
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "parameter/resource"):
                state.verify_serum2_state(self.native_state(changed, meta), proof)

    def test_float_roundtrip_noise_and_expanded_default_sentinel_are_accepted(self):
        converted, proof = self.prepare()
        meta, _, payload = self.read(converted)
        payload["Env0"]["plainParams"]["kParamAttack"] += 1e-12
        payload["Macro0"]["plainParams"] = {}
        state.verify_serum2_state(self.native_state(payload, meta), proof)

    def test_midi_browser_grid_height_is_excluded_but_notes_and_playback_parameters_are_strict(self):
        self.preset["MidiClip2"] = {
            "plainParams": {"kParamSpeed": 1.0}, "gridHeight_Rows": 26.0,
            "clip": {"notes": [{"noteNum": 62, "timeStamp": 0.0, "length": 0.5, "channel": 4,
                                 "attributes": [0.8, 1.0], "expressionEvents": [None]}]}}
        converted, proof = self.prepare()
        meta, _, payload = self.read(converted)
        self.assertNotIn("gridHeight_Rows", payload["MidiClip2"])
        state.verify_serum2_state(self.native_state(payload, meta), proof)
        for field in ("noteNum", "length", "channel", "attributes", "expressionEvents", "playback"):
            changed = copy.deepcopy(payload)
            note = changed["MidiClip2"]["clip"]["notes"][0]
            if field == "playback":
                changed["MidiClip2"]["plainParams"]["kParamSpeed"] = 2.0
            elif field == "attributes":
                note[field] = [0.2, 1.0]
            elif field == "expressionEvents":
                note[field] = [{"time": 0.0}]
            else:
                note[field] += 1
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "parameter/resource"):
                state.verify_serum2_state(self.native_state(changed, meta), proof)

    def test_preset_without_load_evidence_is_rejected(self):
        preset = {**self.preset_meta, "Oscillator0": {"plainParams": "default"}, "Arp0": {"plainParams": "default"}}
        with self.assertRaisesRegex(ValueError, "no verifiable.*differences"):
            state.prepare_serum2_state(self.wrap(self.preset_meta, preset), self.native_state(), "2.0.23")

    def test_newer_preset_and_wrong_configured_version_are_rejected(self):
        for version in ("2.0.24", "2.1.0"):
            meta, preset = dict(self.preset_meta, productVersion=version), dict(self.preset, productVersion=version)
            with self.subTest(version=version), self.assertRaisesRegex(ValueError, "requires a newer plugin"):
                state.prepare_serum2_state(self.wrap(meta, preset), self.native_state(), "2.0.23")
        with self.assertRaisesRegex(ValueError, "does not match.*installed"):
            state.prepare_serum2_state(self.wrap(self.preset_meta, self.preset), self.native_state(), "2.0.25")

    def test_wrong_product_vendor_filetype_and_conflicting_payload_are_rejected(self):
        for key, value in (("product", "Serum"), ("vendor", "Other"), ("fileType", "Other")):
            meta = dict(self.preset_meta, **{key: value})
            with self.subTest(key=key), self.assertRaises(ValueError):
                state.prepare_serum2_state(self.wrap(meta, self.preset), self.native_state(), "2.0.23")
        wrong = dict(self.preset, productVersion="2.0.12")
        with self.assertRaisesRegex(ValueError, "conflicts"):
            state.prepare_serum2_state(self.wrap(self.preset_meta, wrong), self.native_state(), "2.0.23")

    def test_invalid_wrapper_hash_and_cbor_trailing_data_are_rejected(self):
        cases = [self.wrap(self.preset_meta, self.preset, wrapper=3),
                 self.wrap(self.preset_meta, self.preset, raw=self.cbor.dumps(self.preset) + b"\x00")]
        corrupt = bytearray(self.wrap(self.preset_meta, self.preset)); corrupt[-1] ^= 1; cases.append(bytes(corrupt))
        for index, blob in enumerate(cases):
            with self.subTest(index=index), self.assertRaises(ValueError):
                state.prepare_serum2_state(blob, self.native_state(), "2.0.23")

    def test_metadata_and_uncompressed_declared_limits_reject_before_decoding(self):
        with self.assertRaisesRegex(ValueError, "1 MiB"):
            state._read_xfer(b"XferJson\x00" + struct.pack("<Q", state.MAX_METADATA_BYTES + 1) + b"{}" + bytes(8), self.cbor, self.zstd)
        blob = bytearray(self.wrap(self.preset_meta, self.preset))
        length = struct.unpack_from("<Q", blob, 9)[0]
        struct.pack_into("<I", blob, 17 + length, state.MAX_DECOMPRESSED_BYTES + 1)
        with self.assertRaisesRegex(ValueError, "128 MiB"):
            state._read_xfer(bytes(blob), self.cbor, self.zstd)

    def test_zstd_unknown_size_cannot_escape_header_bound(self):
        blob = bytearray(self.wrap(self.preset_meta, self.preset, content_size=False))
        length = struct.unpack_from("<Q", blob, 9)[0]
        struct.pack_into("<I", blob, 17 + length, 1)
        with self.assertRaisesRegex(ValueError, "does not match"):
            state._read_xfer(bytes(blob), self.cbor, self.zstd)

    def test_zstd_requires_complete_checksum_and_exactly_one_frame(self):
        encoded = self.cbor.dumps(self.preset)
        for known_size in (True, False):
            complete = self.zstd.ZstdCompressor(write_content_size=known_size, write_checksum=True).compress(encoded)
            for removed in (1, 2, 3, 4):
                compressed = complete[:-removed]
                metadata = dict(self.preset_meta, hash=hashlib.md5(compressed).hexdigest())
                blob = self.wrappers.wrap_xferjson_precompressed(metadata, compressed, uncompressed_size=len(encoded), version=2)
                with self.subTest(known_size=known_size, removed=removed), self.assertRaisesRegex(ValueError, "incomplete"):
                    state._read_xfer(blob, self.cbor, self.zstd)
            self.assertEqual(encoded, state._decompress(complete, len(encoded), self.zstd))
            with self.subTest(known_size=known_size), self.assertRaises(ValueError):
                state._decompress(complete + complete, len(encoded), self.zstd)

    def test_zstd_declared_frame_size_is_checked_before_allocating_output(self):
        compressed = self.zstd.ZstdCompressor().compress(b"x" * 1024)
        with mock.patch.object(self.zstd, "ZstdDecompressor", side_effect=AssertionError("oversized frame was decompressed")):
            with self.assertRaisesRegex(ValueError, "frame size"):
                state._decompress(compressed, 1, self.zstd)

    def test_nonfinite_payload_and_duplicate_metadata_are_rejected(self):
        bad = dict(self.preset, Env0={"plainParams": {"kParamAttack": float("nan")}})
        with self.assertRaisesRegex(ValueError, "nonfinite"):
            state._read_xfer(self.wrap(self.preset_meta, bad), self.cbor, self.zstd)
        metadata = b'{"product":"Serum2","product":"Other"}'
        blob = b"XferJson\x00" + struct.pack("<Q", len(metadata)) + metadata + bytes(8) + b"x"
        with self.assertRaisesRegex(ValueError, "duplicate"):
            state._read_xfer(blob, self.cbor, self.zstd)

    def test_juce_xml_entities_length_and_duplicate_components_are_rejected(self):
        for xml in (b'<!DOCTYPE x [<!ENTITY y "z">]><VST3PluginState/>',
                    b"<VST3PluginState><IComponent/><IComponent/></VST3PluginState>"):
            blob = struct.pack("<II", 0x21324356, len(xml)) + xml + b"\x00"
            with self.subTest(xml=xml), self.assertRaises(ValueError):
                self.read(blob)
        blob = bytearray(self.native_state()); struct.pack_into("<I", blob, 4, len(blob) * 2)
        with self.assertRaisesRegex(ValueError, "XML length"):
            self.read(bytes(blob))

    def test_readback_version_changes_are_rejected(self):
        converted, proof = self.prepare()
        meta, _, payload = self.read(converted)
        meta["version"] = 10.0
        with self.assertRaisesRegex(ValueError, "version changed"):
            state.verify_serum2_state(self.native_state(payload, meta), proof)


if __name__ == "__main__":
    unittest.main()
