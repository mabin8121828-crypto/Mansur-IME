# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Pure interface/cancellation tests; providers never load models or play audio."""
import importlib.util
import asyncio
import io
import http.server
import os
from pathlib import Path
import threading
import time
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location("learning_worker", Path(__file__).resolve().parents[1] / "learning" / "worker.py")
worker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(worker)


class Collector:
    def __init__(self):
        self.events = []
        self.lock = threading.Lock()

    def send(self, event, current=lambda: True):
        if current():
            with self.lock:
                self.events.append(event)

    def control_error(self, code, request_id=None):
        self.send({"event": "error", "request_id": request_id, "code": code})

    def wait(self, event, request_id=None):
        until = time.monotonic() + 1
        while time.monotonic() < until:
            with self.lock:
                if any(e["event"] == event and e["request_id"] == request_id for e in self.events):
                    return
            time.sleep(0.005)
        raise AssertionError((event, request_id, self.events))


class FakeModels:
    def __init__(self):
        self.started = threading.Event()
        self.release = threading.Event()
        self.closed = False
        self.calls = []
        self.spoken = []
        self.fail_initialize = None
        self.block_translation = self.block_voice = False
        self.initializing = threading.Event()
        self.release_initialize = threading.Event()
        self.release_initialize.set()

    def initialize(self, cancel):
        self.initializing.set()
        while not self.release_initialize.wait(0.005):
            cancel.check()
        if self.fail_initialize:
            raise worker.WorkerError(self.fail_initialize)
        return {"translation_load_ms": 0, "voice_load_ms": 0}

    def translate(self, job):
        self.calls.append(job.request_id)
        self.started.set()
        if self.block_translation and job.request_id == 1:
            self.release.wait(1)  # Model deliberately ignores cancellation.
        return "Hello."

    def synthesize(self, job, text, emit):
        self.spoken.append((job.request_id, text))
        if self.block_voice and job.request_id == 1:
            self.release.wait(1)
        emit(b"\x00\x00\x01\x00", 24000)

    def close(self):
        self.closed = True
        self.release.set()
        self.release_initialize.set()

    def finish_thread(self):
        pass


def learn(identifier, **fields):
    return {"op": "learn", "request_id": identifier, "text": "你好", **fields}


