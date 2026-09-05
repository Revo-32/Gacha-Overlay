#!/usr/bin/env python3
"""Build the two LS Overlay 2.1 Korean manuals from maintainable Markdown."""

from __future__ import annotations

import argparse
import html
import re
from dataclasses import dataclass
from pathlib import Path

from PIL import Image as PilImage
from fontTools.ttLib import TTFont as FontToolsFont
from fontTools.varLib.instancer import instantiateVariableFont
from pypdf import PdfReader
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (
    Flowable,
    Image,
    PageBreak,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)


SCREENSHOT_RE = re.compile(
    r"<!-- SCREENSHOT REQUIRED:\s*([^|]+?)\s*\|\s*(.+?)\s*-->", re.DOTALL
)
PAGEBREAK = "<!-- PAGEBREAK -->"

ACCENT = colors.HexColor("#0BA95B")
ACCENT_DARK = colors.HexColor("#087A45")
LIME = colors.HexColor("#48D529")
INK = colors.HexColor("#17211E")
MUTED = colors.HexColor("#52615B")
SURFACE = colors.HexColor("#F3F7F4")
LINE = colors.HexColor("#D8E3DC")
WHITE = colors.white


@dataclass(frozen=True)
class GuideSpec:
    source: Path
    output_name: str
    title: str
    short_title: str
    min_pages: int
    max_pages: int


class ScreenshotCard(Flowable):
    def __init__(self, path: Path, caption: str, max_width: float, max_height: float):
        with PilImage.open(path) as image:
            width, height = image.size
        scale = min(max_width / width, max_height / height)
        self.image_width = width * scale
        self.image_height = height * scale
        self.caption = Paragraph(html.escape(caption), ParagraphStyle(
            "ScreenshotCaption", fontName="WantedSans", fontSize=8, leading=11,
            textColor=MUTED, alignment=TA_CENTER, wordWrap="CJK"))
        _, self.caption_height = self.caption.wrap(max_width, 100)
        self.path = path
        self.width = max_width
        self.height = self.image_height + self.caption_height + 27

    def draw(self):
        canvas = self.canv
        x = (self.width - self.image_width) / 2
        image_bottom = self.caption_height + 15
        canvas.saveState()
        canvas.setFillColor(WHITE)
        canvas.setStrokeColor(LINE)
        canvas.setLineWidth(0.8)
        canvas.roundRect(x - 6, image_bottom - 6, self.image_width + 12, self.image_height + 12, 8, fill=1, stroke=1)
        canvas.drawImage(
            str(self.path), x, image_bottom, width=self.image_width, height=self.image_height,
            preserveAspectRatio=True, mask="auto"
        )
        if self.path.name == "01-main-hud.png":
            for number, fraction in ((1, 0.963), (2, 0.65), (3, 0.145)):
                cx, cy = x - 10, image_bottom + self.image_height * fraction
                canvas.setFillColor(ACCENT_DARK)
                canvas.circle(cx, cy, 8, fill=1, stroke=0)
                canvas.setFillColor(WHITE)
                canvas.setFont("WantedSansBold", 9)
                canvas.drawCentredString(cx, cy - 3, str(number))
        canvas.setFont("WantedSans", 8)
        canvas.setFillColor(MUTED)
        self.caption.drawOn(canvas, 0, 0)
        canvas.restoreState()


class GuideDocTemplate(SimpleDocTemplate):
    def afterFlowable(self, flowable):
        if not isinstance(flowable, Paragraph):
            return
        level = getattr(flowable, "outline_level", None)
        if level is None:
            return
        key = f"section-{self.page}-{id(flowable)}"
        title = flowable.getPlainText()
        self.canv.bookmarkPage(key)
        self.canv.addOutlineEntry(title, key, level=level, closed=level > 0)


