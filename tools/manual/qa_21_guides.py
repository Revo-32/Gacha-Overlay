"""Render every page with Poppler and check the two manuals structurally."""
import argparse
import json
import re
import subprocess
from pathlib import Path

import pdfplumber
from PIL import Image, ImageDraw
from pypdf import PdfReader

from build_21_guides import PAGEBREAK


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--poppler-bin", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    output = repo / "tmp/pdfs/2.1/qa"
    output.mkdir(parents=True, exist_ok=True)
    report = []
    for kind, name, pages in (("quick-start", "Quick-Start", 8), ("user-guide", "User-Guide", 30)):
        pdf = repo / f"output/pdf/2.1/LS-Overlay-2.1-{name}-ko.pdf"
        source = repo / f"docs/2.1/{kind}/LS-Overlay-2.1-{name}-ko.md"
        reader = PdfReader(pdf)
        assert len(reader.pages) == pages, (name, len(reader.pages))
        chunks = [chunk.strip() for chunk in source.read_text(encoding="utf-8").split(PAGEBREAK)]
        titles = [re.search(r"^# (.+)$", chunk, re.M).group(1) for chunk in chunks]
        for index, page in enumerate(reader.pages):
            text = page.extract_text() or ""
            assert titles[index] in text, (name, index + 1, "page boundary mismatch")
            assert not re.search(r"SCREENSHOT REQUIRED|C:\\Users|E:\\Codex|\bM[123]\b|\d{16,20}", text)
        if kind == "user-guide":
            toc = reader.pages[1].extract_text()
            for line in toc.splitlines():
                if match := re.search(r"^(\d+)\. .+ · (\d+)$", line):
                    assert titles[int(match[2]) - 1].startswith(match[1] + "."), line
        with pdfplumber.open(pdf) as document:
            for index, page in enumerate(document.pages):
                for char in page.chars:
                    assert 45 <= char["x0"] <= char["x1"] <= page.width - 42, (name, index + 1, "horizontal overflow", char["text"])
                    assert 20 <= char["top"] <= char["bottom"] <= page.height - 15, (name, index + 1, "vertical overflow")
        subprocess.run([str(args.poppler_bin / "pdftoppm.exe"), "-r", "110", "-png", str(pdf), str(output / kind)], check=True)
        images = sorted(output.glob(f"{kind}-*.png"))
        assert len(images) == pages
        for start in range(0, len(images), 4):
            with Image.open(images[start]) as first:
                width, height = first.size
            sheet = Image.new("RGB", (width * 2, (height + 24) * 2), "#d5d5d5")
            draw = ImageDraw.Draw(sheet)
            for local, path in enumerate(images[start:start + 4]):
                x, y = (local % 2) * width, (local // 2) * (height + 24)
                draw.text((x + 8, y + 5), f"{name} / {start + local + 1}", fill="black")
                with Image.open(path) as page:
                    sheet.paste(page, (x, y + 24))
            sheet.save(output / f"sheet-{kind}-{start + 1:02}.png")
        fonts = []
        for ref in reader.pages[0]["/Resources"]["/Font"].values():
            font = ref.get_object()
            if "WantedSans" in str(font.get("/BaseFont", "")):
                descriptor = font["/FontDescriptor"]
                assert "/FontFile2" in descriptor and "/ToUnicode" in font
                fonts.append(str(font["/BaseFont"]))
        assert len(fonts) >= 2, "Wanted Sans regular/bold not embedded"
        report.append({"file": pdf.name, "pages": pages, "pageBoundaries": "PASS", "textBounds": "PASS",
                       "toc": "PASS" if kind == "user-guide" else "not required", "fonts": fonts,
                       "renderedPages": len(images), "visualReview": "Requires human/assistant inspection of rendered pages"})
    (output / "structural-qa.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