class LearningWorkerTests(unittest.TestCase):
    def create(self, models=None):
        models = models or FakeModels()
        output = Collector()
        runner = worker.LearningWorker(models, output)
        self.addCleanup(runner.close)
        runner.start()
        return runner, output, models

    def test_event_sequence_and_pcm_shape(self):
        runner, output, _ = self.create()
        output.wait("ready")
        runner.accept(learn(1))
        output.wait("done", 1)
        events = [x for x in output.events if x["request_id"] == 1]
        self.assertEqual([x["event"] for x in events], ["translation", "audio", "done"])
        self.assertEqual(events[1]["sample_rate"], 24000)
        self.assertEqual(events[1]["channels"], 1)
        self.assertEqual(events[1]["pcm_s16le"], "AAABAA==")
        self.assertIn("first_audio_ms", events[2]["timings_ms"])

    def test_english_is_exactly_preserved_and_never_translated(self):
        runner, output, models = self.create()
        for identifier, source in enumerate(("Hello", "  McDonald's  CEO, Alice-Smith!  ", "Hello\nWorld\t2026"), 1):
            runner.accept(learn(identifier, text=source))
            output.wait("done", identifier)
            translated = next(e for e in output.events if e["event"] == "translation" and e["request_id"] == identifier)
            self.assertEqual((translated["text"], translated["source"]), (source, "original"))
            self.assertEqual(models.spoken[-1], (identifier, source))
        self.assertEqual(models.calls, [])

    def test_chinese_and_mixed_text_still_use_provider(self):
        runner, output, models = self.create()
        for identifier, source in enumerate(("你好", "Hello，你好", "Hello\U00020000"), 1):
            runner.accept(learn(identifier, text=source))
            output.wait("done", identifier)
            translated = next(e for e in output.events if e["event"] == "translation" and e["request_id"] == identifier)
            self.assertEqual(translated["source"], "translated")
        self.assertEqual(models.calls, [1, 2, 3])

    def test_only_numbers_punctuation_and_invalid_unicode_are_rejected(self):
        for source in ("12345", "!?，。", " \t\r\n ", "Hello\ud800", " " * 256 + "A"):
            with self.subTest(source=repr(source)), self.assertRaisesRegex(worker.WorkerError, "invalid_text"):
                worker.parse_learn(learn(1, text=source))

    def test_voice_initialization_does_not_start_translation_provider(self):
        fake_voice = SimpleNamespace(get_voices=lambda: list(worker.VOICES))
        fake_onnx = SimpleNamespace(disable_telemetry_events=lambda: None, SessionOptions=SimpleNamespace,
                                   InferenceSession=lambda *args, **kwargs: object())
        fake_kokoro = SimpleNamespace(Kokoro=SimpleNamespace(from_session=lambda *args: fake_voice))
        with tempfile.TemporaryDirectory(prefix="mansur-english-voice-") as temporary:
            voice_dir = Path(temporary)
            for name in ("kokoro-v1.0.onnx", "voices-v1.0.bin"):
                (voice_dir / name).write_bytes(b"fixture-not-a-real-model")
            for provider in ("local", "openrouter"):
                args = SimpleNamespace(translation_provider=provider, voice_model_dir=voice_dir, threads=1,
                                       llama_server=Path(__file__), translation_model=None, device="auto", gpu_layers="99",
                                       openrouter_model="fixture/model", openrouter_key_file=None)
                models = worker.LocalModels(args)
                self.addCleanup(models.close)
                self.addCleanup(models.finish_thread)
                with self.subTest(provider=provider), mock.patch.dict(sys.modules, {
                        "numpy": SimpleNamespace(), "onnxruntime": fake_onnx, "kokoro_onnx": fake_kokoro}), \
                        mock.patch("logging.disable"), mock.patch.object(models.translation, "start", side_effect=AssertionError("translation was prepared")):
                    timings = models.initialize(worker.Cancellation())
                    self.assertIn("voice_load_ms", timings)
                    self.assertNotIn("translation_load_ms", timings)
                    self.assertEqual(models.runtime_info()["translation_state"], "not_started")
                    self.assertTrue(models.runtime_info()["voice_ready"])
                # Use the production provider selection and lazy startup while
                # replacing only numeric model loading/audio with fixed fakes.
                models.initialize = lambda cancel: timings
                models.synthesize = lambda job, text, emit: emit(b"\x00\x00", 24000)
                runner, output, _ = self.create(models)
                runner.accept(learn(1, text="Hello")); output.wait("done", 1)
                self.assertFalse(models.translation_ready)
                runner.accept(learn(2)); output.wait("error", 2)
                error = next(e for e in output.events if e["event"] == "error" and e["request_id"] == 2)
                self.assertEqual(error["code"], "translation_model_missing" if provider == "local" else "api_key_missing")
                runner.accept(learn(3, text="Alice")); output.wait("done", 3)
                runner.close()

    def test_lazy_translation_initializes_once_and_cancelled_start_can_retry(self):
        models = worker.LocalModels(SimpleNamespace(translation_provider="local"))
        calls = []

        class Provider:
            def start(self, cancel):
                calls.append("start"); cancel.check()
            def translate(self, job):
                calls.append("translate"); return "Hello."
            def close(self):
                calls.append("close")

        class Cancels(Provider):
            def start(self, cancel):
                calls.append("cancel-start"); cancel.cancel(); cancel.check()

        models.translation = Cancels(); models._new_translation = Provider
        with self.assertRaises(worker.Cancelled):
            models.translate(worker.parse_learn(learn(1)))
        self.assertFalse(models.translation_ready)
        self.assertEqual(models.translate(worker.parse_learn(learn(2))), "Hello.")
        self.assertEqual(models.translate(worker.parse_learn(learn(3))), "Hello.")
        self.assertEqual(calls, ["cancel-start", "close", "start", "translate", "translate"])
        models.close()

    def test_translation_configuration_failure_does_not_fall_back_to_direct_network(self):
        for code in ("api_proxy_auth_required", "api_proxy_unsupported", "api_proxy_resolution_failed"):
            models = worker.LocalModels(SimpleNamespace(translation_provider="openrouter", translation_config_error=code))
            with self.subTest(code=code), mock.patch.object(models.translation, "start", side_effect=AssertionError("unexpected start")), \
                    mock.patch.object(models.translation, "translate", side_effect=AssertionError("unexpected HTTP")):
                with self.assertRaisesRegex(worker.WorkerError, code):
                    models.translate(worker.parse_learn(learn(1)))
                models.initialize = lambda cancel: {}
                models.synthesize = lambda job, text, emit: emit(b"\x00\x00", 24000)
                runner, output, _ = self.create(models)
                runner.accept(learn(1, text="Hello")); output.wait("done", 1)
                runner.close()

    def test_english_audio_still_obeys_cancel_and_latest_request(self):
        models = FakeModels(); models.block_voice = True
        runner, output, _ = self.create(models)
        runner.accept(learn(1, text="Old English")); output.wait("translation", 1)
        runner.accept(learn(2, text="New English")); models.release.set(); output.wait("done", 2)
        self.assertEqual(models.calls, [])
        self.assertFalse(any(e["request_id"] == 1 and e["event"] in ("audio", "done") for e in output.events))

    def test_latest_slot_and_late_translation(self):
        models = FakeModels()
        models.block_translation = True
        runner, output, _ = self.create(models)
        runner.accept(learn(1))
        self.assertTrue(models.started.wait(1))
        started = time.monotonic()
        for identifier in range(2, 21):
            runner.accept(learn(identifier))
        self.assertLess(time.monotonic() - started, 0.1)
        models.release.set()
        output.wait("done", 20)
        self.assertEqual(models.calls, [1, 20])
        self.assertEqual({x["request_id"] for x in output.events if x["event"] != "ready"}, {20})

    def test_cancelled_speech_never_publishes_late_chunk(self):
        models = FakeModels()
        models.block_voice = True
        runner, output, _ = self.create(models)
        runner.accept(learn(1))
        output.wait("translation", 1)
        runner.accept({"op": "cancel", "request_id": 1})
        runner.accept(learn(2, voice="bf_emma", speed=0.75))
        models.release.set()
        output.wait("done", 2)
        self.assertFalse(any(x["request_id"] == 1 and x["event"] in ("audio", "done") for x in output.events))

    def test_stream_generator_is_closed_on_cancel(self):
        started = threading.Event()
        finalized = threading.Event()

        class FakeVoice:
            async def create_stream(self, *args, **kwargs):
                try:
                    started.set()
                    await asyncio.sleep(10)
                    yield None
                finally:
                    finalized.set()

        models = worker.LocalModels(SimpleNamespace())
        models.voice = FakeVoice()
        models.loop = asyncio.new_event_loop()
        job = worker.parse_learn(learn(1))
        errors = []

        def run():
            try:
                models.synthesize(job, "Hello.", lambda *args: self.fail("Cancelled audio was emitted"))
            except Exception as error:
                errors.append(type(error))

        thread = threading.Thread(target=run, daemon=True)
        thread.start()
        try:
            self.assertTrue(started.wait(1))
            job.cancel.cancel()
            thread.join(0.3)
            self.assertFalse(thread.is_alive())
            self.assertTrue(finalized.is_set())
            self.assertEqual(errors, [worker.Cancelled])
        finally:
            models.finish_thread()

    def test_request_failure_does_not_poison_next_request(self):
        class FailsOnce(FakeModels):
            def translate(self, job):
                if job.request_id == 1:
                    raise worker.WorkerError("translation_connection_failed")
                return super().translate(job)

        runner, output, _ = self.create(FailsOnce())
        runner.accept(learn(1))
        output.wait("error", 1)
        runner.accept(learn(2))
        output.wait("done", 2)

    def test_model_error_does_not_block_new_requests(self):
        models = FakeModels()
        models.fail_initialize = "voice_model_missing"
        runner, output, _ = self.create(models)
        output.wait("error")
        runner.accept(learn(1))
        output.wait("error", 1)
        runner.accept(learn(2))
        output.wait("error", 2)
        self.assertTrue(all(e.get("code") == "voice_model_missing" for e in output.events))

    def test_local_voice_failure_keeps_translation_and_following_requests(self):
        class Translation:
            def start(self, cancel): cancel.check()
            def translate_stream(self, job, partial): partial("Hello"); return "Hello."
            def close(self): pass
        args = SimpleNamespace(translation_provider="compatible", speech_provider="local", translation_config_error="api_key_missing")
        models = worker.LocalModels(args, "voice_runtime_missing")
        args.translation_config_error = None; models.translation = Translation()
        runner, output, _ = self.create(models); output.wait("ready")
        ready = next(e for e in output.events if e["event"] == "ready")
        self.assertFalse(ready["runtime"]["voice_ready"])
        for identifier in (1, 2):
            runner.accept(learn(identifier)); output.wait("error", identifier)
            events = [e for e in output.events if e["request_id"] == identifier]
            self.assertEqual([e["event"] for e in events], ["translation_partial", "translation", "error"])
            self.assertEqual(events[-1]["stage"], "speech"); self.assertEqual(events[-1]["code"], "voice_runtime_missing")

    def test_local_english_survives_missing_voice_and_translation(self):
        args = SimpleNamespace(translation_provider="compatible", speech_provider="local", translation_config_error="api_key_missing")
        models = worker.LocalModels(args, "voice_runtime_missing")
        runner, output, _ = self.create(models); output.wait("ready"); runner.accept(learn(1, text="Hello Mansur")); output.wait("error", 1)
        translated = next(e for e in output.events if e["event"] == "translation")
        self.assertEqual(translated["text"], "Hello Mansur"); self.assertEqual(translated["source"], "original")
        self.assertFalse(models.translation_ready)

    def test_loading_keeps_only_latest_request(self):
        models = FakeModels()
        models.release_initialize.clear()
        runner, output, _ = self.create(models)
        self.assertTrue(models.initializing.wait(1))
        runner.accept(learn(1))
        runner.accept(learn(2))
        models.release_initialize.set()
        output.wait("done", 2)
        self.assertEqual(models.calls, [2])

    def test_cancel_old_id_preserves_new_request(self):
        models = FakeModels()
        models.release_initialize.clear()
        runner, output, _ = self.create(models)
        runner.accept(learn(2))
        runner.accept({"op": "cancel", "request_id": 1})
        models.release_initialize.set()
        output.wait("done", 2)

    def test_invalid_request_preserves_valid_request(self):
        models = FakeModels()
        models.release_initialize.clear()
        runner, output, _ = self.create(models)
        runner.accept(learn(1))
        with self.assertRaises(worker.WorkerError):
            runner.accept(learn(2, speed=float("nan")))
        models.release_initialize.set()
        output.wait("done", 1)

    def test_validation_and_duplicate_ids(self):
        runner, _, _ = self.create()
        for fields in ({"request_id": True}, {"text": ""}, {"text": "x" * 257}, {"text": "\x00"},
                       {"voice": "unknown"}, {"speed": True}, {"speed": 1.26}):
            packet = learn(1)
            packet.update(fields)
            with self.subTest(fields=fields), self.assertRaises(worker.WorkerError):
                runner.accept(packet)
        runner.accept(learn(1))
        with self.assertRaisesRegex(worker.WorkerError, "request_id_not_increasing"):
            runner.accept(learn(1))

    def test_close_cleans_owned_provider(self):
        runner, _, models = self.create()
        runner.accept({"op": "close"})
        runner.close()
        self.assertTrue(models.closed)
        self.assertFalse(runner.thread.is_alive())

    def test_oversized_line_and_eof_close(self):
        runner, output, _ = self.create()
        worker.read_packets(io.BytesIO(b"x" * 8193 + b"\n"), runner, output)
        self.assertTrue(runner.stop.event.is_set())
        self.assertTrue(any(e.get("code") == "request_too_large" for e in output.events))

    def test_registered_operation_is_interrupted(self):
        cancel = worker.Cancellation()
        called = threading.Event()
        with cancel.interrupt_with(called.set):
            cancel.cancel()
        self.assertTrue(called.is_set())
        with self.assertRaises(worker.Cancelled):
            cancel.check()

    def test_device_selection_avoids_shared_memory_and_low_vram(self):
        listing = """Available devices:
  Vulkan0: NVIDIA GeForce RTX 2070 (8220 MiB, 7383 MiB free)
  Vulkan1: Intel(R) UHD Graphics 630 (16308 MiB, 15540 MiB free)
  Vulkan2: AMD Radeon RX 7600 (8192 MiB, 4096 MiB free)
"""
        self.assertEqual(worker.choose_discrete_device(listing, 4000), "Vulkan0")
        self.assertIsNone(worker.choose_discrete_device(listing, 8000))
        self.assertIsNone(worker.choose_discrete_device("Vulkan1: Intel UHD (16308 MiB, 15540 MiB free)", 4000))

    def test_auto_device_zero_layers_becomes_offload(self):
        args = SimpleNamespace(device="auto", gpu_layers="0")
        server = worker.LlamaServer(args)
        server._discover = lambda cancel: "Vulkan9"
        server._configure_device(worker.Cancellation())
        self.assertEqual((server.selected_device, server.selected_gpu_layers), ("Vulkan9", "all"))

    def test_no_suitable_device_uses_cpu_zero_layers(self):
        args = SimpleNamespace(device="auto", gpu_layers="99")
        server = worker.LlamaServer(args)
        server._discover = lambda cancel: None
        server._configure_device(worker.Cancellation())
        self.assertEqual((server.selected_device, server.selected_gpu_layers), ("none", "0"))
        self.assertEqual(server.fallback_reason, "no_suitable_gpu")

    def test_gpu_start_failure_retries_cpu_once(self):
        args = SimpleNamespace(llama_server=Path(__file__), translation_model=Path(__file__),
                               device="auto", gpu_layers="99")

        class StartsFailing(worker.LlamaServer):
            def _discover(self, cancel):
                return "Vulkan9"

            def _start_selected(self, cancel):
                attempts.append((self.selected_device, self.selected_gpu_layers))
                raise worker.WorkerError("translation_start_failed")

        attempts = []
        server = StartsFailing(args)
        with self.assertRaises(worker.WorkerError):
            server.start(worker.Cancellation())
        self.assertEqual(attempts, [("Vulkan9", "99"), ("none", "0")])

    def test_cancel_does_not_retry_failed_gpu(self):
        args = SimpleNamespace(llama_server=Path(__file__), translation_model=Path(__file__),
                               device="Vulkan8", gpu_layers="99")

        class CancelDuringStart(worker.LlamaServer):
            def _start_selected(self, cancel):
                attempts.append(self.selected_device)
                cancel.cancel()
                raise worker.WorkerError("translation_start_failed")

        attempts = []
        with self.assertRaises(worker.Cancelled):
            CancelDuringStart(args).start(worker.Cancellation())
        self.assertEqual(attempts, ["Vulkan8"])

    def test_http_cancel_interrupts_delayed_response_body(self):
        headers_sent = threading.Event()
        release = threading.Event()

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Length", "100")
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.flush()
                headers_sent.set()
                release.wait(2)

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        serving = threading.Thread(target=server.serve_forever, daemon=True)
        serving.start()
        client = worker.LlamaServer(SimpleNamespace())
        client.port, client.secret = server.server_port, "test-token"
        cancel = worker.Cancellation()
        result = []

        def request():
            try:
                client.exchange("GET", "/", None, cancel, 10)
            except Exception as error:
                result.append(type(error))

        requester = threading.Thread(target=request, daemon=True)
        requester.start()
        try:
            self.assertTrue(headers_sent.wait(1))
            cancel.cancel()
            requester.join(1.2)
            self.assertFalse(requester.is_alive())
            self.assertEqual(result, [worker.Cancelled])
        finally:
            release.set()
            server.shutdown()
            server.server_close()

    def test_backpressured_control_errors_do_not_block_input(self):
        # No writer is started: a completely full channel models stalled stdout.
        output = worker.Output.__new__(worker.Output)
        output.items = worker.queue.Queue(maxsize=1)
        output.items.put(({}, lambda: True))
        started = time.monotonic()
        for _ in range(100):
            output.control_error("invalid_json")
        self.assertLess(time.monotonic() - started, 0.1)

    @unittest.skipUnless(os.name == "nt", "Windows process ownership")
    def test_owned_child_job_terminates_only_created_child(self):
        process = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NO_WINDOW)
        job = None
        try:
            job = worker.OwnedChildJob(process)
            job.close()
            process.wait(timeout=2)
            self.assertIsNotNone(process.returncode)
        finally:
            if job:
                job.close()
            if process.poll() is None:
                process.kill()
                process.wait()


if __name__ == "__main__":
    unittest.main()
