# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Loopback HTTP CONNECT and TLS tests; certificates are temporary, never installed."""
from pathlib import Path
import json
import socketserver
import ssl
import subprocess
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "learning"))
import worker
from openrouter_provider import OpenRouterProvider, ProviderError

KEY = "only-a-fake-proxy-test-key"
SENTENCE = "我到家以后给你打电话。"
ANSWER = "I'll call you once I get home."


def headers(stream):
    result = bytearray()
    while not result.endswith(b"\r\n\r\n"):
        byte = stream.recv(1)
        if not byte:
            raise ConnectionError()
        result.extend(byte)
        if len(result) > 16384:
            raise ValueError()
    return bytes(result)


class Tunnel:
    def __init__(self, certificate, keyfile, mode="ok"):
        self.connect = b""
        self.request = b""
        self.sni = []
        self.started = threading.Event()
        self.release = threading.Event()
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(str(certificate), str(keyfile))
        context.set_servername_callback(lambda sock, name, ctx: self.sni.append(name))
        owner = self

        class Handler(socketserver.BaseRequestHandler):
            def handle(self):
                try:
                    owner.connect = headers(self.request)
                    owner.started.set()
                    if mode == "wait":
                        owner.release.wait(3)
                        return
                    if mode == "auth":
                        self.request.sendall(b"HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n")
                        return
                    if mode == "redirect":
                        self.request.sendall(b"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1\r\nContent-Length: 0\r\n\r\n")
                        return
                    self.request.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
                    with context.wrap_socket(self.request, server_side=True) as secure:
                        owner.request = headers(secure)
                        length = next(int(line.split(b":", 1)[1]) for line in owner.request.split(b"\r\n") if line.lower().startswith(b"content-length:"))
                        body = bytearray()
                        while len(body) < length:
                            part = secure.recv(length - len(body))
                            if not part:
                                raise ConnectionError()
                            body.extend(part)
                        if json.loads(body)["messages"][1]["content"] != SENTENCE:
                            raise ValueError()
                        response = json.dumps({"choices": [{"message": {"content": ANSWER}, "finish_reason": "stop"}]}).encode()
                        secure.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: " + str(len(response)).encode() + b"\r\n\r\n" + response)
                except (OSError, ValueError, StopIteration):
                    pass

        self.server = socketserver.ThreadingTCPServer(("127.0.0.1", 0), Handler)
        self.server.daemon_threads = True
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.release.set()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(2)


class ProxyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        openssl = Path(r"C:\Program Files\Git\usr\bin\openssl.exe")
        if not openssl.is_file():
            raise unittest.SkipTest("test-only OpenSSL unavailable")
        cls.temporary = tempfile.TemporaryDirectory(prefix="mansur-proxy-tls-")
        cls.files = {}
        for host in ("openrouter.ai", "wrong.invalid"):
            cert, key = Path(cls.temporary.name) / (host + ".crt"), Path(cls.temporary.name) / (host + ".key")
            subprocess.run([str(openssl), "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                            "-subj", "/CN=" + host, "-addext", "subjectAltName=DNS:" + host,
                            "-keyout", str(key), "-out", str(cert)], check=True, timeout=15,
                           creationflags=subprocess.CREATE_NO_WINDOW, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            cls.files[host] = (cert, key)

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def create(self, mode="ok", certificate_host="openrouter.ai"):
        certificate, keyfile = self.files[certificate_host]
        tunnel = Tunnel(certificate, keyfile, mode)
        self.addCleanup(tunnel.close)
        trust = ssl.create_default_context(cafile=str(certificate))
        trust.minimum_version = ssl.TLSVersion.TLSv1_2
        args = SimpleNamespace(openrouter_model="fake/test-model", openrouter_key_file=None,
                               translation_timeout=2, api_proxy_host="127.0.0.1", api_proxy_port=tunnel.server.server_address[1])
        provider = OpenRouterProvider(args, key_loader=lambda _: KEY)
        self.addCleanup(provider.close)
        provider.start(worker.Cancellation())
        return provider, tunnel, trust

    def test_connect_then_verified_tls_sni_and_secret_only_in_tunnel(self):
        provider, tunnel, trust = self.create()
        with patch.object(OpenRouterProvider, "_tls_context", return_value=trust):
            self.assertEqual(provider.translate(worker.Job(1, SENTENCE, "af_heart", 1)), ANSWER)
        self.assertEqual(tunnel.connect, b"CONNECT openrouter.ai:443 HTTP/1.1\r\nHost: openrouter.ai:443\r\n\r\n")
        self.assertNotIn(KEY.encode(), tunnel.connect)
        self.assertNotIn(b"Authorization", tunnel.connect)
        self.assertIn(b"Authorization: Bearer " + KEY.encode(), tunnel.request)
        self.assertEqual(tunnel.sni, ["openrouter.ai"])
        self.assertTrue(trust.check_hostname)
        self.assertEqual(trust.verify_mode, ssl.CERT_REQUIRED)

    def test_proxy_tls_wrong_hostname_is_rejected(self):
        provider, tunnel, trust = self.create(certificate_host="wrong.invalid")
        with patch.object(OpenRouterProvider, "_tls_context", return_value=trust):
            with self.assertRaises(ProviderError) as error:
                provider.translate(worker.Job(1, SENTENCE, "af_heart", 1))
        self.assertEqual(error.exception.code, "api_connection_failed")
        self.assertEqual(tunnel.request, b"")

    def test_proxy_auth_and_redirect_fail_without_direct_fallback(self):
        for mode, expected in (("auth", "api_proxy_auth_required"), ("redirect", "api_proxy_connect_failed")):
            with self.subTest(mode=mode):
                provider, tunnel, trust = self.create(mode=mode)
                with patch.object(OpenRouterProvider, "_tls_context", return_value=trust):
                    with self.assertRaises(ProviderError) as error:
                        provider.translate(worker.Job(1, SENTENCE, "af_heart", 1))
                self.assertEqual(error.exception.code, expected)
                self.assertEqual(tunnel.request, b"")
                self.assertEqual(tunnel.sni, [])

    def test_cancel_interrupts_proxy_connect(self):
        provider, tunnel, trust = self.create(mode="wait")
        job = worker.Job(1, SENTENCE, "af_heart", 1)
        outcome = []
        def run():
            try:
                provider.translate(job)
            except Exception as error:
                outcome.append(type(error))
        with patch.object(OpenRouterProvider, "_tls_context", return_value=trust):
            thread = threading.Thread(target=run)
            thread.start()
            self.assertTrue(tunnel.started.wait(1))
            job.cancel.cancel()
            thread.join(1)
            self.assertFalse(thread.is_alive())
        self.assertEqual(outcome, [worker.Cancelled])
        self.assertNotIn(KEY.encode(), tunnel.connect)


if __name__ == "__main__":
    unittest.main()
