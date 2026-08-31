#!/usr/bin/env python3
"""Build the workflow-first Gacha Overlay Korean release manual."""

from __future__ import annotations

import argparse
import html
import json
import os
import re
from pathlib import Path

from PIL import Image as PILImage
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.lib.utils import simpleSplit
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (
    BaseDocTemplate,
    Flowable,
    Frame,
    HRFlowable,
    Image,
    KeepTogether,
    PageBreak,
    PageTemplate,
    Paragraph,
    Spacer,
    Table,
    TableStyle,
)


BODY_FONT = "ManualBody"
HEAD_FONT = "ManualHead"
CJK_FONT = "ManualCJK"


def hex_color(value: str):
    return colors.HexColor(value)


def load_theme(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def register_fonts(repo: Path) -> None:
    body = repo / "src/GachaOverlay.App/Assets/Fonts/PretendardVariable.ttf"
    head = repo / "src/GachaOverlay.App/Assets/Fonts/WantedSansVariable.ttf"
    windows = Path(os.environ.get("WINDIR", r"C:\Windows"))
    cjk = windows / "Fonts/malgun.ttf"
    for path in (body, head, cjk):
        if not path.exists():
            raise FileNotFoundError(path)
    pdfmetrics.registerFont(TTFont(BODY_FONT, str(body)))
    pdfmetrics.registerFont(TTFont(HEAD_FONT, str(head)))
    pdfmetrics.registerFont(TTFont(CJK_FONT, str(cjk)))
    pdfmetrics.registerFontFamily(
        BODY_FONT,
        normal=BODY_FONT,
        bold=HEAD_FONT,
        italic=BODY_FONT,
        boldItalic=HEAD_FONT,
    )


class ManualDocTemplate(BaseDocTemplate):
    def __init__(self, filename: str, *, icon_path: Path, palette: dict, **kwargs):
        super().__init__(filename, **kwargs)
        self.icon_path = icon_path
        self.palette = palette
        frame = Frame(
            self.leftMargin,
            self.bottomMargin,
            self.width,
            self.height,
            id="manual",
            leftPadding=0,
            rightPadding=0,
            topPadding=0,
            bottomPadding=0,
        )
        self.addPageTemplates(PageTemplate(id="manual", frames=[frame], onPage=self._draw_page))

    def _draw_page(self, canvas, doc):
        width, height = A4
        canvas.saveState()
        if doc.page == 1:
            canvas.setFillColor(self.palette["panel"])
            canvas.rect(0, 0, width, height, stroke=0, fill=1)
            canvas.setFillColor(colors.white)
            canvas.roundRect(12 * mm, 12 * mm, width - 24 * mm, height - 24 * mm, 5 * mm, stroke=0, fill=1)
        else:
            canvas.drawImage(
                str(self.icon_path),
                self.leftMargin,
                height - 12.2 * mm,
                width=5.5 * mm,
                height=5.5 * mm,
                mask="auto",
                preserveAspectRatio=True,
            )
            canvas.setFillColor(self.palette["muted"])
            canvas.setFont(BODY_FONT, 7.5)
            canvas.drawString(self.leftMargin + 7 * mm, height - 9.2 * mm, "Gacha Overlay · 사용자 설명서")
            canvas.drawRightString(width - self.rightMargin, height - 9.2 * mm, "1.0.0-rc.1")
            canvas.setStrokeColor(self.palette["line"])
            canvas.setLineWidth(0.45)
            canvas.line(self.leftMargin, height - 14 * mm, width - self.rightMargin, height - 14 * mm)

            canvas.setFillColor(self.palette["muted"])
            canvas.setFont(BODY_FONT, 7.5)
            canvas.drawString(self.leftMargin, 9.5 * mm, "Controlled Test Release · Manual 1.1")
            canvas.drawRightString(width - self.rightMargin, 9.5 * mm, f"{doc.page}")
        canvas.restoreState()

    def afterFlowable(self, flowable):
        key = getattr(flowable, "bookmark_key", None)
        title = getattr(flowable, "bookmark_title", None)
        level = getattr(flowable, "outline_level", None)
        if key and title is not None and level is not None:
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(title, key, level=level, closed=level == 0)


class AccentRule(Flowable):
    def __init__(self, color):
        super().__init__()
        self.color = color
        self.height = 1.4 * mm

    def wrap(self, avail_width, avail_height):
        self.width = avail_width
        return avail_width, self.height

    def draw(self):
        self.canv.setFillColor(self.color)
        self.canv.roundRect(0, 0, self.width, self.height, self.height / 2, stroke=0, fill=1)


class CoverHero(Flowable):
    def __init__(self, icon_path: Path, palette: dict):
        super().__init__()
        self.icon_path = icon_path
        self.palette = palette
        self.height = 91 * mm

    def wrap(self, avail_width, avail_height):
        self.width = avail_width
        return avail_width, self.height

    def draw(self):
        c = self.canv
        c.saveState()
        c.setFillColor(self.palette["brand_navy"])
        c.roundRect(0, 0, self.width, self.height, 6 * mm, stroke=0, fill=1)
        c.setFillColor(self.palette["brand_blue"])
        c.circle(self.width - 12 * mm, self.height - 10 * mm, 31 * mm, stroke=0, fill=1)
        c.setFillColor(self.palette["brand_cyan"])
        c.circle(self.width - 6 * mm, 4 * mm, 19 * mm, stroke=0, fill=1)
        c.setFillColor(self.palette["brand_yellow"])
        c.roundRect(12 * mm, self.height - 17 * mm, 38 * mm, 7 * mm, 3.5 * mm, stroke=0, fill=1)
        c.setFillColor(self.palette["brand_navy"])
        c.setFont(HEAD_FONT, 8.2)
        c.drawCentredString(31 * mm, self.height - 14.7 * mm, "OFFICIAL USER GUIDE")

        c.setFillColor(colors.white)
        c.setFont(HEAD_FONT, 34)
        c.drawString(12 * mm, self.height - 39 * mm, "Gacha Overlay")
        c.setFont(HEAD_FONT, 20)
        c.drawString(12 * mm, self.height - 53 * mm, "사용자 설명서")
        c.setFillColor(colors.HexColor("#CDE8FF"))
        c.setFont(BODY_FONT, 9)
        c.drawString(12 * mm, 14 * mm, "VERSION 1.0.0-rc.1   ·   CONTROLLED TEST RELEASE")

        c.drawImage(
            str(self.icon_path),
            self.width - 78 * mm,
            8 * mm,
            width=69 * mm,
            height=69 * mm,
            mask="auto",
            preserveAspectRatio=True,
        )
        c.restoreState()


class PartBanner(Flowable):
    def __init__(self, text: str, palette: dict):
        super().__init__()
        self.text = text
        self.palette = palette
        self.height = 35 * mm

    def wrap(self, avail_width, avail_height):
        self.width = avail_width
        return avail_width, self.height

    def draw(self):
        c = self.canv
        c.saveState()
        c.setFillColor(self.palette["brand_navy"])
        c.roundRect(0, 0, self.width, self.height, 4 * mm, stroke=0, fill=1)
        c.setFillColor(self.palette["brand_cyan"])
        c.roundRect(0, 0, 4 * mm, self.height, 2 * mm, stroke=0, fill=1)
        if "·" in self.text:
            part, title = [piece.strip() for piece in self.text.split("·", 1)]
        else:
            part, title = "PART", self.text
        c.setFillColor(self.palette["brand_yellow"])
        c.setFont(HEAD_FONT, 9)
        c.drawString(10 * mm, self.height - 11 * mm, part)
        c.setFillColor(colors.white)
        c.setFont(HEAD_FONT, 22)
        lines = simpleSplit(title, HEAD_FONT, 22, self.width - 24 * mm)
        y = self.height - 23 * mm
        for line in lines[:2]:
            c.drawString(10 * mm, y, line)
            y -= 9 * mm
        c.restoreState()


class WorkflowFlowable(Flowable):
    def __init__(self, items: list[str], palette: dict):
        super().__init__()
        self.items = items
        self.palette = palette
        self.height = 57 * mm

    def wrap(self, avail_width, avail_height):
        self.width = avail_width
        return avail_width, self.height

    def draw(self):
        c = self.canv
        gap = 4 * mm
        card_w = (self.width - gap * 2) / 3
        card_h = (self.height - gap) / 2
        for index, item in enumerate(self.items[:6]):
            col = index % 3
            row = 1 - index // 3
            x = col * (card_w + gap)
            y = row * (card_h + gap)
            c.setFillColor(self.palette["panel"])
            c.setStrokeColor(self.palette["line"])
            c.setLineWidth(0.6)
            c.roundRect(x, y, card_w, card_h, 3 * mm, stroke=1, fill=1)
            c.setFillColor(self.palette["brand_blue"])
            center_x = x + 8 * mm
            center_y = y + card_h - 8 * mm
            c.circle(center_x, center_y, 4.6 * mm, stroke=0, fill=1)
            c.setFillColor(colors.white)
            c.setFont(HEAD_FONT, 8.5)
            ascent = pdfmetrics.getAscent(HEAD_FONT, 8.5)
            descent = pdfmetrics.getDescent(HEAD_FONT, 8.5)
            baseline = center_y - (ascent + descent) / 2
            c.drawCentredString(center_x, baseline, str(index + 1))
            c.setFillColor(self.palette["ink"])
            c.setFont(HEAD_FONT, 8.8)
            lines = simpleSplit(item, HEAD_FONT, 8.8, card_w - 14 * mm)
            text_y = y + card_h - 8 * mm
            for line in lines[:3]:
                c.drawString(x + 14 * mm, text_y, line)
                text_y -= 5 * mm
            if index < len(self.items) - 1 and col < 2:
                c.setStrokeColor(self.palette["brand_cyan"])
                c.setLineWidth(1.2)
                c.line(x + card_w + 0.8 * mm, y + card_h / 2, x + card_w + gap - 0.8 * mm, y + card_h / 2)


def make_styles(theme: dict, palette: dict) -> dict[str, ParagraphStyle]:
    base = getSampleStyleSheet()
    typo = theme["typography"]
    return {
        "cover_desc": ParagraphStyle(
            "CoverDesc", parent=base["BodyText"], fontName=BODY_FONT, fontSize=12,
            leading=19, textColor=palette["ink"], spaceAfter=4 * mm, wordWrap="CJK"
        ),
        "cover_meta": ParagraphStyle(
            "CoverMeta", parent=base["BodyText"], fontName=BODY_FONT, fontSize=9,
            leading=14, textColor=palette["muted"], wordWrap="CJK"
        ),
        "h1": ParagraphStyle(
            "H1", parent=base["Heading1"], fontName=HEAD_FONT, fontSize=typo["h1_pt"],
            leading=29, textColor=palette["brand_navy"], spaceBefore=1 * mm,
            spaceAfter=3 * mm, keepWithNext=True, wordWrap="CJK"
        ),
        "h2": ParagraphStyle(
            "H2", parent=base["Heading2"], fontName=HEAD_FONT, fontSize=typo["h2_pt"],
            leading=22, textColor=palette["brand_blue"], spaceBefore=3.5 * mm,
            spaceAfter=2 * mm, keepWithNext=True, wordWrap="CJK"
        ),
        "h3": ParagraphStyle(
            "H3", parent=base["Heading3"], fontName=HEAD_FONT, fontSize=12.8,
            leading=18, textColor=palette["ink"], spaceBefore=3 * mm,
            spaceAfter=1.5 * mm, keepWithNext=True, wordWrap="CJK"
        ),
        "body": ParagraphStyle(
            "Body", parent=base["BodyText"], fontName=BODY_FONT, fontSize=typo["body_pt"],
            leading=typo["body_leading_pt"], textColor=palette["ink"],
            spaceAfter=2.1 * mm, wordWrap="CJK"
        ),
        "bullet": ParagraphStyle(
            "Bullet", parent=base["BodyText"], fontName=BODY_FONT, fontSize=10.1,
            leading=15.2, leftIndent=6 * mm, firstLineIndent=-4 * mm,
            textColor=palette["ink"], spaceAfter=1.2 * mm, wordWrap="CJK"
        ),
        "caption": ParagraphStyle(
            "Caption", parent=base["BodyText"], fontName=BODY_FONT, fontSize=9,
            leading=13, textColor=palette["muted"], alignment=TA_CENTER, wordWrap="CJK"
        ),
        "path": ParagraphStyle(
            "Path", parent=base["BodyText"], fontName=HEAD_FONT, fontSize=10.2,
            leading=15, textColor=palette["brand_navy"], wordWrap="CJK"
        ),
        "callout_title": ParagraphStyle(
            "CalloutTitle", parent=base["BodyText"], fontName=HEAD_FONT, fontSize=10.1,
            leading=14, textColor=palette["ink"], spaceAfter=1 * mm, wordWrap="CJK"
        ),
        "callout": ParagraphStyle(
            "Callout", parent=base["BodyText"], fontName=BODY_FONT, fontSize=9.7,
            leading=14.5, textColor=palette["ink"], wordWrap="CJK"
        ),
        "copy_title": ParagraphStyle(
            "CopyTitle", parent=base["BodyText"], fontName=HEAD_FONT, fontSize=9,
            leading=13, textColor=colors.HexColor("#BDEFFF"), wordWrap="CJK"
        ),
        "copy_value": ParagraphStyle(
            "CopyValue", parent=base["BodyText"], fontName=BODY_FONT, fontSize=12,
            leading=17, textColor=colors.white, wordWrap="CJK"
        ),
        "card_title": ParagraphStyle(
            "CardTitle", parent=base["BodyText"], fontName=HEAD_FONT, fontSize=11,
            leading=15, textColor=palette["brand_navy"], wordWrap="CJK"
        ),
        "card_body": ParagraphStyle(
            "CardBody", parent=base["BodyText"], fontName=BODY_FONT, fontSize=9.2,
            leading=13.5, textColor=palette["ink"], wordWrap="CJK"
        ),
    }


def inline_markup(text: str) -> str:
    escaped = html.escape(text.strip())
    escaped = re.sub(r"`([^`]+)`", rf'<font name="{HEAD_FONT}" color="#075FCB">\1</font>', escaped)
    escaped = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", escaped)
    escaped = re.sub(r"([\u3040-\u30ff\u3400-\u9fff]+)", rf'<font name="{CJK_FONT}">\1</font>', escaped)
    return escaped


def make_heading(text: str, style, counter: int, level: int):
    paragraph = Paragraph(inline_markup(text), style)
    paragraph.bookmark_key = f"heading-{counter}"
    paragraph.bookmark_title = text
    paragraph.outline_level = level
    return paragraph


def screenshot_card(path: Path, caption: str, styles, palette: dict, max_width=166 * mm, max_height=112 * mm):
    if not path.exists():
        raise FileNotFoundError(path)
    with PILImage.open(path) as source:
        px_w, px_h = source.size
    scale = min(max_width / px_w, max_height / px_h)
    width = px_w * scale
    height = px_h * scale
    image = Image(str(path), width=width, height=height, mask="auto")
    caption_p = Paragraph(inline_markup(caption), styles["caption"])
    card = Table([[image], [caption_p]], colWidths=[width + 8 * mm], hAlign="CENTER")
    card.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), colors.white),
        ("BOX", (0, 0), (-1, -1), 0.65, palette["line"]),
        ("LEFTPADDING", (0, 0), (-1, -1), 4 * mm),
        ("RIGHTPADDING", (0, 0), (-1, -1), 4 * mm),
        ("TOPPADDING", (0, 0), (-1, 0), 4 * mm),
        ("BOTTOMPADDING", (0, 0), (-1, 0), 2.5 * mm),
        ("TOPPADDING", (0, 1), (-1, 1), 1 * mm),
        ("BOTTOMPADDING", (0, 1), (-1, 1), 3 * mm),
    ]))
    return KeepTogether([Spacer(1, 1.5 * mm), card, Spacer(1, 2.5 * mm)])


