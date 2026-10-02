# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""No cloud access: SSE framing, early presentation, completion and cancellation."""
import json
import sys
from pathlib import Path
import threading
import time
import unittest
from types import SimpleNamespace
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "learning"))
import api_stream
import api_provider
import worker
from test_api_provider import args_for
from test_openrouter_provider import Server, respond, FAKE_KEY


def event(text=None, reason=None, **fields):
    return ("data: " + json.dumps({"choices": [{"index": 0, "delta": {} if text is None else {"content": text}, "finish_reason": reason}], **fields}, ensure_ascii=False) + "\r\n\r\n").encode()


class StreamTests(unittest.TestCase):
    def parser(self):
        values, cancel = [], worker.Cancellation()
        return api_stream.TranslationStream(worker.WorkerError, values.append, cancel), values, cancel

    def test_utf8_split_comments_usage_and_repeated_stop(self):
        parser, values, _ = self.parser()
        raw = b"\xef\xbb\xbf: processing\r\n\r\n" + event("Hello café") + event(reason="stop") + event("", "stop", usage={"total_tokens": 3}) + b"data: [DONE]\r\n\r\n"
        for byte in raw: parser.feed(bytes([byte]))
        self.assertEqual(parser.finish(), "Hello café")
        self.assertEqual(values[0], "Hello café")

    def test_incomplete_rejected_even_when_some_english_shown(self):
        for tail in (b"", event(reason="stop"), b"data: [DONE]\n\n"):
            parser, values, _ = self.parser()
            with self.subTest(tail=tail), self.assertRaises(worker.WorkerError):
                parser.feed(event("Hello") + tail); parser.finish()
            self.assertEqual(values, ["Hello"])

    def test_length_refusal_error_and_late_content_rejected(self):
        for raw in (event("Hello", "length"), event("Hello", "content_filter"), event(error={"message": "private upstream content"}), event("Hello", "stop") + event(" extra"), event("x" * 4097)):
            parser, _, _ = self.parser()
            with self.assertRaises(worker.WorkerError): parser.feed(raw)

    def test_cancel_never_publishes_late_tokens(self):
        parser, values, cancel = self.parser(); cancel.cancel()
        with self.assertRaises(worker.Cancelled): parser.feed(event("Hello"))
        self.assertEqual(values, [])

    def test_bounds_and_malformed_utf8(self):
        for raw in (b"data: " + b"x" * 16385, b"data: \xff\n\n", b"data: {invalid}\n\n"):
            parser, _, _ = self.parser()
            with self.assertRaises((worker.WorkerError, UnicodeError, ValueError)): parser.feed(raw)

    def test_partial_arrives_before_response_finishes_without_retry(self):
        early, finish = threading.Event(), threading.Event()
        def behavior(h, server):
            h.send_response(200); h.send_header("Content-Type", "text/event-stream"); h.send_header("Transfer-Encoding", "chunked"); h.end_headers()
            # One HTTP chunk also streams incrementally; do not wait for its full length.
            raw = event("Hello") + event(".", "stop") + b"data: [DONE]\n\n"
            first = event("Hello")
            h.wfile.write((format(len(raw), "x") + "\r\n").encode() + first); h.wfile.flush()
            if finish.wait(1): h.wfile.write(raw[len(first):] + b"\r\n0\r\n\r\n"); h.wfile.flush()
        server = Server(behavior); self.addCleanup(server.close)
        provider = api_provider.CompatibleProvider(args_for(server, translation_timeout=2), "translation", worker.WorkerError, key_loader=lambda _: FAKE_KEY)
        provider.start(worker.Cancellation()); self.addCleanup(provider.close)
        output, errors = [], []
        def run():
            try: output.append(provider.translate_stream(worker.Job(1, "你好", "af_heart", 1), lambda text: early.set()))
            except Exception as error: errors.append(error)
        thread = threading.Thread(target=run); thread.start()
        try:
            self.assertTrue(early.wait(1)); self.assertTrue(thread.is_alive()); self.assertEqual(output, [])
        finally: finish.set(); thread.join(2)
        self.assertEqual(errors, []); self.assertEqual(output, ["Hello."]); self.assertEqual(len(server.calls), 1)
        self.assertTrue(server.calls[0]["payload"]["stream"])

    def test_json_answer_on_stream_request_consumed_once(self):
        server = Server(lambda h, s: respond(h, value={"choices": [{"message": {"content": "Hello"}, "finish_reason": "stop"}]})); self.addCleanup(server.close)
        provider = api_provider.CompatibleProvider(args_for(server), "translation", worker.WorkerError, key_loader=lambda _: FAKE_KEY)
        provider.start(worker.Cancellation()); self.addCleanup(provider.close)
        values = []
        self.assertEqual(provider.translate_stream(worker.Job(1, "你好", "af_heart", 1), values.append), "Hello")
        self.assertEqual(values, []); self.assertEqual(len(server.calls), 1)

    def test_explicit_nonstream_flag(self):
        server = Server(lambda h, s: respond(h)); self.addCleanup(server.close)
        provider = api_provider.CompatibleProvider(args_for(server, translation_api_no_stream=True), "translation", worker.WorkerError, key_loader=lambda _: FAKE_KEY)
        provider.start(worker.Cancellation()); self.addCleanup(provider.close)
        provider.translate_stream(worker.Job(1, "你好", "af_heart", 1), lambda _: self.fail("Unexpected partial"))
        self.assertFalse(server.calls[0]["payload"]["stream"])

if __name__ == "__main__": unittest.main()
