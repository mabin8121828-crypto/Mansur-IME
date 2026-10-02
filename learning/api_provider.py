# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Configured OpenAI-compatible text and speech. Only confirmed jobs use inference."""
from __future__ import annotations
import asyncio
import contextlib
import importlib.util
import io
import ipaddress
import json
from pathlib import Path
import re
import struct
from urllib.parse import urlsplit
import wave

_spec = importlib.util.spec_from_file_location("_mansur_cloud_transport", Path(__file__).resolve().with_name("openrouter_provider.py"))
_base = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_base)
_stream_spec = importlib.util.spec_from_file_location("_mansur_translation_stream", Path(__file__).resolve().with_name("api_stream.py"))
_stream_module = importlib.util.module_from_spec(_stream_spec)
_stream_spec.loader.exec_module(_stream_module)


def endpoint(value, error_type):
    try:
        url = urlsplit(value)
        local = url.hostname == "localhost"
        if not local:
            with contextlib.suppress(ValueError):
                local = ipaddress.ip_address(url.hostname).is_loopback
        if not url.hostname or url.username is not None or url.password is not None or url.query or url.fragment or len(value) > 2048:
            raise ValueError()
        if url.scheme != "https" and not (url.scheme == "http" and local):
            raise ValueError()
        if any(ord(c) < 33 or ord(c) > 126 or c == "\\" for c in value) or not 1 <= (url.port or (443 if url.scheme == "https" else 80)) <= 65535:
            raise ValueError()
        return url
    except (ValueError, TypeError):
        raise error_type("api_base_url_invalid") from None


