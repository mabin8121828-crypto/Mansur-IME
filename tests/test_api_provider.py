# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Loopback services and disposable DPAPI fixtures; no real cloud account."""
import asyncio
import ctypes
from ctypes import wintypes
import io
import http.server
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
import wave
import queue

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "learning"))
import worker
import api_provider
from test_openrouter_provider import Server, respond, FAKE_KEY


def wav_bytes(channels=1, rate=24000):
    stream = io.BytesIO()
    with wave.open(stream, "wb") as wav:
        wav.setnchannels(channels); wav.setsampwidth(2); wav.setframerate(rate)
        wav.writeframes(struct.pack("<" + "h" * (240 * channels), *([100] * (240 * channels))))
    return stream.getvalue()


def args_for(server, capability="translation", **overrides):
    values = dict(translation_timeout=1, translation_provider="compatible", speech_provider="compatible")
    for cap in ("translation", "speech"):
        values.update({cap + "_api_base_url": "http://127.0.0.1:" + str(server.http.server_port) + "/custom/v1",
                       cap + "_api_model": "fixed/model", cap + "_api_service_id": cap + "-test",
                       cap + "_api_key_file": Path("unused-fixture"), cap + "_api_voice": "fixed-voice",
                       cap + "_api_format": "wav", cap + "_api_sample_rate": 24000})
    values.update(overrides)
    return SimpleNamespace(**values)


def encrypt_key(path, service_id):
    entropy = ("MansurNext.ApiService.v1:" + service_id).encode()
    class Blob(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]
    key_buffer = (ctypes.c_ubyte * len(FAKE_KEY)).from_buffer_copy(FAKE_KEY.encode())
    entropy_buffer = (ctypes.c_ubyte * len(entropy)).from_buffer_copy(entropy)
    source, extra, output = Blob(len(FAKE_KEY), key_buffer), Blob(len(entropy), entropy_buffer), Blob()
    api = ctypes.WinDLL("crypt32", use_last_error=True)
    api.CryptProtectData.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    if not api.CryptProtectData(ctypes.byref(source), None, ctypes.byref(extra), None, None, 1, ctypes.byref(output)):
        raise RuntimeError("test-encryption-failed")
    kernel = ctypes.WinDLL("kernel32"); kernel.LocalFree.argtypes = [ctypes.c_void_p]
    try: path.write_bytes(ctypes.string_at(output.data, output.size))
    finally: kernel.LocalFree(output.data)


