# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""OpenRouter fake HTTP tests only. No real API key, remote request, model or audio."""
import asyncio
import http.server
import json
from pathlib import Path
import ssl
import sys
import threading
import time
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "learning"))
import worker
from openrouter_provider import OpenRouterProvider, ProviderError, load_windows_key

SENTENCE = "我到家以后给你打电话。"
ANSWER = "I'll call you once I get home."
FAKE_KEY = "only-a-fake-test-key"


class Server:
    def __init__(self, behavior):
        owner = self
        self.calls = []
        self.started = threading.Event()
        self.release = threading.Event()

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                owner.calls.append({"path": self.path, "payload": payload,
                                    "authorized": self.headers.get("Authorization") == "Bearer " + FAKE_KEY})
                owner.started.set()
                try:
                    behavior(self, owner)
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    pass

        self.http = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.http.daemon_threads = True
        self.thread = threading.Thread(target=self.http.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.release.set()
        self.http.shutdown()
        self.http.server_close()
        self.thread.join(timeout=2)


def respond(handler, status=200, value=None, raw=None, extra=None):
    if raw is None:
        raw = json.dumps(value if value is not None else {
            "choices": [{"message": {"content": ANSWER}, "finish_reason": "stop"}]}).encode()
    handler.send_response(status)
    handler.send_header("Content-Length", str(len(raw)))
    for key, value in (extra or {}).items():
        handler.send_header(key, value)
    handler.end_headers()
    handler.wfile.write(raw)


class OpenRouterTests(unittest.TestCase):
    def create(self, behavior=lambda h, s: respond(h), timeout=1.0):
        server = Server(behavior)
        self.addCleanup(server.close)
        args = SimpleNamespace(openrouter_model="fake/test-model", openrouter_key_file=Path("unused-encrypted-file"),
                               translation_timeout=timeout)
        provider = OpenRouterProvider(args, key_loader=lambda _: FAKE_KEY,
            connection_factory=lambda: asyncio.open_connection("127.0.0.1", server.http.server_port))
        self.addCleanup(provider.close)
        provider.start(worker.Cancellation())
        return provider, server

    def test_one_confirmed_sentence_only_and_no_startup_request(self):
        provider, server = self.create()
        self.assertEqual(server.calls, [])
        result = provider.translate(worker.Job(1, SENTENCE, "af_heart", 1.0))
        self.assertEqual(result, ANSWER)
        self.assertEqual(len(server.calls), 1)
        call = server.calls[0]
        self.assertEqual(call["path"], "/api/v1/chat/completions")
        self.assertTrue(call["authorized"])
        body = call["payload"]
        self.assertEqual(body["model"], "fake/test-model")
        self.assertEqual(body["messages"][1], {"role": "user", "content": SENTENCE})
        self.assertEqual(len(body["messages"]), 2)
        self.assertNotIn("user", body)
        self.assertNotIn("request_id", body)
        self.assertFalse(body["stream"])
        self.assertFalse(body["provider"]["allow_fallbacks"])

    def test_http_errors_are_fixed_and_no_automatic_retry(self):
        for status, code in ((401, "api_unauthorized"), (402, "api_credit_required"),
                             (429, "api_rate_limited"), (404, "api_model_not_found"), (503, "api_provider_unavailable")):
            with self.subTest(status=status):
                provider, server = self.create(lambda h, s, status=status: respond(h, status, {"secret": FAKE_KEY}))
                with self.assertRaises(ProviderError) as error:
                    provider.translate(worker.Job(1, SENTENCE, "af_heart", 1.0))
                self.assertEqual(error.exception.code, code)
                self.assertNotIn(FAKE_KEY, str(error.exception))
                self.assertEqual(len(server.calls), 1)

    def test_redirect_never_followed(self):
        provider, server = self.create(lambda h, s: respond(h, 302, extra={"Location": "http://127.0.0.1:1/exfiltrate"}))
        with self.assertRaises(ProviderError) as error:
            provider.translate(worker.Job(1, SENTENCE, "af_heart", 1.0))
        self.assertEqual(error.exception.code, "api_redirect_rejected")
        self.assertEqual(len(server.calls), 1)

    def test_invalid_or_oversized_translation_rejected(self):
        for payload in (b"not-json", b"x" * 65537, json.dumps({"choices": [
                {"message": {"content": ANSWER}, "finish_reason": "length"}]}).encode()):
            with self.subTest(length=len(payload)):
                provider, _ = self.create(lambda h, s, payload=payload: respond(h, raw=payload))
                with self.assertRaises(ProviderError):
                    provider.translate(worker.Job(1, SENTENCE, "af_heart", 1.0))

    def test_cancel_interrupts_waiting_http(self):
        provider, server = self.create(lambda h, s: (s.release.wait(2), respond(h)), timeout=3)
        job = worker.Job(1, SENTENCE, "af_heart", 1.0)
        result = []
        def run():
            try:
                provider.translate(job)
            except Exception as error:
                result.append(type(error))
        thread = threading.Thread(target=run)
        thread.start()
        self.assertTrue(server.started.wait(1))
        started = time.monotonic()
        job.cancel.cancel()
        thread.join(timeout=1)
        self.assertFalse(thread.is_alive())
        self.assertLess(time.monotonic() - started, 1)
        self.assertEqual(result, [worker.Cancelled])

    def test_total_timeout_has_no_retry(self):
        provider, server = self.create(lambda h, s: (s.release.wait(2), respond(h)), timeout=0.15)
        started = time.monotonic()
        with self.assertRaises(ProviderError) as error:
            provider.translate(worker.Job(1, SENTENCE, "af_heart", 1.0))
        self.assertEqual(error.exception.code, "api_timeout")
        self.assertLess(time.monotonic() - started, 1)
        self.assertEqual(len(server.calls), 1)

    def test_missing_key_fails_before_http(self):
        args = SimpleNamespace(openrouter_model="fake/test-model", openrouter_key_file=None, translation_timeout=1)
        provider = OpenRouterProvider(args)
        with self.assertRaises(ProviderError) as error:
            provider.start(worker.Cancellation())
        self.assertIn(error.exception.code, ("api_key_missing", "api_key_unreadable"))

    def test_api_mode_does_not_create_llama_server(self):
        models = worker.LocalModels(SimpleNamespace(translation_provider="openrouter"))
        self.assertIsInstance(models.translation, worker.OpenRouterProvider)
        self.assertFalse(hasattr(models.translation, "process"))

    def test_error_code_shape_stays_inside_fixed_failure_boundary(self):
        for code in ({"unexpected": "object"}, [401], "401", None, True):
            with self.subTest(kind=type(code).__name__):
                provider, _ = self.create(lambda h, s, code=code: respond(h, value={"error": {"code": code}}))
                with self.assertRaises(ProviderError) as error:
                    provider.translate(worker.Job(1, SENTENCE, "af_heart", 1.0))
                self.assertEqual(error.exception.code, "api_http_error")

    def test_chunked_response_is_bounded_and_parsed(self):
        def chunks(handler, server):
            body = json.dumps({"choices": [{"message": {"content": ANSWER}, "finish_reason": "stop"}]}).encode()
            handler.send_response(200)
            handler.send_header("Transfer-Encoding", "chunked")
            handler.end_headers()
            for part in (body[:10], body[10:]):
                handler.wfile.write(("%x\r\n" % len(part)).encode() + part + b"\r\n")
            handler.wfile.write(b"0\r\n\r\n")
        provider, server = self.create(chunks)
        self.assertEqual(provider.translate(worker.Job(1, SENTENCE, "af_heart", 1.0)), ANSWER)
        self.assertEqual(len(server.calls), 1)

    def test_cancel_interrupts_waiting_response_body(self):
        body_started = threading.Event()
        def partial_body(handler, server):
            handler.send_response(200)
            handler.send_header("Content-Length", "100")
            handler.end_headers()
            handler.wfile.write(b"{")
            handler.wfile.flush()
            body_started.set()
            server.release.wait(2)
        provider, _ = self.create(partial_body, timeout=3)
        job = worker.Job(1, SENTENCE, "af_heart", 1.0)
        result = []
        def run():
            try:
                provider.translate(job)
            except Exception as error:
                result.append(type(error))
        thread = threading.Thread(target=run)
        thread.start()
        self.assertTrue(body_started.wait(1))
        job.cancel.cancel()
        thread.join(timeout=1)
        self.assertFalse(thread.is_alive())
        self.assertEqual(result, [worker.Cancelled])

    def test_default_connection_requires_certificate_and_tls12(self):
        from unittest.mock import patch
        captured = {}
        async def fake_connect(host, port, **kwargs):
            captured.update(host=host, port=port, **kwargs)
            return None, None
        with patch("asyncio.open_connection", fake_connect):
            asyncio.run(OpenRouterProvider._connection())
        self.assertEqual(captured["host"], "openrouter.ai")
        self.assertTrue(captured["ssl"].check_hostname)
        self.assertEqual(captured["ssl"].verify_mode, ssl.CERT_REQUIRED)
        self.assertGreaterEqual(captured["ssl"].minimum_version, ssl.TLSVersion.TLSv1_2)


if __name__ == "__main__":
    unittest.main()