class CompatibleProvider(_base.OpenRouterProvider):
    def __init__(self, args, capability, error_type=_base.ProviderError, key_loader=None, connection_factory=None):
        self.capability = capability
        self.url = endpoint(getattr(args, capability + "_api_base_url", ""), error_type)
        self.model = getattr(args, capability + "_api_model", "")
        self.service = getattr(args, capability + "_api_service_id", "")
        if not re.fullmatch(capability + r"-[a-z0-9-]{1,48}", self.service) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/@-]{0,199}", self.model):
            raise error_type("api_service_invalid")
        self.key_file = getattr(args, capability + "_api_key_file", None)
        self.config_error = getattr(args, capability + "_config_error", None)
        self.maximum = 65536 if capability == "translation" else 12 * 1024 * 1024
        self.content_type = ""
        self.partial = None
        self.test_loader = key_loader is not None
        super().__init__(args, error_type, key_loader=key_loader, connection_factory=connection_factory or self._connect)

    def start(self, cancel):
        cancel.check()
        if self.config_error:
            raise self.error_type(self.config_error)
        entropy = _base.ENTROPY if self.service == "translation-openrouter" else ("MansurNext.ApiService.v1:" + self.service).encode("utf-8")
        # An injected test loader sees only a disposable fixture, never user credentials.
        self.key = self.key_loader(self.key_file) if self.key_loader is not None and getattr(self, "test_loader", False) else _base.load_windows_key(self.key_file, self.error_type, entropy)
        if not _base.valid_key(self.key):
            raise self.error_type("api_key_unreadable")
        cancel.check()

    async def _connect(self):
        host, port = self.url.hostname, self.url.port or (443 if self.url.scheme == "https" else 80)
        proxy = getattr(self.args, self.capability + "_api_proxy_host", "")
        proxy_port = getattr(self.args, self.capability + "_api_proxy_port", 0)
        if not self.test_loader:
            proxy, proxy_port = _base.live_proxy(self.url.geturl(), proxy, proxy_port, self.error_type)
        tls = self._tls_context() if self.url.scheme == "https" else None
        if not proxy:
            return await asyncio.open_connection(host, port, ssl=tls, server_hostname=host if tls else None, limit=65536)
        if tls is None:
            raise self.error_type("api_proxy_unsupported")
        writer = None
        try:
            reader, writer = await asyncio.open_connection(proxy, proxy_port, limit=65536)
            authority = ("[" + host + "]" if ":" in host else host) + ":" + str(port)
            writer.write(("CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\n\r\n").encode("ascii")); await writer.drain()
            raw = await reader.readuntil(b"\r\n\r\n"); first = raw.split(b"\r\n", 1)[0].split(b" ", 2)
            if len(raw) > 16384 or len(first) < 2 or first[0] not in (b"HTTP/1.0", b"HTTP/1.1") or first[1] != b"200":
                raise self.error_type("api_proxy_auth_required" if len(first) > 1 and first[1] == b"407" else "api_proxy_connect_failed")
            await writer.start_tls(tls, server_hostname=host, ssl_handshake_timeout=self.args.translation_timeout)
            return reader, writer
        except BaseException:
            if writer is not None:
                writer.close()
                with contextlib.suppress(Exception, asyncio.CancelledError):
                    await asyncio.wait_for(writer.wait_closed(), .25)
            raise

    def invalid_response(self):
        raise self.error_type("translation_response_invalid" if self.capability == "translation" else "voice_audio_invalid")

    def payload(self, job):
        study = getattr(job, "operation", None) == "selection_study"
        value = {"model": self.model, "messages": job.study_messages if study else [{"role": "system", "content": _base.SYSTEM_PROMPT}, {"role": "user", "content": job.text}], "temperature": 0, "max_tokens": 512, "stream": self.partial is not None and not study}
        # Vendor options apply only at official hosts, never an edited custom URL.
        host, model = self.url.hostname.lower(), self.model.lower()
        aliyun = host in ("dashscope.aliyuncs.com", "dashscope-intl.aliyuncs.com", "dashscope-us.aliyuncs.com") or host.endswith(".maas.aliyuncs.com")
        mandatory_qwen = "thinking" in model or model.startswith("qwen3.8-2.4t-a95b") or model in ("qwen3.7-max-preview", "qwen3.7-max-2026-05-17")
        hybrid_qwen = model in ("qwen-plus", "qwen-flash") or re.fullmatch(r"qwen3(?:\.[0-9]+)?-(?:max|plus|flash|[0-9]+b(?:-a[0-9]+b)?)(?:-[0-9-]+)?", model)
        if aliyun and model.startswith("qwen") and mandatory_qwen:
            value["max_tokens"] = 4096
        elif aliyun and hybrid_qwen:
            value["enable_thinking"] = False
        elif host == "api.moonshot.cn" and model.startswith("kimi-k"):
            value.pop("temperature")  # K2.6/K2.7/K3 reject temperature=0.
            if model == "kimi-k2.6": value["thinking"] = {"type": "disabled"}
            else:
                value["max_tokens"] = 4096
                if model == "kimi-k3": value["reasoning_effort"] = "low"
        elif host == "open.bigmodel.cn" and model.startswith(("glm-4.5", "glm-4.6", "glm-4.7", "glm-5")):
            if model.startswith("glm-5.3"):
                value.pop("temperature"); value["max_tokens"] = 4096
            else: value["thinking"] = {"type": "disabled"}
        elif host in ("api.minimax.cn", "api.minimaxi.com", "api.minimax.io") and model.startswith("minimax-"):
            value["reasoning_split"] = True
            value.pop("temperature")
            if model == "minimax-m3": value["thinking"] = {"type": "disabled"}
            else: value["max_tokens"] = 4096
        return value

    def translate_stream(self, job, partial):
        if getattr(self.args, "translation_api_no_stream", False): return self.translate(job)
        self.partial = partial
        try: return self.translate(job)
        finally: self.partial = None

    def consume(self, content, job=None):
        value = json.loads(content)
        if not isinstance(value, dict): self.invalid_response()
        return _base.translation_text(value, self.error_type, getattr(job, "operation", None) == "selection_study")

    async def _exchange(self, job, key):
        writer = None
        try:
            reader, writer = await self.connection_factory(); job.cancel.check()
            body = json.dumps(self.payload(job), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            suffix = "/chat/completions" if self.capability == "translation" else "/audio/speech"
            host = "[" + self.url.hostname + "]" if ":" in self.url.hostname else self.url.hostname
            if self.url.port: host += ":" + str(self.url.port)
            path = self.url.path.rstrip("/") + suffix
            headers = ("POST " + path + " HTTP/1.1\r\nHost: " + host + "\r\nAuthorization: Bearer " + key + "\r\nContent-Type: application/json\r\nAccept: */*\r\nConnection: close\r\nContent-Length: " + str(len(body)) + "\r\n\r\n").encode("ascii")
            writer.write(headers + body); await writer.drain()
            raw = await reader.readuntil(b"\r\n\r\n")
            if len(raw) > 16384: self.invalid_response()
            lines = raw[:-4].split(b"\r\n"); first = lines[0].split(b" ", 2)
            if len(first) < 2 or first[0] not in (b"HTTP/1.0", b"HTTP/1.1") or not first[1].isdigit(): self.invalid_response()
            status = int(first[1])
            if 300 <= status < 400: raise self.error_type("api_redirect_rejected")
            if status != 200: raise self.error_type(self.status_code(status))
            fields = {}
            for line in lines[1:]:
                if b":" not in line or line[:1] in (b" ", b"\t"): self.invalid_response()
                name, value = line.split(b":", 1); name = name.strip().lower()
                if name in fields and name in (b"content-length", b"transfer-encoding", b"content-type"): self.invalid_response()
                fields[name] = value.strip().lower()
            self.content_type = fields.get(b"content-type", b"").decode("ascii", errors="replace")
            if fields.get(b"content-encoding", b"identity") != b"identity": self.invalid_response()
            length, encoding = fields.get(b"content-length"), fields.get(b"transfer-encoding")
            if encoding is not None and (encoding != b"chunked" or length is not None): self.invalid_response()
            content = bytearray()
            stream = _stream_module.TranslationStream(self.error_type, self.partial, job.cancel) if self.partial is not None and self.content_type.split(";", 1)[0].strip() == "text/event-stream" else None
            maximum = 512 * 1024 if stream is not None else self.maximum
            total = 0
            def consume_chunk(chunk):
                nonlocal total
                job.cancel.check(); total += len(chunk)
                if total > maximum: self.invalid_response()
                if stream is not None: stream.feed(chunk)
                else: content.extend(chunk)
            if encoding == b"chunked":
                while True:
                    line = await reader.readuntil(b"\r\n"); token = line[:-2].split(b";", 1)[0]
                    if len(line) > 128 or not 1 <= len(token) <= 8 or any(c not in b"0123456789abcdefABCDEF" for c in token): self.invalid_response()
                    size = int(token, 16)
                    if size == 0:
                        trailers = 0
                        while True:
                            line = await reader.readuntil(b"\r\n"); trailers += len(line)
                            if trailers > 8192: self.invalid_response()
                            if line == b"\r\n": break
                        break
                    if total + size > maximum: self.invalid_response()
                    remaining = size
                    while remaining:
                        chunk = await reader.read(min(remaining, 8192))
                        if not chunk: self.invalid_response()
                        consume_chunk(chunk); remaining -= len(chunk)
                    if await reader.readexactly(2) != b"\r\n": self.invalid_response()
            elif length is not None:
                if not length.isdigit() or len(length) > 9 or int(length) > maximum: self.invalid_response()
                remaining = int(length)
                while remaining:
                    chunk = await reader.read(min(remaining, 8192))
                    if not chunk: self.invalid_response()
                    consume_chunk(chunk); remaining -= len(chunk)
            else:
                while True:
                    chunk = await reader.read(8192)
                    if not chunk: break
                    consume_chunk(chunk)
            job.cancel.check(); return stream.finish() if stream is not None else self.consume(content, job)
        finally:
            if writer is not None:
                writer.close()
                with contextlib.suppress(Exception, asyncio.CancelledError): await asyncio.wait_for(writer.wait_closed(), .25)


class CompatibleSpeech(CompatibleProvider):
    def __init__(self, args, error_type=_base.ProviderError, key_loader=None, connection_factory=None):
        super().__init__(args, "speech", error_type, key_loader, connection_factory)
        self.voice = getattr(args, "speech_api_voice", "")
        self.format = getattr(args, "speech_api_format", "wav")
        self.rate = getattr(args, "speech_api_sample_rate", 24000)
        if not isinstance(self.voice, str) or not 1 <= len(self.voice) <= 200 or any(ord(c) < 32 for c in self.voice) or self.format not in ("wav", "pcm") or not 8000 <= self.rate <= 96000:
            raise error_type("api_audio_format_invalid")
        if self.service == "speech-stepfun" and self.rate not in (8000, 16000, 22050, 24000, 48000):
            raise error_type("api_audio_format_invalid")
        self.text = ""

    def payload(self, job):
        value = {"model": self.model, "input": self.text, "voice": self.voice, "response_format": self.format, "speed": job.speed}
        if self.service in ("speech-siliconflow", "speech-stepfun"): value["sample_rate"] = self.rate
        return value

    def consume(self, content, job=None):
        mime = self.content_type.split(";", 1)[0].strip()
        if not content or mime not in ("audio/wav", "audio/x-wav", "audio/wave", "audio/pcm", "audio/L16".lower(), "application/octet-stream"):
            self.invalid_response()
        if self.format == "pcm" and mime in ("audio/wav", "audio/x-wav", "audio/wave", "audio/l16"):
            self.invalid_response()
        return bytes(content)

    def synthesize(self, job, text, callback):
        self.text = text
        try:
            content = self.translate(job)
            if self.format == "wav":
                with wave.open(io.BytesIO(content), "rb") as audio:
                    channels, rate = audio.getnchannels(), audio.getframerate()
                    if audio.getcomptype() != "NONE" or audio.getsampwidth() != 2 or channels not in (1, 2) or not 8000 <= rate <= 96000 or not 1 <= audio.getnframes() <= rate * 120: self.invalid_response()
                    # Validate the entire bounded WAV before queuing any playback.
                    frames = audio.readframes(audio.getnframes() + 1)
                    if len(frames) != audio.getnframes() * 2 * channels: self.invalid_response()
                    for offset in range(0, len(frames), 48000 * channels):
                        job.cancel.check(); pcm = frames[offset:offset + 48000 * channels]
                        if channels == 2:
                            values = struct.unpack("<" + "h" * (len(pcm) // 2), pcm)
                            pcm = struct.pack("<" + "h" * (len(values) // 2), *((values[i] + values[i + 1]) // 2 for i in range(0, len(values), 2)))
                        callback(pcm, rate)
            else:
                if len(content) % 2 or content.startswith((b"RIFF", b"ID3", b"{", b"[")) or len(content) > self.rate * 120 * 2: self.invalid_response()
                for offset in range(0, len(content), 48000): job.cancel.check(); callback(content[offset:offset + 48000], self.rate)
        except (wave.Error, EOFError, ValueError, struct.error):
            self.invalid_response()
        finally:
            self.text = ""
