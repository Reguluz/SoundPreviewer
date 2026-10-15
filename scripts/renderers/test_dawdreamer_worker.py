"""Standard-library contract tests. Never import a native host or load a plugin."""

import builtins
from contextlib import contextmanager, ExitStack
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock


SPEC = importlib.util.spec_from_file_location("tested_dawdreamer_worker", Path(__file__).with_name("dawdreamer_worker.py"))
worker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(worker)


def runtime(version="14.40.33810.0", name="MSVCP140.dll"):
    return {"Name": name, "Path": "TEST ONLY/" + name, "Version": version}


def successful_probe():
    return {
        "Success": True, "Message": "TEST ONLY mocked empty-host DSP result; no plugin was loaded.",
        "AdapterVersion": worker.ADAPTER_VERSION, "ProbeKind": "EmptyHostDsp",
        "CommercialPluginsLoaded": False, "AudioWritten": False,
        "PythonPath": "TEST ONLY/python.exe", "PythonVersion": "3.12.14", "Architecture": "x64",
        "HostVersion": worker.HOST_VERSION, "NumpyVersion": worker.NUMPY_VERSION,
        "RuntimeDlls": [runtime()], "SampleRate": 44100, "Channels": 2, "Frames": 2205,
    }


@contextmanager
def mocked_host(versions=None, runtimes=None):
    versions = versions or {"dawdreamer": worker.HOST_VERSION, "numpy": worker.NUMPY_VERSION}
    imports = []
    native = SimpleNamespace()
    numpy = SimpleNamespace()
    real_import = builtins.__import__

    def version(package):
        value = versions[package]
        if isinstance(value, Exception):
            raise value
        return value

    def fake_import(name, *args, **kwargs):
        if name in ("dawdreamer", "numpy"):
            imports.append(name)
            return native if name == "dawdreamer" else numpy
        return real_import(name, *args, **kwargs)

    with ExitStack() as stack:
        stack.enter_context(mock.patch.object(worker.sys, "platform", "win32"))
        stack.enter_context(mock.patch.object(worker.sys, "version_info", (3, 12, 14)))
        stack.enter_context(mock.patch.object(worker.struct, "calcsize", return_value=8))
        stack.enter_context(mock.patch.object(worker.importlib.metadata, "version", side_effect=version))
        stack.enter_context(mock.patch.object(worker, "loaded_runtime_info", return_value=runtimes or [runtime()]))
        stack.enter_context(mock.patch.object(builtins, "__import__", new=fake_import))
        yield imports, native, numpy


