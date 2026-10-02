# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Run a build tool with one spelling per Windows environment variable.

Some desktop launchers inherit both PATH and Path; older MSBuild fails while
creating its compiler child process. This only normalizes the child environment.
"""
import os
import subprocess
import sys

if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(2)
    env = {key.upper(): value for key, value in os.environ.items()}
    raise SystemExit(subprocess.call(sys.argv[1:], env=env))