def register_fonts(repo: Path, text: str) -> None:
    font_path = repo / "src/GachaOverlay.App/Assets/Fonts/WantedSansVariable.ttf"
    scratch = repo / "tmp/pdfs/2.1/fonts"
    scratch.mkdir(parents=True, exist_ok=True)
    for name, weight in (("WantedSans", 400), ("WantedSansBold", 700)):
        instance_path = scratch / f"{name}.ttf"
        if not instance_path.exists():
            with FontToolsFont(font_path) as variable:
                instance = instantiateVariableFont(variable, {"wght": weight}, inplace=False)
                instance.save(instance_path)
        pdfmetrics.registerFont(TTFont(name, str(instance_path)))
    font = pdfmetrics.getFont("WantedSans")
    pdfmetrics.registerFontFamily(
        "WantedSans", normal="WantedSans", bold="WantedSansBold",
        italic="WantedSans", boldItalic="WantedSansBold"
    )
    missing = {character for character in text if not character.isspace() and ord(character) not in font.face.charToGlyph}
    if missing:
        raise ValueError(f"Wanted Sans missing glyphs: {sorted(missing)!r}")


def styles():
    sample = getSampleStyleSheet()
    body = ParagraphStyle(
        "Body", parent=sample["BodyText"], fontName="WantedSans", fontSize=10.2,
        leading=15.8, textColor=INK, spaceAfter=7, wordWrap="CJK"
    )
    return {
        "cover": ParagraphStyle(
            "Cover", parent=body, fontSize=34, leading=42, textColor=ACCENT_DARK,
            alignment=TA_LEFT, spaceAfter=8
        ),
        "cover_sub": ParagraphStyle(
            "CoverSub", parent=body, fontSize=18, leading=25, textColor=INK,
            spaceAfter=18
        ),
        "h1": ParagraphStyle(
            "H1", parent=body, fontName="WantedSansBold", fontSize=23, leading=29, textColor=ACCENT_DARK,
            spaceAfter=14, keepWithNext=True
        ),
        "h2": ParagraphStyle(
            "H2", parent=body, fontName="WantedSansBold", fontSize=13.5, leading=19, textColor=INK,
            spaceBefore=8, spaceAfter=6, keepWithNext=True
        ),
        "body": body,
        "bullet": ParagraphStyle(
            "Bullet", parent=body, leftIndent=13, firstLineIndent=-8, bulletIndent=4,
            spaceAfter=5
        ),
        "number": ParagraphStyle(
            "Number", parent=body, leftIndent=16, firstLineIndent=-11, spaceAfter=5
        ),
        "caption": ParagraphStyle(
            "Caption", parent=body, fontSize=8, leading=11, textColor=MUTED,
            alignment=TA_CENTER
        ),
        "code": ParagraphStyle(
            "Code", parent=body, fontName="WantedSans", fontSize=9, leading=13,
            leftIndent=12, borderColor=LINE, borderWidth=0.7, borderPadding=8,
            backColor=SURFACE, spaceBefore=14, spaceAfter=18
        ),
        "toc": ParagraphStyle(
            "Toc", parent=body, fontSize=11.2, leading=20, leftIndent=8, spaceAfter=2
        ),
    }


def inline_markup(value: str) -> str:
    escaped = html.escape(value.strip())
    escaped = re.sub(
        r"\[([^]]+)\]\((https://[^)]+|mailto:[^)]+)\)",
        r'<link href="\2" color="#087A45"><u>\1</u></link>', escaped
    )
    escaped = re.sub(r"`([^`]+)`", r'<font color="#087A45">\1</font>', escaped)
    escaped = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", escaped)
    return escaped


def screenshot_slots(source_text: str) -> list[tuple[str, str]]:
    return [(name.strip(), re.sub(r"\s+", " ", caption.strip())) for name, caption in SCREENSHOT_RE.findall(source_text)]


def assert_screenshots(repo: Path, source_text: str) -> None:
    screenshot_dir = repo / "docs/2.1/assets/screenshots"
    missing = sorted({name for name, _ in screenshot_slots(source_text) if not (screenshot_dir / name).is_file()})
    if missing:
        joined = "\n  - ".join(missing)
        raise FileNotFoundError(
            "Current LS Overlay 2.1 screenshots are required before PDF generation:\n  - " + joined
        )


