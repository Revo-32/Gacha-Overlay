#!/usr/bin/env python3
"""Validate LS Overlay 2.1 manual sources and, when present, final PDFs."""

from __future__ import annotations

import argparse
import re
from pathlib import Path

from pypdf import PdfReader

from build_21_guides import PAGEBREAK, SCREENSHOT_RE


PUBLIC_LINKS = (
    "https://status.revo32.cloud",
    "https://overlay.revo32.cloud/privacy",
    "https://overlay.revo32.cloud/terms",
    "mailto:revo.32.39.41@gmail.com",
)
FORBIDDEN = (
    "Client Secret 값을 입력",
    "Bot Token을 입력",
    "Guild ID",
    "E:\\Codex",
    "RemotePrimary",
    "M1",
    "M2",
    "M3",
    "[스크린샷 예정",
)


def validate_source(repo: Path, source: Path, expected_pages: range, edition: str = "2.1") -> list[str]:
    text = source.read_text(encoding="utf-8")
    errors = []
    page_count = len([chunk for chunk in text.split(PAGEBREAK) if chunk.strip()])
    if page_count not in expected_pages:
        errors.append(f"source page plan {page_count} outside {expected_pages.start}-{expected_pages.stop - 1}")
    for required in (f"LS Overlay {edition}", f"{edition}.0", "F9", "F10", "%LOCALAPPDATA%\\GachaOverlay"):
        if required not in text:
            errors.append(f"missing source term: {required}")
    for link in PUBLIC_LINKS:
        if link not in text:
            errors.append(f"missing public link: {link}")
    for forbidden in FORBIDDEN:
        if forbidden in text:
            errors.append(f"forbidden source term: {forbidden}")
    if re.search(r"(?<!\d)\d{16,20}(?!\d)", text):
        errors.append("Discord-like Snowflake found")
    screenshot_dir = repo / "docs/2.1/assets/screenshots"
    for name, _ in SCREENSHOT_RE.findall(text):
        path = screenshot_dir / name.strip()
        if path.exists() and path.suffix.lower() != ".png":
            errors.append(f"screenshot is not PNG: {path.name}")
    return errors


def validate_pdf(path: Path, expected_pages: range, edition: str = "2.1") -> list[str]:
    if not path.exists():
        return [f"PDF not generated: {path.name}"]
    reader = PdfReader(str(path))
    errors = []
    if len(reader.pages) not in expected_pages:
        errors.append(f"PDF pages {len(reader.pages)} outside {expected_pages.start}-{expected_pages.stop - 1}")
    text = "\n".join(page.extract_text() or "" for page in reader.pages)
    if any(len((page.extract_text() or "").strip()) < 35 for page in reader.pages):
        errors.append("blank or unsearchable PDF page")
    if "2.0.0" in text or re.search(r"\brc(?:\.\d+)?\b", text, re.IGNORECASE):
        errors.append("stale current-version branding")
    for required in (f"{edition}.0", "F9", "F10"):
        if required not in text:
            errors.append(f"missing PDF term: {required}")
    if not reader.outline:
        errors.append("missing PDF outline/bookmarks")
    metadata = "\n".join(str(value) for value in (reader.metadata or {}).values())
    if re.search(r"[A-Z]:\\", metadata):
        errors.append("private path in PDF metadata")
    return errors


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--edition", choices=("2.1", "2.2"), default="2.1")
    parser.add_argument("--pdf-dir", type=Path, default=Path("output/pdf/2.1"))
    parser.add_argument("--sources-only", action="store_true")
    args = parser.parse_args()
    edition = args.edition
    repo = args.repo.resolve()
    pdf_dir = (repo / args.pdf_dir).resolve()
    specs = (
        (repo / f"docs/{edition}/quick-start/LS-Overlay-{edition}-Quick-Start-ko.md", range(6, 9), f"LS-Overlay-{edition}-Quick-Start-ko.pdf"),
        (repo / f"docs/{edition}/user-guide/LS-Overlay-{edition}-User-Guide-ko.md", range(25, 36), f"LS-Overlay-{edition}-User-Guide-ko.pdf"),
    )
    errors = []
    for source, pages, pdf_name in specs:
        errors.extend(f"{source.name}: {error}" for error in validate_source(repo, source, pages, edition))
        if not args.sources_only:
            errors.extend(f"{pdf_name}: {error}" for error in validate_pdf(pdf_dir / pdf_name, pages, edition))
    if errors:
        print("\n".join(f"FAIL: {error}" for error in errors))
        raise SystemExit(1)
    print(f"LS Overlay {edition} documentation validation PASS")


if __name__ == "__main__":
    main()
