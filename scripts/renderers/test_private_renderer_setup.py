"""Test private macOS runtime transactions without executing a plugin or download."""
import ast
from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]


def setup_functions():
    source = (ROOT / "scripts" / "Prepare-DawDreamer.sh").read_text(encoding="utf-8")
    embedded = source.split("<<'PRIVATE_PYTHON'", 1)[1].split("\n", 1)[1].split("\nPRIVATE_PYTHON", 1)[0]
    tree = ast.parse(embedded)
    tree.body = [node for node in tree.body if not isinstance(node, ast.Try)]
    namespace = {"__name__": "TEST_ONLY_private_renderer_setup"}
    exec(compile(tree, "Prepare-DawDreamer.sh:private", "exec"), namespace)
    return namespace


class PrivateRendererSetupTests(unittest.TestCase):
    def setUp(self):
        temporary = ROOT / "artifacts" / "tests"
        temporary.mkdir(parents=True, exist_ok=True)
        self.container = tempfile.TemporaryDirectory(prefix="TEST-ONLY-private-setup-", dir=temporary)
        self.addCleanup(self.container.cleanup)
        self.root = Path(self.container.name).resolve()
        self.app = self.root / "Application with 中文"
        self.resources = self.app / "SoundPreviewer.app" / "Contents" / "MacOS" / "Renderers"
        self.resources.mkdir(parents=True)
        for name in ("requirements.txt", "requirements-serum2.txt"):
            shutil.copyfile(ROOT / "scripts" / "renderers" / name, self.resources / name)
        (self.resources / "dawdreamer_worker.py").write_text("TEST ONLY; never executed", encoding="utf-8")
        self.data = self.app / "Preview"
        (self.data / "renderers" / "tmp").mkdir(parents=True)
        self.runtime = self.data / "renderers" / "dawdreamer-portable-osx-arm64"
        self.namespace = setup_functions()
        self.installs = 0
        self.fail_probe = False
        self.probe_calls = 0
        self.fail_probe_after = None
        self.lock_error = False

    def fake_runtime(self, root, serum2=False):
        binary = root / "bin" / "python3.12"
        binary.parent.mkdir(parents=True, exist_ok=True)
        binary.write_bytes(b"TEST ONLY interpreter; never executed")
        lib = root / "lib"
        lib.mkdir(exist_ok=True)
        (lib / "libpython3.12.dylib").write_bytes(b"TEST ONLY dynamic library")
        site = lib / "python3.12" / "site-packages"
        site.mkdir(parents=True, exist_ok=True)
        dependencies = dict(self.namespace["BASE_PINS"])
        if serum2:
            dependencies.update(self.namespace["SERUM2_PINS"])
        for name, version in dependencies.items():
            canonical = name.replace("-", "_")
            metadata = site / f"{canonical}-{version}.dist-info"
            metadata.mkdir(exist_ok=True)
            (metadata / "METADATA").write_text(f"Name: {name}\nVersion: {version}\n", encoding="utf-8")
            module = site / f"{canonical}.so"
            module.write_bytes(b"TEST ONLY module; never loaded")
            (metadata / "RECORD").write_text(f"{metadata.name}/METADATA,,\n{module.name},,\n", encoding="utf-8")
        return binary

    def info(self, binary):
        root = Path(binary).parents[1]
        site = root / "lib" / "python3.12" / "site-packages"
        versions = {}
        for name, version in {**self.namespace["BASE_PINS"], **self.namespace["SERUM2_PINS"]}.items():
            versions[name] = version if (site / f"{name.replace('-', '_')}-{version}.dist-info").is_dir() else None
        return {"Version": "3.12.15", "Platform": "darwin", "Architecture": "arm64", "Prefix": str(root),
                "IsVenv": False, "Versions": versions}

    def run_process(self, command, **kwargs):
        if "--probe" in command:
            self.probe_calls += 1
            if self.fail_probe or self.fail_probe_after == self.probe_calls:
                return subprocess.CompletedProcess(command, 1)
            path = Path(command[command.index("--result") + 1])
            path.write_text(json.dumps({"Success": True, "Finished": True, "CommercialPluginsLoaded": False,
                "AudioWritten": False, "ProbeKind": "EmptyHostDsp", "HostVersion": "0.8.3", "NumpyVersion": "1.26.4"}),
                encoding="utf-8")
        elif "install" in command and "python" in command:
            self.installs += 1
            installation = Path(command[command.index("--install-dir") + 1])
            self.fake_runtime(installation / "cpython-3.12.15-macos-aarch64-none")
        elif "pip" in command and "install" in command:
            self.installs += 1
            python = Path(command[command.index("--python") + 1])
            serum2 = command[-1].endswith("requirements-serum2.txt")
            self.fake_runtime(python.parents[1], serum2)
        else:
            self.fail("Unexpected setup subprocess: " + str(command))
        return subprocess.CompletedProcess(command, 0)

    def setup(self, staged="", serum2=False, offline=False, uv="TEST-ONLY-uv", output=None):
        result = output or self.data / "diagnostics" / "setup-result.json"
        arguments = ["setup", str(self.app), str(self.resources), str(self.data), "arm64", uv,
                     "1" if serum2 else "0", str(result), "1" if offline else "0", str(self.runtime), str(staged)]
        modules = {"fcntl": mock.Mock(LOCK_EX=1, LOCK_NB=4,
            flock=mock.Mock(side_effect=BlockingIOError if self.lock_error else None))} if os.name == "nt" or self.lock_error else {}
        with mock.patch.dict(sys.modules, modules), mock.patch.object(sys, "argv", arguments), mock.patch.dict(self.namespace, {"python_info": self.info}), \
                mock.patch.object(subprocess, "run", side_effect=self.run_process), redirect_stdout(io.StringIO()):
            self.namespace["setup_private"]()
        return json.loads(result.read_text(encoding="utf-8"))

    def initial(self, serum2=False):
        staged = self.data / "renderers" / "tmp" / "TEST-ONLY-bootstrap" / "python"
        self.fake_runtime(staged)
        return self.setup(staged=staged, serum2=serum2)

    def move_fixture(self, source, destination):
        for attempt in range(40):
            try:
                source.rename(destination)
                return
            except PermissionError:
                if attempt == 39:
                    raise
                # Windows may briefly retain a scanned metadata-directory handle.
                # The acceptance still requires the actual rename to succeed.
                time.sleep(0.05)

    def test_new_runtime_promotes_after_probe_and_keeps_a_relative_manifest(self):
        result = self.initial(serum2=True)
        marker = json.loads((self.runtime / "runtime.json").read_text(encoding="utf-8"))
        self.assertEqual(str(self.runtime / "bin" / "python3.12"), result["EnvironmentPythonPath"])
        self.assertEqual("uv-managed-cpython", marker["RuntimeKind"])
        self.assertEqual("3.12.15", marker["PythonVersion"])
        self.assertIn("serum2-preset-loader", marker["Dependencies"])
        self.assertIn("bin/python3.12", marker["Files"])
        self.assertNotIn(str(self.root), json.dumps(marker))
        self.assertLessEqual(len(marker["Files"]), 64)

    def test_moving_the_whole_application_keeps_offline_runtime_ready(self):
        self.initial(serum2=True)
        old_marker = (self.runtime / "runtime.json").read_bytes()
        moved = self.root / "Moved portable app🎹"
        self.move_fixture(self.app, moved)
        self.app = moved
        self.resources = moved / "SoundPreviewer.app" / "Contents" / "MacOS" / "Renderers"
        self.data = moved / "Preview"
        self.runtime = self.data / "renderers" / "dawdreamer-portable-osx-arm64"
        self.installs = 0
        result = self.setup(offline=True, uv="")
        self.assertTrue(result["Success"])
        self.assertTrue(result["IncludeSerum2"])
        self.assertEqual(0, self.installs)
        self.assertEqual(old_marker, (self.runtime / "runtime.json").read_bytes())

    def test_interrupted_promotion_recovers_valid_backup_offline_without_uv(self):
        self.initial(serum2=True)
        previous = (self.runtime / "runtime.json").read_bytes()
        backup = self.runtime.with_name(self.runtime.name + "-before-setup-0123456789abcdef")
        self.move_fixture(self.runtime, backup)
        self.installs = 0
        result = self.setup(offline=True, uv="")
        self.assertTrue(result["Success"])
        self.assertTrue(result["IncludeSerum2"])
        self.assertEqual(0, self.installs)
        self.assertFalse(backup.exists())
        self.assertEqual(previous, (self.runtime / "runtime.json").read_bytes())

    def test_interrupted_promotion_leaves_unrecognized_backup_untouched(self):
        self.initial()
        backup = self.runtime.with_name(self.runtime.name + "-before-setup-0123456789abcdef")
        self.move_fixture(self.runtime, backup)
        marker = backup / "runtime.json"
        value = json.loads(marker.read_text(encoding="utf-8"))
        value["SchemaVersion"] = 99
        marker.write_text(json.dumps(value), encoding="utf-8")
        original = marker.read_bytes()
        self.installs = 0
        with self.assertRaisesRegex(ValueError, "needs preparation"):
            self.setup(offline=True, uv="")
        self.assertFalse(self.runtime.exists())
        self.assertEqual(original, marker.read_bytes())
        self.assertEqual(0, self.installs)

    def test_interrupted_promotion_checks_hashes_and_exact_backup_name(self):
        self.initial()
        backup = self.runtime.with_name(self.runtime.name + "-before-setup-0123456789abcdef")
        self.move_fixture(self.runtime, backup)
        binary = backup / "bin" / "python3.12"
        binary.write_bytes(b"TEST ONLY modified interpreter")
        self.installs = 0
        with self.assertRaisesRegex(ValueError, "needs preparation"):
            self.setup(offline=True, uv="")
        self.assertFalse(self.runtime.exists())
        self.assertTrue(backup.exists())
        # A valid managed marker in a user-named folder is also insufficient.
        self.fake_runtime(backup)
        user_named = self.runtime.with_name(self.runtime.name + "-before-setup-my-backup")
        self.move_fixture(backup, user_named)
        with self.assertRaisesRegex(ValueError, "needs preparation"):
            self.setup(offline=True, uv="")
        self.assertFalse(self.runtime.exists())
        self.assertTrue(user_named.exists())
        self.assertEqual(0, self.installs)

    def test_interrupted_promotion_does_not_replace_existing_unknown_runtime(self):
        self.initial()
        backup = self.runtime.with_name(self.runtime.name + "-before-setup-0123456789abcdef")
        self.move_fixture(self.runtime, backup)
        self.runtime.mkdir()
        unknown = self.runtime / "user-file.txt"
        unknown.write_bytes(b"TEST ONLY existing unrecognized data")
        self.installs = 0
        with self.assertRaisesRegex(ValueError, "not a recognized"):
            self.setup(offline=True, uv="")
        self.assertEqual(b"TEST ONLY existing unrecognized data", unknown.read_bytes())
        self.assertTrue(backup.exists())
        self.assertEqual(0, self.installs)

    def test_adding_serum2_preserves_previous_runtime_as_backup(self):
        self.initial()
        previous = (self.runtime / "runtime.json").read_bytes()
        self.setup(serum2=True)
        backups = list(self.runtime.parent.glob(self.runtime.name + "-before-setup-*"))
        self.assertEqual(1, len(backups))
        self.assertEqual(previous, (backups[0] / "runtime.json").read_bytes())
        self.assertIn("serum2-preset-loader", json.loads((self.runtime / "runtime.json").read_text())["Dependencies"])

    def test_failed_probe_does_not_replace_an_existing_runtime(self):
        self.initial()
        previous = (self.runtime / "runtime.json").read_bytes()
        self.fail_probe = True
        with self.assertRaisesRegex(ValueError, "bounded probe report"):
            self.setup(serum2=True)
        self.assertEqual(previous, (self.runtime / "runtime.json").read_bytes())
        self.assertEqual([], list(self.runtime.parent.glob(self.runtime.name + "-before-setup-*")))
        self.assertEqual([], list((self.data / "renderers" / "tmp").glob("runtime-mac-*")))

    def test_forward_runtime_schema_is_not_overwritten(self):
        self.initial()
        marker = self.runtime / "runtime.json"
        value = json.loads(marker.read_text())
        value["SchemaVersion"] = 99
        marker.write_text(json.dumps(value), encoding="utf-8")
        original = marker.read_bytes()
        with self.assertRaisesRegex(ValueError, "unsupported"):
            self.setup()
        self.assertEqual(original, marker.read_bytes())

    def test_failure_after_promotion_restores_previous_runtime(self):
        self.initial()
        previous = (self.runtime / "runtime.json").read_bytes()
        self.fail_probe_after = self.probe_calls + 2
        with self.assertRaisesRegex(ValueError, "bounded probe report"):
            self.setup(serum2=True)
        self.assertEqual(previous, (self.runtime / "runtime.json").read_bytes())
        self.assertEqual([], list(self.runtime.parent.glob(self.runtime.name + "-before-setup-*")))

    def test_concurrent_setup_is_rejected_before_runtime_changes(self):
        self.lock_error = True
        with self.assertRaisesRegex(ValueError, "already running"):
            self.setup()
        self.assertFalse(self.runtime.exists())
        self.assertEqual(0, self.installs)

    def test_external_result_path_is_rejected_before_installation(self):
        with self.assertRaisesRegex(ValueError, "Preview directory"):
            self.setup(output=self.root / "outside.json")
        self.assertFalse(self.runtime.exists())
        self.assertEqual(0, self.installs)


if __name__ == "__main__":
    unittest.main()