def callout(kind: str, lines: list[str], styles, palette: dict):
    if kind == "COPY":
        title = lines[0] if lines else "복사할 값"
        values = lines[1:] if len(lines) > 1 else []
        value = "<br/>".join(inline_markup(line) for line in values)
        table = Table([
            [Paragraph(inline_markup(title), styles["copy_title"])],
            [Paragraph(value, styles["copy_value"])],
        ], colWidths=[170 * mm], hAlign="CENTER")
        table.setStyle(TableStyle([
            ("BACKGROUND", (0, 0), (-1, -1), palette["brand_navy"]),
            ("LINEBEFORE", (0, 0), (0, -1), 4, palette["brand_cyan"]),
            ("LEFTPADDING", (0, 0), (-1, -1), 5 * mm),
            ("RIGHTPADDING", (0, 0), (-1, -1), 5 * mm),
            ("TOPPADDING", (0, 0), (-1, 0), 3 * mm),
            ("BOTTOMPADDING", (0, 0), (-1, 0), 1 * mm),
            ("TOPPADDING", (0, 1), (-1, 1), 1 * mm),
            ("BOTTOMPADDING", (0, 1), (-1, 1), 3.5 * mm),
        ]))
        return KeepTogether([Spacer(1, 1.5 * mm), table, Spacer(1, 2.5 * mm)])

    settings = {
        "TIP": ("알아두면 좋아요", palette["tip"], palette["brand_cyan"]),
        "IMPORTANT": ("중요", palette["important"], palette["brand_yellow"]),
        "SECURITY": ("보안", palette["security"], colors.HexColor("#D34A4A")),
        "TROUBLESHOOT": ("문제가 있다면", palette["troubleshoot"], colors.HexColor("#7257C8")),
        "RESULT": ("정상이라면", colors.HexColor("#EAF8EF"), colors.HexColor("#2FA866")),
    }
    label, background, accent = settings[kind]
    body = "<br/>".join(inline_markup(line) for line in lines)
    table = Table([
        [Paragraph(label, styles["callout_title"])],
        [Paragraph(body, styles["callout"])],
    ], colWidths=[170 * mm], hAlign="CENTER")
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), background),
        ("LINEBEFORE", (0, 0), (0, -1), 4, accent),
        ("LEFTPADDING", (0, 0), (-1, -1), 5 * mm),
        ("RIGHTPADDING", (0, 0), (-1, -1), 5 * mm),
        ("TOPPADDING", (0, 0), (-1, 0), 3 * mm),
        ("BOTTOMPADDING", (0, 0), (-1, 0), 0),
        ("TOPPADDING", (0, 1), (-1, 1), 1 * mm),
        ("BOTTOMPADDING", (0, 1), (-1, 1), 3.5 * mm),
    ]))
    return KeepTogether([Spacer(1, 1.5 * mm), table, Spacer(1, 2.5 * mm)])


