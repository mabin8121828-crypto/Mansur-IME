# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""C# production DPAPI writer -> production Python reader, synthetic secret only."""
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "learning"))
from openrouter_provider import load_windows_key, ProviderError

fixture = Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="mansur-credential-interop-") as temporary:
    subprocess.run([str(fixture), temporary], check=True, creationflags=subprocess.CREATE_NO_WINDOW,
                   stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=10)
    secret_file = Path(temporary) / "openrouter.key.dpapi"
    expected = "only-a-synthetic-provider-interop-key"
    assert expected.encode() not in secret_file.read_bytes()
    assert load_windows_key(secret_file) == expected
    secret_file.write_bytes(b"invalid encrypted fixture")
    try:
        load_windows_key(secret_file)
    except ProviderError as error:
        assert error.code == "api_key_unreadable"
    else:
        raise AssertionError("invalid encrypted fixture accepted")
print("DPAPI_INTEROP_PASS 3")
