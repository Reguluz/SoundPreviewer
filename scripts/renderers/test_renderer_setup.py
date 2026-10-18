"""Exercise the macOS setup layout without downloading wheels or loading a host."""
import ast
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]


def setup_functions():
    source = (ROOT / "scripts" / "Prepare-DawDreamer.sh").read_text(encoding="utf-8")
    embedded = source.split("<<'PYTHON'", 1)[1].split("\n", 1)[1].rsplit("\nPYTHON", 1)[0]
    tree = ast.parse(embedded)
    # The final try/except is the command-line entry point. Exercise its setup()
    # directly so Windows CI can validate the same path and dependency logic.
    tree.body = [node for node in tree.body if not isinstance(node, ast.Try)]
    namespace = {"__name__": "TEST_ONLY_renderer_setup"}
    exec(compile(tree, "Prepare-DawDreamer.sh", "exec"), namespace)
    return namespace


class RendererSetupLayoutTests(unittest.TestCase):
    def setUp(self):
        root = ROOT / "artifacts" / "tests"
        root.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="TEST-ONLY-renderer-setup-", dir=root)
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name).resolve()
        self.base = Path(sys.executable).resolve()
        self.namespace = setup_functions()

    def resources(self, application, relative):
        resources = application / relative
        resources.mkdir(parents=True)
        for name in ("requirements.txt", "requirements-serum2.txt"):
            shutil.copyfile(ROOT / "scripts" / "renderers" / name, resources / name)
        (resources / "dawdreamer_worker.py").write_text("TEST ONLY placeholder; never executed", encoding="utf-8")

    def run_setup(self, application, data=None, include_serum2=False):
        home = self.directory / "home"
        home.mkdir(exist_ok=True)
        expected = data or home / "Library" / "Application Support" / "SoundPreviewer"
        environment = expected / "renderers" / "TEST-ONLY-host"
        versions = {**self.namespace["BASE_PINS"], **self.namespace["SERUM2_PINS"]}

        def info(executable, _):
            # A base Python alias and a venv's Python may both resolve to the same
            # binary on macOS. Their environment identities follow the invoked path.
            venv = Path(executable) == environment / "bin" / "python"
            if not venv:
                self.assertEqual(self.base, Path(executable).resolve())
            return {"Executable": str(executable), "BaseExecutable": str(self.base),
                    "Prefix": str(environment if venv else self.base.parent), "Platform": "darwin",
                    "Architecture": "arm64", "Implementation": "CPython", "Bits": 64,
                    "Major": 3, "Minor": 12, "IsConda": False, "IsVenv": venv, "Versions": versions}

        def run(command, **kwargs):
            if "venv" in command:
                python = environment / "bin" / "python"
                python.parent.mkdir(parents=True)
                python.write_text("TEST ONLY virtual-environment placeholder", encoding="utf-8")
            elif "--probe" in command:
                report = Path(command[command.index("--result") + 1])
                report.write_text(json.dumps({"Success": True, "Finished": True, "CommercialPluginsLoaded": False,
                    "AudioWritten": False, "ProbeKind": "EmptyHostDsp", "HostVersion": "0.8.3", "NumpyVersion": "1.26.4"}),
                    encoding="utf-8")
            else:
                self.fail("A ready environment must not download or install dependencies.")
            return subprocess.CompletedProcess(command, 0)

        arguments = ["setup", str(application), "TEST-ONLY-host", "1" if include_serum2 else "0", "", str(data or "")]
        output = io.StringIO()
        with mock.patch.object(sys, "argv", arguments), mock.patch.object(sys, "platform", "darwin"), \
                mock.patch.object(Path, "home", return_value=home), \
                mock.patch.dict(self.namespace, {"python_info": info}), \
                mock.patch.object(subprocess, "run", side_effect=run), redirect_stdout(output):
            self.namespace["setup"]()
        result = json.loads(output.getvalue()[output.getvalue().index("{"):])
        self.assertEqual(str(environment / "bin" / "python"), result["EnvironmentPythonPath"])
        self.assertTrue((expected / "diagnostics" / "host-probe-TEST-ONLY-host.json").is_file())
        return expected

    def test_source_and_published_layouts_keep_environment_in_user_data(self):
        for layout in ("scripts/renderers", "Renderers", "Contents/MacOS/Renderers"):
            with self.subTest(layout=layout):
                app = self.directory / layout.replace("/", "-")
                self.resources(app, layout)
                before = sorted(str(path.relative_to(app)) for path in app.rglob("*"))
                self.run_setup(app, self.directory / (app.name + "-data"), include_serum2=True)
                self.assertEqual(before, sorted(str(path.relative_to(app)) for path in app.rglob("*")))

    def test_default_environment_survives_application_folder_changes(self):
        old = self.directory / "old-version"
        new = self.directory / "new-version"
        for app in (old, new):
            self.resources(app, "Renderers")
        first = self.run_setup(old)
        self.assertEqual(first, self.run_setup(new))

    def test_missing_application_resources_do_not_create_user_data(self):
        app = self.directory / "missing-resources"
        app.mkdir()
        data = self.directory / "unused-data"
        with self.assertRaisesRegex(ValueError, "requirements or worker script is missing"):
            self.run_setup(app, data)
        self.assertFalse(data.exists())

    def test_python_alias_and_venv_can_resolve_to_the_same_base_binary(self):
        app = self.directory / "published"
        self.resources(app, "Renderers")
        data = self.directory / "data"
        alias = self.directory / "python3"
        venv_python = data / "renderers" / "TEST-ONLY-host" / "bin" / "python"
        real_resolve = Path.resolve

        def resolve(path, *args, **kwargs):
            return self.base if path in (alias, venv_python) else real_resolve(path, *args, **kwargs)

        with mock.patch.object(sys, "executable", str(alias)), \
                mock.patch.object(Path, "resolve", autospec=True, side_effect=resolve):
            self.run_setup(app, data)


if __name__ == "__main__":
    unittest.main()