def parse_table(lines: list[str], style_set: dict) -> Table:
    rows = []
    for line in lines:
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells):
            continue
        rows.append([Paragraph(inline_markup(cell), style_set["body"]) for cell in cells])
    table = Table(rows, colWidths=[166 * mm / len(rows[0])] * len(rows[0]), hAlign="LEFT", repeatRows=1)
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), SURFACE),
        ("TEXTCOLOR", (0, 0), (-1, 0), ACCENT_DARK),
        ("FONTNAME", (0, 0), (-1, -1), "WantedSans"),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("GRID", (0, 0), (-1, -1), 0.5, LINE),
        ("LEFTPADDING", (0, 0), (-1, -1), 7),
        ("RIGHTPADDING", (0, 0), (-1, -1), 7),
        ("TOPPADDING", (0, 0), (-1, -1), 5),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
    ]))
    return table


def page_flowables(repo: Path, chunk: str, page_index: int, style_set: dict) -> list[Flowable]:
    screenshot_dir = repo / "docs/2.1/assets/screenshots"
    flow: list[Flowable] = []
    lines = chunk.strip().splitlines()
    paragraph: list[str] = []
    in_code = False
    code_lines: list[str] = []

    def flush_paragraph():
        if paragraph:
            flow.append(Paragraph(inline_markup(" ".join(paragraph)), style_set["body"]))
            paragraph.clear()

    i = 0
    while i < len(lines):
        line = lines[i].rstrip()
        if line.strip().startswith("```"):
            flush_paragraph()
            if in_code:
                flow.append(Paragraph("<br/>".join(html.escape(value) for value in code_lines), style_set["code"]))
                code_lines.clear()
            in_code = not in_code
            i += 1
            continue
        if in_code:
            code_lines.append(line)
            i += 1
            continue
        slot = SCREENSHOT_RE.fullmatch(line.strip())
        if slot:
            flush_paragraph()
            filename, caption = slot.group(1).strip(), re.sub(r"\s+", " ", slot.group(2).strip())
            flow.extend([
                Spacer(1, 4),
                ScreenshotCard(screenshot_dir / filename, caption, 166 * mm,
                               {"01-main-hud.png": 105, "11-settings-themes.png": 72}.get(filename, 100) * mm),
            ])
            i += 1
            continue
        if line.startswith("| ") and i + 1 < len(lines) and lines[i + 1].startswith("|"):
            flush_paragraph()
            table_lines = []
            while i < len(lines) and lines[i].startswith("|"):
                table_lines.append(lines[i])
                i += 1
            flow.extend([parse_table(table_lines, style_set), Spacer(1, 7)])
            continue
        if not line.strip():
            flush_paragraph()
            i += 1
            continue
        if line.startswith("# "):
            flush_paragraph()
            key = "cover" if page_index == 0 else "h1"
            item = Paragraph(inline_markup(line[2:]), style_set[key])
            item.outline_level = 0
            flow.append(item)
        elif line.startswith("## "):
            flush_paragraph()
            item = Paragraph(inline_markup(line[3:]), style_set["cover_sub"] if page_index == 0 else style_set["h2"])
            if page_index != 0:
                item.outline_level = 1
            flow.append(item)
        elif line.startswith("- "):
            flush_paragraph()
            flow.append(Paragraph("• " + inline_markup(line[2:]), style_set["bullet"]))
        elif re.match(r"^\d+\.\s", line):
            flush_paragraph()
            flow.append(Paragraph(inline_markup(line), style_set["toc"] if page_index == 1 else style_set["number"]))
        else:
            paragraph.append(line)
        i += 1
    flush_paragraph()
    return flow


