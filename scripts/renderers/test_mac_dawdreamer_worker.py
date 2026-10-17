"""macOS architecture contracts; synthetic binaries are never loaded."""
import copy
from pathlib import Path
import plistlib
import struct
import tempfile
import unittest
from unittest import mock

from test_dawdreamer_worker import worker, mocked_host


class MacHostTests(unittest.TestCase):
    def setUp(self):
        root = Path(__file__).resolve().parents[2] / "artifacts" / "tests"
        root.mkdir(parents=True, exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix="TEST-ONLY-mac-host-", dir=root)
        self.addCleanup(self.directory.cleanup)
        self.bundle = Path(self.directory.name) / "TEST-ONLY.vst"
        contents = self.bundle / "Contents"
        (contents / "MacOS").mkdir(parents=True)
        (contents / "Info.plist").write_bytes(plistlib.dumps({"CFBundleExecutable": "test"}))
        self.binary = contents / "MacOS" / "test"
        self.binary.write_bytes(struct.pack("<IIIIIIII", 0xFEEDFACF, 0x0100000C, 0, 6, 0, 0, 0, 0))

    def check(self, machine="arm64"):
        with mock.patch.object(worker.sys, "platform", "darwin"), mock.patch.object(worker.platform, "machine", return_value=machine):
            worker.check_plugin_architecture(self.bundle)

    def test_native_arm64_accepts_matching_bundle_and_rejects_x64(self):
        self.check()
        with self.assertRaisesRegex(ValueError, "does not match"):
            self.check("x86_64")

    def test_universal_bundle_accepts_both_native_hosts(self):
        header = struct.pack(">II", 0xCAFEBABE, 2)
        records = struct.pack(">IIIII", 0x01000007, 0, 48, 32, 0) + struct.pack(">IIIII", 0x0100000C, 0, 80, 32, 0)
        self.binary.write_bytes(header + records + bytes(64))
        self.check()
        self.check("x86_64")

    def test_bad_universal_slice_is_rejected(self):
        self.binary.write_bytes(struct.pack(">II", 0xCAFEBABE, 1) + struct.pack(">IIIII", 0x0100000C, 0, 1024, 32, 0))
        with self.assertRaisesRegex(ValueError, "outside"):
            self.check()

    def test_windows_dll_is_rejected_on_mac(self):
        binary = Path(self.directory.name) / "TEST-ONLY.dll"
        binary.write_bytes(b"MZ" + bytes(128))
        with mock.patch.object(worker.sys, "platform", "darwin"), self.assertRaisesRegex(ValueError, "native VST2"):
            worker.check_plugin_architecture(binary)

    def test_bundle_path_traversal_and_symlink_are_rejected(self):
        plist = self.bundle / "Contents" / "Info.plist"
        plist.write_bytes(plistlib.dumps({"CFBundleExecutable": "../../outside"}))
        with self.assertRaisesRegex(ValueError, "filename"):
            self.check()
        plist.write_bytes(plistlib.dumps({"CFBundleExecutable": "test"}))
        link = Path(self.directory.name) / "linked.vst"
        link.symlink_to(self.bundle, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "native VST2"):
            worker.check_mac_plugin_architecture(link)

    def test_mac_host_does_not_call_windows_runtime_apis(self):
        with mocked_host() as (imports, _, _), mock.patch.object(worker.sys, "platform", "darwin"), \
                mock.patch.object(worker, "loaded_runtime_info", side_effect=AssertionError("Windows API called")):
            _, _, result = worker.load_host(lambda phase: None)
            self.assertEqual(["dawdreamer", "numpy"], imports)
            self.assertEqual("darwin", result["Platform"])
            self.assertNotIn("RuntimeDlls", result)

    def test_baseline_bundle_keeps_requested_order_without_loading_host(self):
        request = {
            "IsInitializationBaseline": True,
            "Preset": {"Synth": 1, "SourcePath": "", "ContentHash": ""},
            "Plugin": {"Synth": 1, "PluginPath": str(self.bundle)},
            "Template": {"LowNote": 36, "MiddleNote": 60, "HighNote": 84, "NoteOrder": 1,
                         "Velocity": 89, "HoldSeconds": 0.5, "TailSeconds": 0.08, "SampleRate": 44100, "Bpm": 120},
        }
        with mock.patch.object(worker.sys, "platform", "darwin"), mock.patch.object(worker.platform, "machine", return_value="arm64"):
            source, binary, pitches, raw = worker.validate_request(copy.deepcopy(request))
        self.assertEqual(self.bundle.resolve(), binary)
        self.assertEqual([60, 84, 36], pitches)
        self.assertIsNone(source)
        self.assertIsNone(raw)


if __name__ == "__main__":
    unittest.main()
