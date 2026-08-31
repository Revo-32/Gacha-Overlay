from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / "tmp" / "manual-capture"
OUT = ROOT / "docs" / "manual" / "assets" / "1.0.0-rc.1"


def _font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = (
        Path(r"C:\Windows\Fonts\malgun.ttf"),
        Path(r"C:\Windows\Fonts\segoeui.ttf"),
    )
    for candidate in candidates:
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size)
    return ImageFont.load_default()


def crop_with_masks(
    source: Path,
    destination: Path,
    crop: tuple[int, int, int, int],
    masks: tuple[tuple[tuple[int, int, int, int], str], ...] = (),
) -> None:
    image = Image.open(source).convert("RGB").crop(crop)
    draw = ImageDraw.Draw(image)
    for rectangle, label in masks:
        draw.rounded_rectangle(rectangle, radius=6, fill="#111318", outline="#6c7180", width=2)
        if label:
            x1, y1, x2, y2 = rectangle
            font = _font(min(18, max(12, (y2 - y1) - 10)))
            bbox = draw.textbbox((0, 0), label, font=font)
            x = x1 + 12
            y = y1 + max(0, ((y2 - y1) - (bbox[3] - bbox[1])) // 2 - bbox[1])
            draw.text((x, y), label, fill="#f3f4f6", font=font)
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination, format="PNG", optimize=True)


def normalize_output_assets() -> None:
    """Re-encode every retained asset as metadata-free, true PNG data."""
    for source in sorted(OUT.glob("*.png")):
        with Image.open(source) as opened:
            image = opened.convert("RGB")
            image.load()
        image.info.clear()
        temporary = source.with_name(source.name + ".tmp")
        image.save(temporary, format="PNG", optimize=True)
        temporary.replace(source)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--hud-source", type=Path)
    args = parser.parse_args()

    oauth = RAW / "oauth-current.png"
    if oauth.exists():
        crop_with_masks(
            oauth,
            OUT / "03-oauth2-page.png",
            (20, 60, 1765, 500),
            (
                ((10, 53, 215, 103), "APPLICATION"),
                ((390, 250, 815, 297), "YOUR_CLIENT_ID"),
            ),
        )
        crop_with_masks(oauth, OUT / "04-redirect-uri.png", (340, 500, 1765, 760))
        crop_with_masks(
            oauth,
            OUT / "05-client-id-location.png",
            (340, 215, 1765, 495),
            (((70, 97, 495, 141), "YOUR_CLIENT_ID"),),
        )
        crop_with_masks(oauth, OUT / "06-client-secret-location.png", (830, 215, 1600, 460))

    onboarding_oauth = RAW / "onboarding-oauth-current.png"
    if onboarding_oauth.exists():
        crop_with_masks(
            onboarding_oauth,
            OUT / "09-discord-authentication.png",
            (0, 0, 754, 617),
            (((57, 280, 678, 326), "YOUR_CLIENT_ID"),),
        )

    discord_settings = RAW / "discord-settings-current.png"
    if discord_settings.exists():
        crop_with_masks(
            discord_settings,
            OUT / "09b-discord-authenticated.png",
            (0, 0, 974, 757),
            (((273, 284, 912, 329), "YOUR_CLIENT_ID"),),
        )

    if args.hud_source is not None:
        crop_with_masks(
            args.hud_source,
            OUT / "13-hud.png",
            (0, 20, 652, 665),
            (
                ((20, 50, 630, 485), "채팅 내용은 개인정보 보호를 위해 가렸습니다."),
                ((40, 583, 100, 612), "예시"),
            ),
        )
        crop_with_masks(
            args.hud_source,
            OUT / "17-sales-queue.png",
            (6, 526, 648, 641),
            (((39, 78, 100, 104), "예시"),),
        )

    normalize_output_assets()


if __name__ == "__main__":
    main()
