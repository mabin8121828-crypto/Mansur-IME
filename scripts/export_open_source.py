#!/usr/bin/env python3
# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Create a fresh source-only export; never upload, register or install it."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import zipfile

ROOT_FILES = ("LICENSE", "COPYRIGHT.md", "NOTICE", "THIRD_PARTY_NOTICES.md",
              "README.md", "README.en.md", "CHANGELOG.md", "CONTRIBUTING.md",
              ".gitignore", ".gitattributes", "CMakeLists.txt")
PUBLIC_DOCS = ("BUILDING.md", "PRIVACY.md", "OPEN_SOURCE.md", "DOMESTIC_PROVIDERS.md",
               "LOCAL_MODELS.md", "local-model-downloads.json", "lexicon-example.tsv",
               "QUICK_START.md", "USER_GUIDE.md", "FAQ.md", "KNOWN_ISSUES.md",
               "media/README.md", "media/ASSETS.json", "media/mansur-poster.png",
               "media/candidate.png", "media/english-popup.png", "media/word-learning.png",
               "media/settings-appearance.png", "media/models-translation.png", "media/models-speech.png")
PUBLIC_GITHUB_FILES = (".github/ISSUE_TEMPLATE/bug_report.yml",
                       ".github/ISSUE_TEMPLATE/feature_request.yml",
                       ".github/ISSUE_TEMPLATE/config.yml", ".github/workflows/ci.yml")
CODE_DIRS = ("core", "include", "windows", "desktop", "learning", "installer", "tools", "scripts", "tests")
CODE_SUFFIXES = {".cpp", ".hpp", ".cs", ".py", ".ps1", ".def", ".manifest", ".config"}
SECRET_PATTERNS = (
    re.compile(rb"sk-(?:or-v1-)?[A-Za-z0-9_-]{48,}"),
    re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
)
FORBIDDEN_PARTS = {".git", "__pycache__", ".venv", "venv", "models", "runtime", "build", "dist"}

def source_files(root: Path) -> list[Path]:
    files = {root / name for name in ROOT_FILES}
    files.update(root / "docs" / name for name in PUBLIC_DOCS)
    files.update(root / name for name in PUBLIC_GITHUB_FILES)
    for directory in CODE_DIRS:
        for file in (root / directory).rglob("*"):
            if any(part in FORBIDDEN_PARTS for part in file.relative_to(root).parts):
                continue
            if file.is_symlink():
                raise ValueError("Symlink rejected in source selection")
            if not file.is_file():
                continue
            relative = file.relative_to(root)
            asset = relative.parts[:2] == ("desktop", "iconassets")
            # Include upstream artwork/license/provenance verbatim, including
            # its self-authored renderer. No local docs or JSON configs elsewhere.
            if asset or file.suffix.lower() in CODE_SUFFIXES or file.name == "CMakeLists.txt":
                files.add(file)
    files.update((root / "licenses").glob("*.txt"))
    for file in files:
        if not file.is_file() or file.is_symlink() or not file.resolve().is_relative_to(root.resolve()):
            raise ValueError("Required export file missing or outside source root")
    return sorted(files, key=lambda value: value.relative_to(root).as_posix())

def validate_file(relative: str, data: bytes) -> None:
    if any(pattern.search(data) for pattern in SECRET_PATTERNS):
        # Never print the matching bytes or credential value.
        raise ValueError("Potential secret detected in " + relative)
    if relative.endswith((".cpp", ".hpp", ".cs", ".py", ".ps1")):
        prefix = data.decode("utf-8-sig").splitlines()[:8]
        if not any("SPDX-License-Identifier: MIT" in line for line in prefix):
            raise ValueError("Missing original source license header in " + relative)

def export(root: Path, name: str) -> dict:
    if not re.fullmatch(r"Mansur-source-[A-Za-z0-9._-]{1,80}", name):
        raise ValueError("Invalid source export name")
    root = root.resolve()
    output = root / "output" / "open-source"
    if output.is_symlink() or not output.resolve().is_relative_to(root):
        raise ValueError("Export directory outside workspace")
    folder, archive = output / name, output / (name + ".zip")
    if folder.exists() or archive.exists():
        raise ValueError("Export already exists; choose a fresh name")
    # Validate the complete allowlist before creating any output. No reading of
    # external user state, credentials, clipboard, installed models or Git history.
    payloads = []
    for file in source_files(root):
        relative = file.relative_to(root).as_posix()
        data = file.read_bytes()
        validate_file(relative, data)
        payloads.append((relative, data))
    legal = {name for name, _ in payloads}
    if not {"licenses/AOSP-NOTICE.txt", "licenses/JIEBA-MIT.txt",
            "desktop/iconassets/lucide/LICENSE", "desktop/iconassets/services/LICENSE"} <= legal:
        raise ValueError("Third-party license inventory incomplete")
    output.mkdir(parents=True, exist_ok=True)
    folder.mkdir()  # No overwriting or recursive cleanup on failures.
    entries = []
    for relative, data in payloads:
        target = folder / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as stream:
            stream.write(data)
        entries.append({"path": relative, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    manifest = {"project": "Mansur", "public_author": "Mansur", "status": "alpha-source",
                "license": "MIT for original material; retained upstream licenses apply separately",
                "scope": "Source allowlist; no user state, binaries, models, local history or upload",
                "files": entries}
    (folder / "source-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as bundle:
        for file in sorted(folder.rglob("*")):
            if file.is_file():
                bundle.write(file, name + "/" + file.relative_to(folder).as_posix())
    # Verify the materialized directory and every corresponding ZIP member.
    with zipfile.ZipFile(archive) as bundle:
        if len(bundle.infolist()) != len(entries) + 1:
            raise ValueError("Unexpected archive members")
        for entry in entries:
            stored = (folder / entry["path"]).read_bytes()
            packed = bundle.read(name + "/" + entry["path"])
            if stored != packed or hashlib.sha256(stored).hexdigest() != entry["sha256"]:
                raise ValueError("Export integrity mismatch")
    return {"status": "SOURCE_EXPORT_PASS", "folder": str(folder), "archive": str(archive),
            "files": len(entries), "bytes": archive.stat().st_size,
            "zip_sha256": hashlib.sha256(archive.read_bytes()).hexdigest()}

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", default="Mansur-source-alpha-20261002")
    args = parser.parse_args()
    try:
        print(json.dumps(export(Path(__file__).resolve().parents[1], args.name), ensure_ascii=False))
    except (ValueError, OSError) as error:
        raise SystemExit(str(error)) from error