def path_box(text: str, styles, palette: dict):
    table = Table([[Paragraph(inline_markup(text.replace("**", "")), styles["path"])]], colWidths=[170 * mm])
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), colors.HexColor("#EEF5FF")),
        ("BOX", (0, 0), (-1, -1), 0.6, colors.HexColor("#B9D5F7")),
        ("LEFTPADDING", (0, 0), (-1, -1), 4 * mm),
        ("RIGHTPADDING", (0, 0), (-1, -1), 4 * mm),
        ("TOPPADDING", (0, 0), (-1, -1), 3 * mm),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 3 * mm),
    ]))
    return KeepTogether([table, Spacer(1, 2.5 * mm)])


def toc_table(styles, palette: dict):
    entries = [
        ("START HERE", "전체 흐름과 준비물", "2"),
        ("PART 1", "설치 전에 준비하기", "4"),
        ("PART 2", "Discord 개인용 Application", "5"),
        ("PART 3", "처음 연결하기", "14"),
        ("PART 4", "게임에서 HUD 사용하기", "21"),
        ("PART 5", "내 취향에 맞게 설정하기", "24"),
        ("PART 6", "판매 대기열 사용하기", "30"),
        ("PART 7", "문제가 생겼을 때", "34"),
        ("PART 8", "보안 · 데이터 · 제한사항", "38"),
        ("QUICK", "한 페이지 빠른 참조", "40"),
    ]
    rows = []
    for label, title, page in entries:
        rows.append([
            Paragraph(f"<b>{label}</b>", styles["card_title"]),
            Paragraph(inline_markup(title), styles["card_body"]),
            Paragraph(f"<b>{page}</b>", styles["card_title"]),
        ])
    table = Table(rows, colWidths=[32 * mm, 119 * mm, 15 * mm], hAlign="CENTER")
    commands = [
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
        ("LINEBELOW", (0, 0), (-1, -2), 0.4, palette["line"]),
        ("LEFTPADDING", (0, 0), (-1, -1), 3 * mm),
        ("RIGHTPADDING", (0, 0), (-1, -1), 3 * mm),
        ("TOPPADDING", (0, 0), (-1, -1), 2.3 * mm),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 2.3 * mm),
        ("TEXTCOLOR", (2, 0), (2, -1), palette["brand_blue"]),
        ("ALIGN", (2, 0), (2, -1), "CENTER"),
    ]
    table.setStyle(TableStyle(commands))
    return KeepTogether([table, Spacer(1, 4 * mm)])