class CompatibleApiTests(unittest.TestCase):
    def create(self, behavior=None, speech=False, **fields):
        if behavior is None:
            behavior = lambda h, s: respond(h, raw=wav_bytes(), extra={"Content-Type": "audio/wav"}) if speech else respond(h, value={"choices": [{"message": {"content": "Hello."}, "finish_reason": "stop"}]})
        server = Server(behavior); self.addCleanup(server.close)
        args = args_for(server, **fields)
        provider = api_provider.CompatibleSpeech(args, worker.WorkerError, key_loader=lambda _: FAKE_KEY) if speech else api_provider.CompatibleProvider(args, "translation", worker.WorkerError, key_loader=lambda _: FAKE_KEY)
        self.addCleanup(provider.close); provider.start(worker.Cancellation())
        return provider, server

    def test_translation_custom_url_only_confirmed_sentence(self):
        provider, server = self.create()
        self.assertEqual(server.calls, [])
        self.assertEqual(provider.translate(worker.Job(1, "你好", "af_heart", 1)), "Hello.")
        self.assertEqual(len(server.calls), 1); call = server.calls[0]
        self.assertTrue(call["authorized"]); self.assertEqual(call["path"], "/custom/v1/chat/completions")
        self.assertEqual(call["payload"]["messages"][1], {"role": "user", "content": "你好"})
        self.assertEqual(len(call["payload"]["messages"]), 2)

    def test_wav_uses_actual_rate_and_downmixes_stereo(self):
        provider, server = self.create(lambda h, s: respond(h, raw=wav_bytes(2, 44100), extra={"Content-Type": "audio/wav"}), speech=True)
        output = []; provider.synthesize(worker.Job(1, "你好", "af_heart", .75), "Hello.", lambda pcm, rate: output.append((pcm, rate)))
        self.assertEqual(output, [(struct.pack("<" + "h" * 240, *([100] * 240)), 44100)])
        body = server.calls[0]["payload"]
        self.assertEqual(server.calls[0]["path"], "/custom/v1/audio/speech")
        self.assertEqual(body, {"model": "fixed/model", "input": "Hello.", "voice": "fixed-voice", "response_format": "wav", "speed": .75})

    def test_pcm_selected_rate_and_siliconflow_rate_argument(self):
        pcm = b"\x01\x00" * 100
        provider, server = self.create(lambda h, s: respond(h, raw=pcm, extra={"Content-Type": "audio/pcm"}), speech=True, speech_api_format="pcm", speech_api_sample_rate=16000, speech_api_service_id="speech-siliconflow")
        output = []; provider.synthesize(worker.Job(1, "Hello", "af_heart", 1), "Hello", lambda p, r: output.append((p, r)))
        self.assertEqual(output, [(pcm, 16000)]); self.assertEqual(server.calls[0]["payload"]["sample_rate"], 16000)

    def test_audio_response_errors_never_become_noise(self):
        for data, mime, fmt in ((b"<html>error</html>", "text/html", "pcm"), (b"ID3mp3", "audio/mpeg", "wav"), (b"{}", "application/json", "pcm"), (b"\x01", "audio/pcm", "pcm"), (wav_bytes()[:-3], "audio/wav", "wav"), (wav_bytes(), "audio/wav", "pcm")):
            with self.subTest(mime=mime, fmt=fmt):
                provider, server = self.create(lambda h, s, data=data, mime=mime: respond(h, raw=data, extra={"Content-Type": mime}), speech=True, speech_api_format=fmt)
                output = []
                with self.assertRaises(worker.WorkerError) as error: provider.synthesize(worker.Job(1, "Hello", "af_heart", 1), "Hello", lambda p, r: output.append(p))
                self.assertEqual(error.exception.code, "voice_audio_invalid")
                # Truncated WAV must be rejected before any partial playback.
                self.assertEqual(output, [])

    def test_redirect_and_auth_errors_have_no_retry_or_secret_body(self):
        for status, code in ((302, "api_redirect_rejected"), (401, "api_unauthorized"), (403, "api_forbidden"), (429, "api_rate_limited")):
            provider, server = self.create(lambda h, s, status=status: respond(h, status=status, raw=FAKE_KEY.encode(), extra={"Location": "https://example.com"}))
            with self.assertRaises(worker.WorkerError) as error: provider.translate(worker.Job(1, "你好", "af_heart", 1))
            self.assertEqual(error.exception.code, code); self.assertNotIn(FAKE_KEY, str(error.exception)); self.assertEqual(len(server.calls), 1)

    def test_cancellation_and_timeout_close_pending_exchange(self):
        provider, server = self.create(lambda h, s: (s.release.wait(2), respond(h)), translation_timeout=.1)
        with self.assertRaises(worker.WorkerError) as error: provider.translate(worker.Job(1, "你好", "af_heart", 1))
        self.assertEqual(error.exception.code, "api_timeout")
        provider, server = self.create(lambda h, s: (s.release.wait(2), respond(h)), speech=True)
        job = worker.Job(2, "Hello", "af_heart", 1); output = []; errors = []
        def run():
            try: provider.synthesize(job, "Hello", lambda p, r: output.append(p))
            except BaseException as e: errors.append(e)
        thread = threading.Thread(target=run); thread.start()
        self.assertTrue(server.started.wait(1)); job.cancel.cancel(); thread.join(1)
        self.assertFalse(thread.is_alive()); self.assertTrue(isinstance(errors[0], worker.Cancelled)); self.assertEqual(output, [])

    def test_insecure_or_credential_urls_rejected(self):
        for url in ("http://example.com", "https://secret@example.com/v1", "https://example.com/v1?key=secret", "https://example.com/#fragment", "https://example.com/\r\nX", "file:///C:/test"):
            with self.assertRaises(worker.WorkerError): api_provider.endpoint(url, worker.WorkerError)
        self.assertEqual(api_provider.endpoint("http://[::1]:1234/v1", worker.WorkerError).hostname, "::1")

    def test_custom_https_proxy_connect_does_not_send_key_or_input(self):
        calls = []
        class Proxy(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_CONNECT(self):
                calls.append((self.path, self.headers.get("Authorization"), self.headers.get("Proxy-Authorization")))
                self.send_response(407); self.end_headers()
        proxy = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Proxy)
        thread = threading.Thread(target=proxy.serve_forever, daemon=True); thread.start()
        try:
            args = args_for(SimpleNamespace(http=SimpleNamespace(server_port=1)), translation_api_base_url="https://chosen.example:8443/v1",
                            translation_api_proxy_host="127.0.0.1", translation_api_proxy_port=proxy.server_port)
            provider = api_provider.CompatibleProvider(args, "translation", worker.WorkerError, key_loader=lambda _: FAKE_KEY)
            try:
                provider.start(worker.Cancellation())
                with self.assertRaises(worker.WorkerError) as error: provider.translate(worker.Job(1, "你好", "af_heart", 1))
                self.assertEqual(error.exception.code, "api_proxy_auth_required")
                self.assertEqual(calls, [("chosen.example:8443", None, None)])
            finally: provider.close()
            # A worker launched with a direct route must pick up the user's
            # subsequently enabled manual proxy before each new request.
            args.translation_api_proxy_host = ""
            args.translation_api_proxy_port = 0
            refreshed = api_provider.CompatibleProvider(args, "translation", worker.WorkerError, key_loader=lambda _: FAKE_KEY)
            try:
                refreshed.start(worker.Cancellation()); refreshed.test_loader = False
                from unittest.mock import patch
                with patch.object(api_provider._base, "live_proxy", return_value=("127.0.0.1", proxy.server_port)) as read_route:
                    for identifier in (2, 3):
                        with self.assertRaises(worker.WorkerError) as error:
                            refreshed.translate(worker.Job(identifier, "你好", "af_heart", 1))
                        self.assertEqual(error.exception.code, "api_proxy_auth_required")
                    self.assertEqual(read_route.call_count, 2)
                self.assertEqual(calls, [("chosen.example:8443", None, None)] * 3)
            finally: refreshed.close()
        finally: proxy.shutdown(); proxy.server_close(); thread.join(1)

    @unittest.skipUnless(sys.platform == "win32", "Windows DPAPI fixture")
    def test_actual_worker_cloud_only_two_services_and_english_bypass(self):
        text_server = Server(lambda h, s: respond(h, value={"choices": [{"message": {"content": "Hello."}, "finish_reason": "stop"}]}))
        voice_server = Server(lambda h, s: respond(h, raw=wav_bytes(), extra={"Content-Type": "audio/wav"}))
        self.addCleanup(text_server.close); self.addCleanup(voice_server.close)
        with tempfile.TemporaryDirectory(prefix="MansurCloudWorkerTest-") as root:
            command = [sys.executable, "-B", str(Path(worker.__file__)), "--translation-provider", "compatible", "--speech-provider", "compatible"]
            for capability, server in (("translation", text_server), ("speech", voice_server)):
                key = Path(root) / (capability + ".dpapi"); encrypt_key(key, capability + "-test")
                command += ["--" + capability + "-api-base-url", "http://127.0.0.1:" + str(server.http.server_port) + "/v1",
                            "--" + capability + "-api-model", "fixed/model", "--" + capability + "-api-service-id", capability + "-test", "--" + capability + "-api-key-file", str(key)]
            command += ["--speech-api-voice", "fixed-voice", "--speech-api-format", "wav"]
            process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
            events = queue.Queue()
            reader = threading.Thread(target=lambda: [events.put(json.loads(line)) for line in process.stdout], daemon=True); reader.start()
            def wait(kind, rid):
                deadline = time.monotonic() + 5; seen = []
                while time.monotonic() < deadline:
                    event = events.get(timeout=max(.01, deadline - time.monotonic())); seen.append(event)
                    if event["event"] == "error": self.fail(str(event))
                    if event["event"] == kind and event.get("request_id") == rid: return event, seen
                self.fail("worker event timeout")
            try:
                wait("ready", None)
                self.assertEqual(text_server.calls, []); self.assertEqual(voice_server.calls, [])
                process.stdin.write(json.dumps({"op": "learn", "request_id": 1, "text": "你好"}) + "\n"); process.stdin.flush()
                _, seen = wait("done", 1)
                self.assertTrue(any(e["event"] == "translation" and e["text"] == "Hello." for e in seen))
                self.assertTrue(any(e["event"] == "audio" for e in seen))
                self.assertEqual(len(text_server.calls), 1); self.assertEqual(len(voice_server.calls), 1)
                process.stdin.write(json.dumps({"op": "learn", "request_id": 2, "text": "Hello"}) + "\n"); process.stdin.flush()
                wait("done", 2)
                self.assertEqual(len(text_server.calls), 1); self.assertEqual(len(voice_server.calls), 2)
                self.assertEqual(voice_server.calls[-1]["payload"]["input"], "Hello")
                process.stdin.write('{"op":"shutdown"}\n'); process.stdin.flush(); process.wait(timeout=5)
                self.assertEqual(process.returncode, 0)
                stderr = process.stderr.read(); self.assertNotIn(FAKE_KEY, stderr); self.assertNotIn("你好", stderr); self.assertNotIn("Hello", stderr)
            finally:
                if process.poll() is None: process.kill(); process.wait(timeout=3)
                for stream in (process.stdin, process.stdout, process.stderr): stream.close()


if __name__ == "__main__": unittest.main()
