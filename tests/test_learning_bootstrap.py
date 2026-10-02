# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Exercise the real interpreter process boundary without loading models or GUI."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class LearningBootstrapTests(unittest.TestCase):
    def run_worker(self, script, working):
        return subprocess.run([sys.executable, "-I", "-B", str(script), "--help"], cwd=str(working),
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def test_installed_interpreter_isolated_help(self):
        result = self.run_worker(ROOT / "learning" / "worker.py", ROOT)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        self.assertIn(b"--translation-provider", result.stdout)
        self.assertIn(b"--openrouter-model", result.stdout)
        self.assertEqual(result.stderr, b"")

    def test_installed_interpreter_uses_packaged_sibling_not_cwd(self):
        with tempfile.TemporaryDirectory(prefix="learning-bootstrap-") as temporary:
            package = Path(temporary) / "package with spaces"
            working = Path(temporary) / "unrelated working directory"
            package.mkdir(); working.mkdir()
            for name in ("worker.py", "openrouter_provider.py", "selection_study.py"):
                shutil.copyfile(ROOT / "learning" / name, package / name)
            (working / "openrouter_provider.py").write_text("raise RuntimeError('CWD_MODULE_MUST_NOT_LOAD')\n", encoding="utf-8")
            result = self.run_worker(package / "worker.py", working)
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
            self.assertEqual(result.stderr, b"")

    def test_missing_packaged_provider_does_not_fall_back_to_cwd(self):
        with tempfile.TemporaryDirectory(prefix="learning-bootstrap-") as temporary:
            package = Path(temporary) / "package"
            working = Path(temporary) / "cwd"
            package.mkdir(); working.mkdir()
            shutil.copyfile(ROOT / "learning" / "worker.py", package / "worker.py")
            shutil.copyfile(ROOT / "learning" / "openrouter_provider.py", working / "openrouter_provider.py")
            result = self.run_worker(package / "worker.py", working)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(b"FileNotFoundError", result.stderr)
            self.assertNotIn(b"--translation-provider", result.stdout)

    def test_damaged_packaged_provider_is_not_silently_ignored(self):
        with tempfile.TemporaryDirectory(prefix="learning-bootstrap-") as temporary:
            package = Path(temporary)
            shutil.copyfile(ROOT / "learning" / "worker.py", package / "worker.py")
            (package / "openrouter_provider.py").write_text("def broken(:\n", encoding="utf-8")
            result = self.run_worker(package / "worker.py", ROOT)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(b"SyntaxError", result.stderr)
            self.assertEqual(result.stdout, b"")


if __name__ == "__main__":
    unittest.main()