def card_grid(cards: list[tuple[str, str]], styles, palette: dict, columns=3):
    rows = []
    for index in range(0, len(cards), columns):
        row = []
        for title, body in cards[index:index + columns]:
            content = [Paragraph(inline_markup(title), styles["card_title"]), Spacer(1, 1.5 * mm), Paragraph(inline_markup(body), styles["card_body"])]
            inner = Table([[content]], colWidths=[(166 / columns - 4) * mm])
            inner.setStyle(TableStyle([
                ("BACKGROUND", (0, 0), (-1, -1), palette["panel"]),
                ("BOX", (0, 0), (-1, -1), 0.6, palette["line"]),
                ("LEFTPADDING", (0, 0), (-1, -1), 4 * mm),
                ("RIGHTPADDING", (0, 0), (-1, -1), 4 * mm),
                ("TOPPADDING", (0, 0), (-1, -1), 4 * mm),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 4 * mm),
            ]))
            row.append(inner)
        while len(row) < columns:
            row.append(Spacer(1, 1))
        rows.append(row)
    outer = Table(rows, colWidths=[166 / columns * mm] * columns, hAlign="CENTER")
    outer.setStyle(TableStyle([
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 1.5 * mm),
        ("RIGHTPADDING", (0, 0), (-1, -1), 1.5 * mm),
        ("TOPPADDING", (0, 0), (-1, -1), 1.5 * mm),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 1.5 * mm),
    ]))
    return KeepTogether([outer, Spacer(1, 3 * mm)])