def furniture(canvas, doc, short_title: str):
    canvas.saveState()
    width, height = A4
    canvas.setFillColor(ACCENT)
    canvas.rect(0, height - 7, width, 7, fill=1, stroke=0)
    canvas.setStrokeColor(LINE)
    canvas.line(20 * mm, 16 * mm, width - 20 * mm, 16 * mm)
    canvas.setFont("WantedSans", 7.8)
    canvas.setFillColor(MUTED)
    canvas.drawString(20 * mm, 10.5 * mm, f"LS Overlay 2.1 · {short_title}")
    canvas.drawRightString(width - 20 * mm, 10.5 * mm, str(doc.page))
    canvas.restoreState()


def build_one(repo: Path, spec: GuideSpec, output_dir: Path) -> Path:
    text = spec.source.read_text(encoding="utf-8")
    assert_screenshots(repo, text)
    register_fonts(repo, text)
    style_set = styles()
    chunks = [chunk.strip() for chunk in text.split(PAGEBREAK) if chunk.strip()]
    story: list[Flowable] = []
    logo = repo / "assets/branding/LS_Overlay_logo.png"
    for index, chunk in enumerate(chunks):
        if index:
            story.append(PageBreak())
        if index == 0:
            with PilImage.open(logo) as original:
                logo_ratio = original.height / original.width
            image = Image(str(logo), width=55 * mm, height=55 * mm * logo_ratio)
            image.hAlign = "LEFT"
            story.extend([image, Spacer(1, 18 * mm)])
        story.extend(page_flowables(repo, chunk, index, style_set))

    output_dir.mkdir(parents=True, exist_ok=True)
    output = output_dir / spec.output_name
    doc = GuideDocTemplate(
        str(output), pagesize=A4, leftMargin=20 * mm, rightMargin=20 * mm,
        topMargin=20 * mm, bottomMargin=21 * mm, title=spec.title,
        author="LS Overlay", subject="LS Overlay 2.1 Korean documentation",
        creator="LS Overlay documentation pipeline"
    )
    doc.build(
        story,
        onFirstPage=lambda canvas, current: furniture(canvas, current, spec.short_title),
        onLaterPages=lambda canvas, current: furniture(canvas, current, spec.short_title),
    )
    reader = PdfReader(str(output))
    if not spec.min_pages <= len(reader.pages) <= spec.max_pages:
        raise ValueError(f"Unexpected page count for {spec.output_name}: {len(reader.pages)}")
    if any(len((page.extract_text() or "").strip()) < 35 for page in reader.pages):
        raise ValueError(f"Blank or unsearchable page detected in {spec.output_name}")
    return output


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=("quick-start", "user-guide", "all"), default="all")
    parser.add_argument("--output-dir", type=Path, default=Path("output/pdf/2.1"))
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    specs = {
        "quick-start": GuideSpec(
            repo / "docs/2.1/quick-start/LS-Overlay-2.1-Quick-Start-ko.md",
            "LS-Overlay-2.1-Quick-Start-ko.pdf", "LS Overlay 2.1 빠른 시작 가이드",
            "빠른 시작 가이드", 6, 8
        ),
        "user-guide": GuideSpec(
            repo / "docs/2.1/user-guide/LS-Overlay-2.1-User-Guide-ko.md",
            "LS-Overlay-2.1-User-Guide-ko.pdf", "LS Overlay 2.1 상세 사용자 설명서",
            "상세 사용자 설명서", 25, 35
        ),
    }
    selected = list(specs.values()) if args.kind == "all" else [specs[args.kind]]
    screenshot_dir = repo / "docs/2.1/assets/screenshots"
    missing = sorted({
        name.strip()
        for spec in selected
        for name, _ in screenshot_slots(spec.source.read_text(encoding="utf-8"))
        if not (screenshot_dir / name.strip()).is_file()
    })
    if missing:
        print("PARTIAL - current LS Overlay 2.1 screenshots required:")
        for name in missing:
            print(f"  - {name}")
        raise SystemExit(2)
    for spec in selected:
        output = build_one(repo, spec, (repo / args.output_dir).resolve())
        print(output)


if __name__ == "__main__":
    main()
