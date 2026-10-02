# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Headless regression for the wide-path compiler; fixed fixtures only."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


def main():
    executable = Path(sys.argv[1]).resolve()
    base = Path(sys.argv[2]).resolve()
    environment = {key.upper(): value for key, value in os.environ.items()}
    checks = []
    with tempfile.TemporaryDirectory(prefix="mansur-词库 空格-") as directory:
        root = Path(directory)
        user = root / "用户 词库.tsv"
        output = root / "用户 结果.mlex"

        def run(text, expected, code=None):
            user.write_text(text, encoding="utf-8")
            previous = output.read_bytes() if output.exists() else None
            completed = subprocess.run(
                [str(executable), "--merge", str(base), str(user), str(output)],
                capture_output=True, env=environment, timeout=60)
            diagnostics = (completed.stdout + completed.stderr).decode("utf-8")
            assert (completed.returncode == 0) == expected, diagnostics
            if code is not None:
                assert code in diagnostics, diagnostics
            if not expected:
                assert output.read_bytes() == previous, "failed merge changed previous output"
            assert "私密固定样例" not in diagnostics
            assert str(root) not in diagnostics
            assert not list(root.glob(".mansur-compile-*")), "staging directory leaked"
            checks.append(code or "wide_path_success")

        run("ceshiyonghu\t测试用户\t10000\tce shi yong hu\n", True)
        run("nihao\t你好\t10000\t\n", True)
        run("nihao\t你好\t10000\tni hao\nnihao\t私密固定样例\tbad\tni hao\n", False, "line=2")
        run("nihao\t私密固定样例\tbad\tni hao\n", False, "line=1")
        run("nihao\t你好\t10000\tnih ao\n", False, "line=1")
        run("nihao\t你好\t1000000000001\tni hao\n", False, "line=1")
        run("nihao\t你\u0085好\t10000\tni hao\n", False, "line=1")
        run("#" + "x" * (4 * 1024 * 1024), False, "user_dictionary_too_large")
        run("".join(f"ni\t字{i}\t100\n" for i in range(10001)), False, "too_many_user_entries")

        # A failure at final publication must preserve the previous output too.
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                      ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        kernel.CreateFileW.restype = ctypes.c_void_p
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.CreateFileW(str(output), 0x80000000, 1, None, 3, 0, None)
        assert handle != ctypes.c_void_p(-1).value
        try:
            run("nihao\t你好\t10000\tni hao\n", False, "publish_failed")
        finally:
            kernel.CloseHandle(handle)
        # The original two-argument mode still works through wide paths.
        done = subprocess.run([str(executable), str(user), str(root / "旧模式 输出.mlex")],
                              capture_output=True, env=environment, timeout=60)
        assert done.returncode == 0, done.stderr
        checks.append("legacy_mode_wide_path")
        print(json.dumps({"passed": len(checks), "checks": checks}, ensure_ascii=True))


if __name__ == "__main__":
    main()