class RuntimeGuardTests(unittest.TestCase):
    def test_old_runtime_rejected_before_engine(self):
        with self.assertRaisesRegex(RuntimeError, "14.27"):
            worker.validate_runtime_info([runtime("14.27.29016.0")])

    def test_minimum_and_newer_runtime_versions_pass(self):
        for version in ("14.40.0.0", "14.44.35211.0", "14.51.36231.0", "15.0.0.0"):
            with self.subTest(version=version):
                worker.validate_runtime_info([runtime(version)])

    def test_missing_standard_runtime_is_not_verified(self):
        for runtimes in ([], [runtime(name="VCRUNTIME140.dll")]):
            with self.subTest(runtimes=runtimes), self.assertRaisesRegex(RuntimeError, "MSVCP140"):
                worker.validate_runtime_info(runtimes)

    def test_old_companion_runtime_is_rejected_and_names_are_case_insensitive(self):
        worker.validate_runtime_info([runtime(name="msvcp140.DLL")])
        with self.assertRaisesRegex(RuntimeError, "VCRUNTIME140"):
            worker.validate_runtime_info([runtime(), runtime("14.27.0.0", "VCRUNTIME140.dll")])

    def test_pin_mismatches_fail_before_importing_any_native_module(self):
        for versions in ({"dawdreamer": "0.9.0", "numpy": worker.NUMPY_VERSION},
                         {"dawdreamer": worker.HOST_VERSION, "numpy": "2.0.0"}):
            with self.subTest(versions=versions), mocked_host(versions) as (imports, _, _):
                with self.assertRaisesRegex(ValueError, "Expected DawDreamer"):
                    worker.load_host(lambda phase: None)
                self.assertEqual([], imports)

    def test_missing_package_has_actionable_error_without_native_import(self):
        versions = {"dawdreamer": worker.importlib.metadata.PackageNotFoundError("dawdreamer")}
        with mocked_host(versions) as (imports, _, _):
            with self.assertRaisesRegex(RuntimeError, "Optional host is missing"):
                worker.load_host(lambda phase: None)
            self.assertEqual([], imports)

    def test_runtime_guard_runs_after_mocked_daw_import_and_before_numpy(self):
        with mocked_host(runtimes=[runtime("14.27.29016.0")]) as (imports, _, _):
            with self.assertRaisesRegex(RuntimeError, "14.27"):
                worker.load_host(lambda phase: None)
            self.assertEqual(["dawdreamer"], imports)

    def test_validated_host_reports_metadata_and_preserves_import_order(self):
        metadata = []
        with mocked_host() as (imports, native, numpy):
            actual_daw, actual_numpy, result = worker.load_host(lambda phase: None, lambda item: metadata.append(copy.deepcopy(item)))
            self.assertIs(actual_daw, native)
            self.assertIs(actual_numpy, numpy)
            self.assertEqual(["dawdreamer", "numpy"], imports)
            self.assertEqual(worker.HOST_VERSION, result["HostVersion"])
            self.assertEqual([runtime()], result["RuntimeDlls"])
            self.assertNotIn("RuntimeDlls", metadata[0])
            self.assertEqual([runtime()], metadata[-1]["RuntimeDlls"])