def special_directive(text: str, styles, palette: dict):
    if text.startswith("FLOW:"):
        items = [item.strip() for item in text.split(":", 1)[1].split("|")]
        return KeepTogether([Spacer(1, 1 * mm), WorkflowFlowable(items, palette), Spacer(1, 4 * mm)])
    if text == "TOC":
        return toc_table(styles, palette)
    if text == "CHANNEL_MAP":
        return card_grid([
            ("SERVER · 고정", "Gacha Overlay가 연결할 Production Guild"),
            ("MAIN · 선택", "HUD에 채팅을 표시할 채널 한 개"),
            ("SALES · 고정", "판매 대기열에 사용하는 Production Sales Channel"),
        ], styles, palette)
    if text == "HOTKEYS":
        return card_grid([
            ("F9 · SHOW / HIDE", "설정을 유지한 채 HUD 표시만 켜거나 끕니다."),
            ("F10 · LOCK / UNLOCK", "게임 클릭 통과와 HUD 편집 가능 상태를 바꿉니다."),
        ], styles, palette, columns=2)
    if text == "LOCK_COMPARE":
        return card_grid([
            ("LOCKED", "게임 클릭 통과 · HUD 이동/크기 조절 불가 · Scroll/Button 입력 없음"),
            ("UNLOCKED", "HUD 이동 · 크기 조절 · Settings · Media 확대와 Queue 조작 가능"),
        ], styles, palette, columns=2)
    if text == "PRODUCT_EXAMPLES":
        return card_grid([
            ("벙커", "상품 한 개"),
            ("벙커 x2", "같은 상품 두 개"),
            ("벙커 x2 · 나클", "수량과 다른 상품 조합"),
        ], styles, palette)
    if text == "QUICK_REFERENCE":
        return card_grid([
            ("F9", "HUD 표시 / 숨김 · 트레이 메뉴에서도 가능"),
            ("F10", "잠금 / 편집 상태"),
            ("SETTINGS", "트레이 아이콘 우클릭 → 설정 또는 F10 → Gear"),
            ("SALES PAUSED", "Discord Sales Channel과 Accessibility 확인"),
            ("MAIN CHANNEL", "Settings → Server"),
            ("DIAGNOSTICS", "Settings → 진단 및 복구"),
        ], styles, palette, columns=2)
    raise ValueError(f"Unknown manual directive: {text}")


