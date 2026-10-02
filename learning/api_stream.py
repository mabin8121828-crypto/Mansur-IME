# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Bounded SSE translation decoder. No prompts, responses or credentials are logged."""
import codecs
import json
import time


class TranslationStream:
    def __init__(self, error_type, partial, cancel):
        self.error_type, self.partial, self.cancel = error_type, partial, cancel
        self.decoder = codecs.getincrementaldecoder("utf-8-sig")("strict")
        self.buffer, self.data, self.text = "", [], ""
        self.total, self.record_size = 0, 0
        self.stopped = self.done = False
        self.last_publish = 0

    def invalid(self):
        raise self.error_type("translation_response_invalid")

    def feed(self, raw):
        self.cancel.check()
        self.total += len(raw)
        if self.total > 512 * 1024: self.invalid()
        self.buffer += self.decoder.decode(raw)
        while "\n" in self.buffer:
            line, self.buffer = self.buffer.split("\n", 1)
            if line.endswith("\r"): line = line[:-1]
            if len(line) > 16384: self.invalid()
            if not line:
                if self.data: self._event("\n".join(self.data))
                self.data, self.record_size = [], 0
            elif line.startswith("data:"):
                payload = line[5:]
                if payload.startswith(" "): payload = payload[1:]
                self.record_size += len(payload)
                if self.record_size > 16384: self.invalid()
                self.data.append(payload)
            # SSE comments, event names and ids carry no translated text.
        if len(self.buffer) > 16384: self.invalid()

    def _event(self, data):
        if self.done: self.invalid()
        if data == "[DONE]":
            if not self.stopped: self.invalid()
            self.done = True
            return
        value = json.loads(data)
        if not isinstance(value, dict): self.invalid()
        if "error" in value: raise self.error_type("api_provider_unavailable")
        choices = value.get("choices")
        if choices == [] and isinstance(value.get("usage"), dict): return
        if not isinstance(choices, list) or len(choices) != 1 or not isinstance(choices[0], dict): self.invalid()
        choice = choices[0]
        if choice.get("index", 0) != 0: self.invalid()
        delta = choice.get("delta")
        if not isinstance(delta, dict) or delta.get("tool_calls") or delta.get("function_call"): self.invalid()
        content = delta.get("content")
        if content is not None:
            if not isinstance(content, str) or (self.stopped and content): self.invalid()
            self.text += content
            if len(self.text) > 4096 or any((ord(c) < 32 and c not in "\r\n\t") or 0xD800 <= ord(c) <= 0xDFFF for c in content): self.invalid()
            now = time.monotonic()
            if content and (not self.last_publish or now - self.last_publish >= .08):
                self.cancel.check()
                self.partial(self.text)
                self.last_publish = now
        reason = choice.get("finish_reason")
        if reason is not None:
            if reason != "stop": self.invalid()
            self.stopped = True

    def finish(self):
        self.cancel.check()
        self.buffer += self.decoder.decode(b"", final=True)
        # An unterminated record/disconnected generation is never a final sentence.
        if self.buffer.strip() or self.data or not self.done or not self.stopped or not self.text.strip(): self.invalid()
        result = self.text.strip()
        if len(result) > 1000 or not any(c.isascii() and c.isalpha() for c in result): self.invalid()
        return result
