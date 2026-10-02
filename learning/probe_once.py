# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""One fixed, silent end-to-end check. Does not save PCM or play any sound."""
import argparse
import base64
import ctypes
from ctypes import wintypes
import datetime
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time


def running(pid):
    api = ctypes.WinDLL("kernel32", use_last_error=True)
    api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    api.OpenProcess.restype = wintypes.HANDLE
    api.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    api.WaitForSingleObject.restype = wintypes.DWORD
    api.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = api.OpenProcess(0x00100000, False, pid)
    if not handle:
        return False
    try:
        return api.WaitForSingleObject(handle, 0) == 258
    finally:
        api.CloseHandle(handle)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--device", default="none")
    parser.add_argument("--gpu-layers", default="0")
    parser.add_argument("--warmup", type=int, choices=(0, 1), default=0)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    installed = Path("C:/Program Files/MansurIME")
    command = [sys.executable, "-B", "-X", "utf8", "-u", str(root / "worker.py"),
        "--llama-server", str(installed / "translation-runtime/llama-server.exe"),
        "--translation-model", str(installed / "translation-model/model.gguf"),
        "--voice-model-dir", str(installed / "voice-model"), "--translation-timeout", "90",
        "--device", args.device, "--gpu-layers", args.gpu_layers]
    started = time.perf_counter()
    process = subprocess.Popen(command, cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW,
        env={key.upper(): value for key, value in os.environ.items()})
    inbox = queue.Queue()
    diagnostics = []

    def read():
        for line in process.stdout:
            inbox.put(json.loads(line))
        inbox.put(None)

    threading.Thread(target=read, daemon=True).start()
    allowed_diagnostics = {"translation_loading", "translation_ready", "voice_loading", "voice_numpy_import",
        "voice_onnx_import", "voice_kokoro_import", "voice_session_loading", "voice_session_ready", "voice_ready", "worker_closed",
        "translation_cpu_fallback"}

    def read_diagnostics():
        for line in process.stderr:
            code = line.strip()
            if code in allowed_diagnostics:
                diagnostics.append(code)
                print("stage=" + code, flush=True)
            else:
                diagnostics.append("unexpected_diagnostic_suppressed")

    diagnostic_thread = threading.Thread(target=read_diagnostics, daemon=True)
    diagnostic_thread.start()
    report = {"date": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "scope": "one fixed sentence; silent PCM generation; not TSF or actual-app acceptance",
        "device": args.device, "gpu_layers": args.gpu_layers, "warmup_runs": args.warmup,
        "cache_prompt": False, "worker_pid": process.pid, "fixed_source": "我到家以后给你打电话。",
        "voice": "af_heart", "status": "FAILED", "audio_bytes": 0, "audio_chunks": 0, "runs": []}
    digest = hashlib.sha256()
    translation_pid = None
    request_id = 1

    def request():
        process.stdin.write(json.dumps({"op": "learn", "request_id": request_id,
            "text": report["fixed_source"], "voice": "af_heart", "speed": 1.0}) + "\n")
        process.stdin.flush()

    try:
        while True:
            event = inbox.get(timeout=150)
            if event is None:
                raise RuntimeError("worker_exited")
            if event["event"] == "ready":
                report["startup_ms"] = round((time.perf_counter() - started) * 1000, 2)
                report["load_timings_ms"] = event["timings_ms"]
                report["runtime"] = event["runtime"]
                translation_pid = event["runtime"]["translation_pid"]
                request()
            elif event["event"] == "translation":
                report["english"] = event["text"]
            elif event["event"] == "audio":
                pcm = base64.b64decode(event["pcm_s16le"], validate=True)
                if len(pcm) % 2 or event["sample_rate"] != 24000 or event["channels"] != 1:
                    raise RuntimeError("invalid_pcm")
                digest.update(pcm)
                report["audio_bytes"] += len(pcm)
                report["audio_chunks"] += 1
            elif event["event"] == "done":
                report["timings_ms"] = event["timings_ms"]
                report["audio_sha256"] = digest.hexdigest()
                report["runs"].append({"warmup": request_id <= args.warmup, "english": report.get("english"),
                    "timings_ms": event["timings_ms"], "audio_bytes": report["audio_bytes"],
                    "audio_sha256": report["audio_sha256"]})
                if request_id <= args.warmup:
                    request_id += 1
                    report["audio_bytes"] = report["audio_chunks"] = 0
                    digest = hashlib.sha256()
                    request()
                    continue
                report["status"] = "SILENT_PIPELINE_PASSED"
                break
            elif event["event"] == "error":
                report["error_code"] = event["code"]
                break
    except Exception as error:
        report["probe_error"] = type(error).__name__
    finally:
        closed = time.perf_counter()
        if process.poll() is None:
            try:
                process.stdin.write('{"op":"close"}\n')
                process.stdin.flush()
                process.stdin.close()
                process.wait(timeout=8)
            except (OSError, subprocess.TimeoutExpired):
                process.kill()
                process.wait(timeout=3)
                report["forced_worker_cleanup"] = True
        report["close_ms"] = round((time.perf_counter() - closed) * 1000, 2)
        report["worker_exit_code"] = process.returncode
        deadline = time.monotonic() + 2
        while running(process.pid) and time.monotonic() < deadline:
            time.sleep(0.02)
        report["worker_released"] = not running(process.pid)
        report["translation_process_released"] = None if translation_pid is None else not running(translation_pid)
        diagnostic_thread.join(timeout=1)
        report["diagnostics"] = diagnostics
        print(json.dumps(report, ensure_ascii=True))
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if report["status"] == "SILENT_PIPELINE_PASSED" and report["translation_process_released"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
