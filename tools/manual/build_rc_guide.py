"""Build the text-first Korean 2.0 guide from editable Markdown. No screenshots."""
import argparse
import html
import re
from pathlib import Path

from pypdf import PdfReader
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import Image, PageBreak, Paragraph, SimpleDocTemplate, Spacer


def build(repo: Path, output: Path):
    source = repo / "docs/user/LS-Overlay-2.0-RC-User-Guide-ko.md"
    text = source.read_text(encoding="utf-8")
    fonts = repo / "src/GachaOverlay.App/Assets/Fonts"
    for name, file in (("Body", "PretendardVariable.ttf"), ("Head", "WantedSansVariable.ttf")):
        font = TTFont(name, str(fonts / file))
        pdfmetrics.registerFont(font)
        missing = {c for c in text if not c.isspace() and ord(c) not in font.face.charToGlyph}
        if missing:
            raise ValueError(f"Missing glyphs in {name}: {sorted(missing)}")
    pdfmetrics.registerFontFamily("Body", normal="Body", bold="Head", italic="Body", boldItalic="Head")
    green = colors.HexColor("#14764D")
    ink = colors.HexColor("#192B27")
    body = ParagraphStyle("Body", fontName="Body", fontSize=11.5, leading=18.5,
                          textColor=ink, spaceAfter=10, wordWrap="CJK")
    heading = ParagraphStyle("Heading", parent=body, fontName="Head", fontSize=23,
                             leading=30, textColor=green, spaceAfter=20, keepWithNext=True)
    subheading = ParagraphStyle("Subheading", parent=body, fontName="Head", fontSize=14,
                                leading=21, spaceBefore=9, spaceAfter=7, keepWithNext=True)
    item = ParagraphStyle("Item", parent=body, leftIndent=8, spaceAfter=7)

    def markup(value):
        value = html.escape(value)
        value = re.sub(r"\[([^]]+)\]\((https://[^)]+|mailto:[^)]+)\)",
                       r'<link href="\2" color="#14764D"><u>\1</u></link>', value)
        return re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", value)

    def furniture(canvas, doc):
        canvas.saveState()
        canvas.setFillColor(green)
        canvas.rect(0, A4[1] - 9, A4[0], 9, fill=1, stroke=0)
        canvas.setFont("Body", 8.5)
        canvas.setFillColor(colors.HexColor("#566960"))
        canvas.drawString(44, A4[1] - 34, "LS Overlay | 사용자 안내서")
        canvas.drawRightString(A4[0] - 44, 25, f"2.0.0  |  {doc.page}")
        canvas.restoreState()

    story = []
    for page_index, chunk in enumerate(text.split("\n---\n")):
        if page_index:
            story.append(PageBreak())
        else:
            logo = Image(str(repo / "assets/branding/LS_Overlay_logo.png"), width=106, height=106)
            logo.hAlign = "LEFT"
            story.extend([logo, Spacer(1, 19)])
        for block in re.split(r"\n\s*\n", chunk.strip()):
            for line in block.splitlines() if block.startswith(("#", "- ", "1. ")) else [block.replace("\n", " ")]:
                if line.startswith("# "):
                    story.append(Paragraph(markup(line[2:]), heading))
                elif line.startswith("## "):
                    story.append(Paragraph(markup(line[3:]), subheading))
                else:
                    story.append(Paragraph(markup(line), item if re.match(r"(- |\d+\. )", line) else body))
    output.parent.mkdir(parents=True, exist_ok=True)
    doc = SimpleDocTemplate(str(output), pagesize=A4, leftMargin=44, rightMargin=44,
                            topMargin=62, bottomMargin=48, title="LS Overlay 2.0.0 사용자 안내서",
                            author="LS Overlay", subject="한국어 사용자 안내서")
    doc.build(story, onFirstPage=furniture, onLaterPages=furniture)
    reader = PdfReader(output)
    assert len(reader.pages) == 9, f"Unexpected pagination: {len(reader.pages)}"
    extracted = "\n".join(page.extract_text() for page in reader.pages)
    for forbidden in ("E:\\", "C:\\Users", "[스크린샷 예정", "Client Secret", "Guild ID", "Railway", "RemotePrimary"):
        assert forbidden not in extracted, forbidden
    assert not re.search(r"(?<!\d)\d{17,20}(?!\d)", extracted)
    uris = {a.get_object().get("/A", {}).get("/URI") for p in reader.pages for a in p.get("/Annots", [])}
    for uri in ("https://overlay.revo32.cloud/privacy", "https://overlay.revo32.cloud/terms",
                "https://status.revo32.cloud", "mailto:revo.32.39.41@gmail.com"):
        assert uri in uris, uri
    print(f"PDF PASS: {len(reader.pages)} pages; embedded Korean fonts; 4 public links; no internal IDs/paths")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    build(Path(__file__).resolve().parents[2], args.output.resolve())
