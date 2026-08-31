#!/usr/bin/env python3
"""Validate the release manual PDF, its source references, and sanitized images."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

from PIL import Image
from pypdf import PdfReader


EXPECTED_ICON_SHA256 = "944090cf580c9fd770193804f6675c1f60d45af19e94f6234438bd0912c6186f"
REQUIRED_TEXT = [
    "https://127.0.0.1",
    "rpc",
    "identify",
    "messages.read",
    "AUTHORIZE",
    "https://discord.com/api/v10/oauth2/token",
    "Basic authentication",
    "DPAPI",
    "Named Pipe",
    "English",
    "한국어",
    "日本語",
    "App Tester",
    "F9",
    "F10",
    "Paused",
    "Diagnostic ZIP",
    "[스티커]",
    "[메시지]",
]
FORBIDDEN_TEXT = [
    "Custom HEX Theme",
    "Chat Text Shadow",
    "Guild Selector",
    "KoPub",
    "Sold History UI",
]
SECRET_PATTERNS = {
    "client_secret_assignment": r"client[_ ]secret\s*[=:]\s*[A-Za-z0-9._-]{12,}",
    "access_token_assignment": r"access[_ ]token\s*[=:]\s*[A-Za-z0-9._-]{12,}",
    "refresh_token_assignment": r"refresh[_ ]token\s*[=:]\s*[A-Za-z0-9._-]{12,}",
    "bearer_token": r"Bearer\s+[A-Za-z0-9._-]{20,}",
    "mfa_token": r"mfa\.[A-Za-z0-9_-]{20,}",
    "discord_snowflake": r"(?<!\d)\d{16,20}(?!\d)",
    "email_address": r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def count_outline_items(items) -> int:
    count = 0
    for item in items:
        if isinstance(item, list):
            count += count_outline_items(item)
        else:
            count += 1
    return count


def inspect_pngs(paths: list[Path]) -> dict:
    non_png = []
    metadata = []
    invalid_size = []
    for path in paths:
        with Image.open(path) as image:
            if image.format != "PNG":
                non_png.append(path.name)
            if image.info:
                metadata.append({"file": path.name, "keys": sorted(image.info)})
            if image.width < 1 or image.height < 1:
                invalid_size.append(path.name)
    return {
        "count": len(paths),
        "non_png": non_png,
        "metadata": metadata,
        "invalid_size": invalid_size,
    }


def font_audit(reader: PdfReader) -> dict:
    fonts = {}
    for page in reader.pages:
        resources = page.get("/Resources", {})
        for ref in resources.get("/Font", {}).values():
            font = ref.get_object()
            base_name = str(font.get("/BaseFont", "unknown"))
            if base_name in fonts:
                continue
            descriptor = font.get("/FontDescriptor")
            if descriptor is not None:
                descriptor = descriptor.get_object()
            embedded = bool(
                descriptor
                and any(key in descriptor for key in ("/FontFile", "/FontFile2", "/FontFile3"))
            )
            fonts[base_name] = {
                "subtype": str(font.get("/Subtype", "unknown")),
                "to_unicode": "/ToUnicode" in font,
                "embedded": embedded,
                "standard_font": base_name == "/Helvetica",
            }
    return fonts


def validate(repo: Path, pdf: Path, source: Path) -> dict:
    reader = PdfReader(str(pdf))
    page_text = [(page.extract_text() or "").strip() for page in reader.pages]
    text = "\n".join(page_text)
    compact = re.sub(r"\s+", "", text)
    source_text = source.read_text(encoding="utf-8")
    combined = text + "\n" + source_text

    image_refs = re.findall(r"!\[[^\]]*\]\(([^)]+\.png)\)", source_text)
    missing_refs = [ref for ref in image_refs if not (source.parent / ref).exists()]
    base_dir = repo / "docs/manual/assets/1.0.0-rc.1"
    guide_dir = repo / "docs/manual/assets/1.0.0-rc.1-guide"
    base_pngs = sorted(base_dir.glob("*.png"))
    guide_pngs = sorted(guide_dir.glob("*.png"))

    user_icon = repo / "assets/input/GachaOverlay_AppIcon_Source.png"
    production_icon = repo / "src/GachaOverlay.App/Assets/Branding/GachaOverlay-AppIcon.png"
    raw_capture_dir = repo / "tmp/manual-capture"
    raw_capture_files = sorted(path.name for path in raw_capture_dir.glob("**/*") if path.is_file()) if raw_capture_dir.exists() else []

    required_missing = [term for term in REQUIRED_TEXT if re.sub(r"\s+", "", term) not in compact]
    forbidden_found = [term for term in FORBIDDEN_TEXT if term.casefold() in combined.casefold()]
    secret_hits = {
        name: sorted(set(match.group(0) for match in re.finditer(pattern, combined, re.IGNORECASE)))
        for name, pattern in SECRET_PATTERNS.items()
    }
    secret_hits = {name: hits for name, hits in secret_hits.items() if hits}
    developer_path_hits = sorted(set(re.findall(r"(?:[A-Z]:\\Users\\|E:\\Codex\\)[^\s<>'\"]*", combined, re.IGNORECASE)))

    metadata = {str(key): str(value) for key, value in (reader.metadata or {}).items()}
    metadata_blob = "\n".join(metadata.values())
    metadata_private_hits = [term for term in ("C:\\Users\\", "E:\\Codex\\", "Rev") if term.casefold() in metadata_blob.casefold()]

    widths = [float(page.mediabox.width) for page in reader.pages]
    heights = [float(page.mediabox.height) for page in reader.pages]
    font_info = font_audit(reader)
    custom_font_failures = [
        name for name, info in font_info.items()
        if not info["standard_font"] and (not info["embedded"] or not info["to_unicode"])
    ]

    result = {
        "pdf": str(pdf),
        "pdf_bytes": pdf.stat().st_size,
        "pages": len(reader.pages),
        "page_text_characters": [len(value) for value in page_text],
        "blank_or_unsearchable_pages": [index + 1 for index, value in enumerate(page_text) if len(value) < 40],
        "a4_page_size_pass": all(abs(width - 595.276) < 1.0 for width in widths) and all(abs(height - 841.89) < 1.0 for height in heights),
        "encrypted": reader.is_encrypted,
        "outline_items": count_outline_items(reader.outline),
        "metadata": metadata,
        "metadata_private_hits": metadata_private_hits,
        "required_missing": required_missing,
        "forbidden_found": forbidden_found,
        "secret_or_personal_hits": secret_hits,
        "developer_path_hits": developer_path_hits,
        "non_breaking_hyphen_count": combined.count("\u2011"),
        "source_screenshot_refs": len(image_refs),
        "unique_source_screenshot_refs": len(set(image_refs)),
        "missing_source_screenshot_refs": missing_refs,
        "base_screenshots": inspect_pngs(base_pngs),
        "annotated_screenshots": inspect_pngs(guide_pngs),
        "raw_capture_files": raw_capture_files,
        "user_icon_sha256": sha256(user_icon),
        "production_icon_sha256": sha256(production_icon),
        "icon_exact_match": sha256(user_icon) == sha256(production_icon) == EXPECTED_ICON_SHA256,
        "fonts": font_info,
        "custom_font_failures": custom_font_failures,
    }
    result["pass"] = all([
        result["pages"] == 40,
        result["pdf_bytes"] > 0,
        not result["blank_or_unsearchable_pages"],
        result["a4_page_size_pass"],
        not result["encrypted"],
        result["outline_items"] >= 10,
        not result["metadata_private_hits"],
        not result["required_missing"],
        not result["forbidden_found"],
        not result["secret_or_personal_hits"],
        not result["developer_path_hits"],
        result["non_breaking_hyphen_count"] == 0,
        result["source_screenshot_refs"] == 25,
        result["unique_source_screenshot_refs"] == 25,
        not result["missing_source_screenshot_refs"],
        result["base_screenshots"]["count"] == 25,
        not result["base_screenshots"]["non_png"],
        not result["base_screenshots"]["metadata"],
        result["annotated_screenshots"]["count"] == 14,
        not result["annotated_screenshots"]["non_png"],
        not result["annotated_screenshots"]["metadata"],
        not result["raw_capture_files"],
        result["icon_exact_match"],
        not result["custom_font_failures"],
    ])
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--pdf", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--json-output", type=Path)
    args = parser.parse_args()
    result = validate(args.repo.resolve(), args.pdf.resolve(), args.source.resolve())
    payload = json.dumps(result, ensure_ascii=False, indent=2)
    if args.json_output:
        args.json_output.parent.mkdir(parents=True, exist_ok=True)
        args.json_output.write_text(payload + "\n", encoding="utf-8")
    print(payload)
    raise SystemExit(0 if result["pass"] else 1)


if __name__ == "__main__":
    main()
