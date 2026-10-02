#!/usr/bin/env python3
# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Check public documentation links, image integrity and the source allowlist."""
from __future__ import annotations

import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import re
import struct
from urllib.parse import unquote, urlsplit

from export_open_source import source_files, validate_file


class HtmlLinks(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.links: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        for name, value in attrs:
            if name in {"src", "href"} and value:
                self.links.append(value)


def links_in(text: str) -> list[str]:
    # Fenced examples are not navigational links; retain inline link labels.
    text = re.sub(r"(?ms)^```[^\n]*\n.*?^```[^\n]*$", "", text)
    html = HtmlLinks()
    html.feed(text)
    markdown = re.findall(r"!?\[[^\]\n]*\]\((<[^>]+>|[^\s)]+)(?:\s+[^)]*)?\)", text)
    return html.links + [link.strip("<>") for link in markdown]


def check(root: Path) -> dict:
    root = root.resolve()
    selected = source_files(root)
    allowed = {file.resolve() for file in selected}
    link_count = 0
    for file in selected:
        relative = file.relative_to(root).as_posix()
        data = file.read_bytes()
        validate_file(relative, data)
        if file.suffix.lower() != ".md":
            continue
        for link in links_in(data.decode("utf-8-sig")):
            url = urlsplit(link)
            if url.scheme in {"https", "http", "mailto"} or url.netloc or not url.path:
                continue
            if url.scheme:
                raise ValueError("Unsupported public link in " + relative)
            target = (file.parent / unquote(url.path)).resolve()
            if target not in allowed:
                raise ValueError("Missing or non-public link in " + relative + ": " + link)
            link_count += 1

    media = root / "docs" / "media"
    manifest = json.loads((media / "ASSETS.json").read_text(encoding="utf-8"))
    expected = {file.name for file in selected if file.parent == media and file.suffix == ".png"}
    entries = manifest["assets"]
    if len(entries) != len(expected) or {item["file"] for item in entries} != expected:
        raise ValueError("Media manifest must cover exactly the public PNG images")
    for item in entries:
        file = media / item["file"]
        data = file.read_bytes()
        if len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
            raise ValueError("Invalid PNG header: " + item["file"])
        width, height = struct.unpack(">II", data[16:24])
        if (len(data) != item["bytes"] or hashlib.sha256(data).hexdigest() != item["sha256"]
                or width != item["width"] or height != item["height"]):
            raise ValueError("Media integrity mismatch: " + item["file"])
    homepage = (root / "README.md").read_text(encoding="utf-8")
    if "docs/media/mansur-poster.png" not in links_in(homepage):
        raise ValueError("Homepage poster link missing")
    return {"status": "DOCS_CHECK_PASS", "public_files": len(selected),
            "relative_links": link_count, "images": len(entries)}


if __name__ == "__main__":
    try:
        print(json.dumps(check(Path(__file__).resolve().parents[1]), ensure_ascii=False))
    except (ValueError, OSError, KeyError) as error:
        raise SystemExit(str(error)) from error