def parse_body(source_path: Path, styles, palette: dict):
    source = source_path.read_text(encoding="utf-8")
    if "<!-- BODY -->" not in source:
        raise ValueError("Manual source is missing BODY marker")
    lines = source.split("<!-- BODY -->", 1)[1].splitlines()
    story = []
    heading_counter = 0
    i = 0
    while i < len(lines):
        raw = lines[i]
        stripped = raw.strip()
        if not stripped:
            i += 1
            continue
        if stripped == "<!-- PAGE -->":
            story.append(PageBreak())
            i += 1
            continue
        directive_match = re.match(r"<!--\s*(.+?)\s*-->", stripped)
        if directive_match:
            story.append(special_directive(directive_match.group(1), styles, palette))
            i += 1
            continue
        if stripped.startswith("# "):
            title = stripped[2:].strip()
            heading_counter += 1
            if title.startswith("PART "):
                banner = PartBanner(title, palette)
                banner.bookmark_key = f"heading-{heading_counter}"
                banner.bookmark_title = title
                banner.outline_level = 0
                story.extend([banner, Spacer(1, 4 * mm)])
            else:
                story.extend([
                    make_heading(title, styles["h1"], heading_counter, 0),
                    AccentRule(palette["brand_blue"]),
                    Spacer(1, 3 * mm),
                ])
            i += 1
            continue
        if stripped.startswith("## "):
            title = stripped[3:].strip()
            heading_counter += 1
            story.append(make_heading(title, styles["h2"], heading_counter, 1))
            i += 1
            continue
        if stripped.startswith("### "):
            title = stripped[4:].strip()
            heading_counter += 1
            story.append(make_heading(title, styles["h3"], heading_counter, 2))
            i += 1
            continue
        image_match = re.match(r"!\[([^]]*)\]\(([^)]+)\)", stripped)
        if image_match:
            caption, relative = image_match.groups()
            asset = (source_path.parent / relative).resolve()
            compact_part_assets = {
                "13-hud.png",
                "14-settings-general.png",
                "17-sales-settings.png",
            }
            max_height = 88 * mm if asset.name in compact_part_assets else 112 * mm
            story.append(screenshot_card(asset, caption, styles, palette, max_height=max_height))
            i += 1
            continue
        if stripped.startswith("> [!"):
            match = re.match(r"> \[!([A-Z]+)\]", stripped)
            if not match:
                raise ValueError(stripped)
            kind = match.group(1)
            i += 1
            quote_lines = []
            while i < len(lines) and lines[i].strip().startswith(">"):
                quote_lines.append(lines[i].strip()[1:].strip())
                i += 1
            story.append(callout(kind, quote_lines, styles, palette))
            continue
        if re.match(r"^[-*] ", stripped):
            story.append(Paragraph("• " + inline_markup(re.sub(r"^[-*] ", "", stripped)), styles["bullet"]))
            i += 1
            continue
        numbered = re.match(r"^(\d+)\.\s+(.+)", stripped)
        if numbered:
            story.append(Paragraph(f"{numbered.group(1)}. {inline_markup(numbered.group(2))}", styles["bullet"]))
            i += 1
            continue
        if stripped.startswith("**") and "→" in stripped:
            story.append(path_box(stripped, styles, palette))
            i += 1
            continue

        paragraph_lines = [raw.rstrip()]
        i += 1
        while i < len(lines):
            candidate_raw = lines[i]
            candidate = candidate_raw.strip()
            if (
                not candidate
                or candidate.startswith("#")
                or candidate.startswith("!")
                or candidate.startswith(">")
                or candidate.startswith("<!--")
                or re.match(r"^[-*] ", candidate)
                or re.match(r"^\d+\.\s+", candidate)
                or (candidate.startswith("**") and "→" in candidate)
            ):
                break
            paragraph_lines.append(candidate_raw.rstrip())
            i += 1
        fragments = []
        for line in paragraph_lines:
            fragments.append(inline_markup(line))
            if line.endswith("  "):
                fragments.append("<br/>")
            else:
                fragments.append(" ")
        story.append(Paragraph("".join(fragments).strip(), styles["body"]))
    return story


