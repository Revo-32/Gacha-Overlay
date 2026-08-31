from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "docs" / "manual" / "assets" / "1.0.0-rc.1"
OUTPUT = ROOT / "docs" / "manual" / "assets" / "1.0.0-rc.1-guide"

ACCENT = "#00CFEA"
BADGE = "#081A7A"
BADGE_TEXT = "#FFFFFF"


@dataclass(frozen=True)
class Marker:
    box: tuple[float, float, float, float]
    number: int
    anchor: tuple[float, float]


# Coordinates are normalized so the source assets can be regenerated without
# tying annotations to one pixel size. Every source is already privacy-redacted.
ANNOTATIONS: dict[str, tuple[Marker, ...]] = {
    "02-new-application.png": (
        Marker((0.89, 0.22, 0.998, 0.56), 1, (0.88, 0.20)),
    ),
    "03-oauth2-page.png": (
        Marker((0.006, 0.51, 0.122, 0.62), 1, (0.13, 0.50)),
        Marker((0.214, 0.398, 0.728, 0.768), 2, (0.73, 0.39)),
    ),
    "04-redirect-uri.png": (
        Marker((0.045, 0.40, 0.966, 0.64), 1, (0.96, 0.38)),
    ),
    "05-client-id-location.png": (
        Marker((0.043, 0.229, 0.354, 0.54), 1, (0.36, 0.22)),
    ),
    "06-client-secret-location.png": (
        Marker((0.023, 0.282, 0.591, 0.576), 1, (0.61, 0.27)),
        Marker((0.023, 0.584, 0.182, 0.751), 2, (0.20, 0.58)),
    ),
    "07-tester-authorization.png": (
        Marker((0.041, 0.337, 0.303, 0.52), 1, (0.31, 0.33)),
        Marker((0.868, 0.398, 0.938, 0.523), 2, (0.86, 0.39)),
    ),
    "08-onboarding-language.png": (
        Marker((0.073, 0.382, 0.447, 0.447), 1, (0.46, 0.38)),
        Marker((0.809, 0.888, 0.881, 0.951), 2, (0.843, 0.86)),
    ),
    "09-discord-authentication.png": (
        Marker((0.066, 0.399, 0.909, 0.792), 1, (0.91, 0.39)),
        Marker((0.809, 0.888, 0.881, 0.951), 2, (0.843, 0.86)),
    ),
    "10-target-server-check.png": (
        Marker((0.067, 0.26, 0.56, 0.58), 1, (0.56, 0.25)),
        Marker((0.809, 0.888, 0.881, 0.951), 2, (0.843, 0.86)),
    ),
    "11-main-channel-selection.png": (
        Marker((0.07, 0.37, 0.61, 0.49), 1, (0.62, 0.36)),
        Marker((0.809, 0.888, 0.881, 0.951), 2, (0.843, 0.86)),
    ),
    "12-sales-compatibility.png": (
        Marker((0.067, 0.26, 0.56, 0.64), 1, (0.57, 0.25)),
        Marker((0.809, 0.888, 0.881, 0.951), 2, (0.843, 0.86)),
    ),
    "15-settings-server.png": (
        Marker((0.276, 0.342, 0.675, 0.58), 1, (0.68, 0.335)),
    ),
    "17-sales-settings.png": (
        Marker((0.276, 0.15, 0.96, 0.72), 1, (0.965, 0.145)),
    ),
    "18-diagnostics-zip.png": (
        Marker((0.267, 0.31, 0.88, 0.86), 1, (0.89, 0.30)),
    ),
}


def _font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    path = Path(r"C:\Windows\Fonts\arialbd.ttf")
    return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default()


def annotate(source: Path, destination: Path, markers: tuple[Marker, ...]) -> None:
    with Image.open(source) as opened:
        image = opened.convert("RGBA")
        image.load()
    image.info.clear()
    draw = ImageDraw.Draw(image, "RGBA")
    width, height = image.size
    stroke = max(3, round(min(width, height) * 0.008))
    badge_radius = max(14, round(min(width, height) * 0.034))
    font = _font(max(14, round(badge_radius * 0.90)))

    for marker in markers:
        x1, y1, x2, y2 = marker.box
        rectangle = (round(x1 * width), round(y1 * height), round(x2 * width), round(y2 * height))
        radius = max(7, round(min(width, height) * 0.018))
        draw.rounded_rectangle(rectangle, radius=radius, outline=ACCENT, width=stroke)

        cx = round(marker.anchor[0] * width)
        cy = round(marker.anchor[1] * height)
        draw.ellipse(
            (cx - badge_radius, cy - badge_radius, cx + badge_radius, cy + badge_radius),
            fill=BADGE,
            outline=ACCENT,
            width=max(2, stroke // 2),
        )
        draw.text(
            (cx, cy),
            str(marker.number),
            font=font,
            fill=BADGE_TEXT,
            anchor="mm",
        )

    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination, format="PNG", optimize=True)


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for filename, markers in ANNOTATIONS.items():
        source = SOURCE / filename
        if not source.exists():
            raise FileNotFoundError(source)
        annotate(source, OUTPUT / filename, markers)
    print(f"Annotated screenshots: {len(ANNOTATIONS)}")


if __name__ == "__main__":
    main()