class IsolatedProbeTests(unittest.TestCase):
    def setUp(self):
        self.allowed = (Path(__file__).resolve().parents[2] / "artifacts" / "tests").resolve()
        self.allowed.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="daw-contract-", dir=self.allowed)
        self.directory = Path(self.temporary.name).resolve()
        if self.allowed not in self.directory.parents:
            raise AssertionError("Test fixture escaped artifacts/tests.")
        self.result_path = self.directory / "result.json"

    def tearDown(self):
        if self.allowed not in self.directory.resolve().parents:
            raise AssertionError("Refusing to clean an unverified fixture path.")
        self.temporary.cleanup()

    def run_parent(self, payload, exit_code=0, timeout=False, raw_json=None):
        phases = []

        def fake_child(command, **kwargs):
            self.assertEqual(worker.PROBE_TIMEOUT_SECONDS, kwargs["timeout"])
            self.assertEqual(subprocess.DEVNULL, kwargs["stdin"])
            self.assertIn("--probe-internal", command)
            self.assertNotIn("--request", command)
            child_path = Path(command[command.index("--result") + 1])
            self.assertIn(self.directory, child_path.resolve().parents)
            if payload is not None or raw_json is not None:
                text = raw_json if raw_json is not None else json.dumps(payload)
                child_path.write_text(text, encoding="utf-8")
            child_path.with_suffix(child_path.suffix + ".phase").write_text("TEST ONLY empty host stage", encoding="utf-8")
            if timeout:
                raise subprocess.TimeoutExpired(command, worker.PROBE_TIMEOUT_SECONDS)
            return subprocess.CompletedProcess(command, exit_code, stdout=b"", stderr=b"")

        with mock.patch.object(worker.subprocess, "CREATE_NO_WINDOW", 0x08000000, create=True), \
                mock.patch.object(worker.subprocess, "run", side_effect=fake_child) as run:
            result = worker.isolated_probe(self.result_path, phases.append)
        run.assert_called_once()
        return result, phases

    def assert_probe_only(self, result):
        self.assertEqual("EmptyHostDsp", result["ProbeKind"])
        self.assertIs(result["CommercialPluginsLoaded"], False)
        self.assertIs(result["AudioWritten"], False)
        self.assertNotIn("OutputPath", result)
        self.assertNotIn("Notes", result)
        self.assertFalse(list(self.directory.rglob("*.wav")))

    def test_successful_child_is_only_an_empty_host_dsp_result(self):
        result, phases = self.run_parent(successful_probe())
        self.assertIs(result["Success"], True)
        self.assertEqual(2205, result["Frames"])
        self.assertEqual(2, result["Channels"])
        self.assertEqual(44100, result["SampleRate"])
        self.assertGreaterEqual(len(phases), 2)
        self.assert_probe_only(result)

    def test_finished_json_failure_preserves_actionable_guard_message(self):
        payload = {**successful_probe(), "Success": False, "Finished": True, "Message": "TEST ONLY runtime 14.27 rejected"}
        result, _ = self.run_parent(payload, exit_code=1)
        self.assertIs(result["Success"], False)
        self.assertEqual(payload["Message"], result["Message"])
        self.assert_probe_only(result)

    def test_native_crash_keeps_partial_runtime_metadata_and_hex_exit(self):
        payload = {"Success": False, "RuntimeDlls": [runtime("14.27.29016.0")], "PythonPath": "TEST ONLY/python.exe"}
        result, _ = self.run_parent(payload, exit_code=-1073741819)
        self.assertIs(result["Success"], False)
        self.assertIn("0xC0000005", result["Message"])
        self.assertIn("TEST ONLY empty host stage", result["Message"])
        self.assertEqual(payload["RuntimeDlls"], result["RuntimeDlls"])
        self.assert_probe_only(result)

    def test_timeout_is_a_failure_with_partial_metadata_not_fake_audio(self):
        payload = {"Success": False, "RuntimeDlls": [runtime()]}
        result, _ = self.run_parent(payload, timeout=True)
        self.assertIs(result["Success"], False)
        self.assertIn(str(worker.PROBE_TIMEOUT_SECONDS), result["Message"])
        self.assertIn("TEST ONLY empty host stage", result["Message"])
        self.assertEqual(payload["RuntimeDlls"], result["RuntimeDlls"])
        self.assert_probe_only(result)

    def test_missing_and_malformed_child_json_cannot_pass(self):
        for raw_json in (None, "{broken"):
            with self.subTest(raw_json=raw_json):
                result, _ = self.run_parent(None, raw_json=raw_json)
                self.assertIs(result["Success"], False)
                self.assertTrue(result["Message"])
                self.assert_probe_only(result)

    def test_nonobject_child_json_is_a_structured_probe_failure(self):
        for raw_json in ("[]", "null", '"not an object"'):
            with self.subTest(raw_json=raw_json):
                result, _ = self.run_parent(None, raw_json=raw_json)
                self.assertIs(result["Success"], False)
                self.assertTrue(result["Message"])
                self.assert_probe_only(result)

    def test_main_writes_finished_probe_json_for_success_and_failure(self):
        for success in (True, False):
            with self.subTest(success=success):
                payload = {**successful_probe(), "Success": success}
                with mock.patch.object(worker.sys, "argv", [str(worker.__file__), "--probe", "--result", str(self.result_path)]), \
                        mock.patch.object(worker.sys, "stderr", mock.MagicMock()), \
                        mock.patch.object(worker, "isolated_probe", return_value=payload):
                    code = worker.main()
                result = json.loads(self.result_path.read_text(encoding="utf-8"))
                self.assertEqual(0 if success else 1, code)
                self.assertIs(result["Success"], success)
                self.assertIs(result["Finished"], True)
                self.assert_probe_only(result)

    def test_main_parent_launch_error_still_identifies_a_probe_not_a_render(self):
        with mock.patch.object(worker.sys, "argv", [str(worker.__file__), "--probe", "--result", str(self.result_path)]), \
                mock.patch.object(worker.sys, "stderr", mock.MagicMock()), \
                mock.patch.object(worker, "isolated_probe", side_effect=OSError("TEST ONLY child launch denied")):
            code = worker.main()
        result = json.loads(self.result_path.read_text(encoding="utf-8"))
        self.assertEqual(1, code)
        self.assertIs(result["Success"], False)
        self.assertIn("TEST ONLY child launch denied", result["Message"])
        self.assert_probe_only(result)


if __name__ == "__main__":
    unittest.main()
