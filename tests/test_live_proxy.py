# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Pure routing cases; no system proxy writes, remote calls or real credentials."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "learning"))
from openrouter_provider import live_proxy, ProviderError


class LiveProxyTests(unittest.TestCase):
    def route(self, proxy="", bypass="", auto=False, pac="", pinned=("old.example", 8123), url="https://chosen.example/v1"):
        return live_proxy(url, *pinned, reader=lambda: {"proxy": proxy, "bypass": bypass, "auto": auto, "pac": pac})

    def test_manual_changes_are_seen_without_worker_restart(self):
        self.assertEqual(self.route("127.0.0.1:8123"), ("127.0.0.1", 8123))
        self.assertEqual(self.route("127.0.0.1:9123"), ("127.0.0.1", 9123))
        self.assertEqual(self.route(), ("", 0))

    def test_protocol_specific_and_ipv6_routes(self):
        self.assertEqual(self.route("http=first.example:8080;https=[::1]:8443"), ("::1", 8443))
        self.assertEqual(self.route("http=first.example:8080"), ("", 0))

    def test_explicit_bypass_and_local_patterns_are_honored(self):
        for bypass in ("*.example", "chosen.example:443"):
            self.assertEqual(self.route("127.0.0.1:8123", bypass), ("", 0))
        self.assertEqual(self.route("127.0.0.1:8123", "<local>", url="http://localhost:8000/v1"), ("", 0))
        self.assertEqual(self.route("127.0.0.1:8123", "<local>"), ("127.0.0.1", 8123))

    def test_pac_and_wpad_keep_desktop_resolved_route(self):
        self.assertEqual(self.route(pac="https://settings.example/proxy.pac"), ("old.example", 8123))
        self.assertEqual(self.route(auto=True), ("old.example", 8123))
        self.assertEqual(self.route("127.0.0.1:9123", auto=True), ("127.0.0.1", 9123))

    def test_invalid_proxy_does_not_silently_become_direct(self):
        for proxy in ("socks5://localhost:1080", "http://localhost:99999", "http://localhost/path", "http://localhost?key=hidden"):
            with self.assertRaises(ProviderError) as result:
                self.route(proxy)
            self.assertEqual(result.exception.code, "api_proxy_unsupported")

    def test_proxy_credentials_have_fixed_error_without_secret(self):
        with self.assertRaises(ProviderError) as result:
            self.route("http://synthetic-secret@localhost:8080")
        self.assertEqual(str(result.exception), "api_proxy_auth_required")


if __name__ == "__main__":
    unittest.main()