def build_cover(repo: Path, styles, palette: dict):
    icon = repo / "assets/input/GachaOverlay_AppIcon_Source.png"
    hud = repo / "docs/manual/assets/1.0.0-rc.1/13-hud.png"
    hud_image = Image(str(hud), width=63 * mm, height=62.3 * mm, mask="auto")
    left = [
        Paragraph("Discord 채팅과 판매 대기열을<br/>게임 화면에서 확인하는 Windows HUD", styles["cover_desc"]),
        Spacer(1, 5 * mm),
        Paragraph("실제 UI를 따라 한 단계씩 설정하는 초보자용 Release Guide", styles["cover_meta"]),
        Spacer(1, 7 * mm),
        Paragraph("Manual 1.1 · 2026-09-01", styles["cover_meta"]),
    ]
    lower = Table([[left, hud_image]], colWidths=[91 * mm, 67 * mm], hAlign="CENTER")
    lower.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, -1), colors.white),
        ("BOX", (0, 0), (-1, -1), 0.7, palette["line"]),
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
        ("LEFTPADDING", (0, 0), (-1, -1), 6 * mm),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6 * mm),
        ("TOPPADDING", (0, 0), (-1, -1), 6 * mm),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 6 * mm),
    ]))
    security = Paragraph(
        "Client Secret과 token은 비밀번호처럼 보호하세요. Discord User Token이나 DevTools token은 사용하지 않습니다.",
        styles["cover_meta"],
    )
    return [
        CoverHero(icon, palette),
        Spacer(1, 8 * mm),
        lower,
        Spacer(1, 9 * mm),
        security,
        PageBreak(),
    ]


def build_pdf(repo: Path, source: Path, theme_path: Path, output: Path) -> None:
    theme = load_theme(theme_path)
    palette = {name: hex_color(value) for name, value in theme["colors"].items()}
    register_fonts(repo)
    styles = make_styles(theme, palette)
    page = theme["page"]
    output.parent.mkdir(parents=True, exist_ok=True)
    doc = ManualDocTemplate(
        str(output),
        icon_path=repo / "assets/input/GachaOverlay_AppIcon_Source.png",
        palette=palette,
        pagesize=A4,
        leftMargin=page["margin_left_mm"] * mm,
        rightMargin=page["margin_right_mm"] * mm,
        topMargin=page["margin_top_mm"] * mm,
        bottomMargin=page["margin_bottom_mm"] * mm,
        title="Gacha Overlay 사용자 설명서",
        author="Gacha Overlay",
        subject="Gacha Overlay 1.0.0-rc.1 Controlled Test Release 사용자 설명서",
        creator="Gacha Overlay reproducible manual pipeline",
    )
    story = build_cover(repo, styles, palette)
    story.extend(parse_body(source, styles, palette))
    doc.build(story)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--theme", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    build_pdf(args.repo.resolve(), args.source.resolve(), args.theme.resolve(), args.output.resolve())
    print(args.output.resolve())


if __name__ == "__main__":
    main()
