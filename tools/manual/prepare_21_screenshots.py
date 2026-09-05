"""Crop and irreversibly redact user-supplied, real 2.1 screenshots.

No UI is synthesized. The input directory is private and never packaged.
Only cropped/redacted RGB PNGs are written to documentation assets.
"""
import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input-dir", type=Path, required=True)
    args = parser.parse_args()
    output = REPO / "docs/2.1/assets/screenshots"
    output.mkdir(parents=True, exist_ok=True)
    records = []

    def make(name, source, crop=None, masks=()):
        path = args.input_dir / f"codex-clipboard-{source}.png"
        with Image.open(path) as original:
            result = original.convert("RGB")
        draw = ImageDraw.Draw(result)
        for box in masks:
            draw.rectangle(box, fill="#454C53")
        if crop:
            result = result.crop(crop)
        target = output / name
        result.save(target, optimize=True)
        records.append({"file": name, "sourceSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        "crop": crop, "redactedRegions": list(masks), "pixels": result.size,
                        "sha256": hashlib.sha256(target.read_bytes()).hexdigest()})

    main_source = "0f07dfdf-eb0c-4157-ac22-6f22210b2b99"
    main_masks = [(40,65,340,120), (40,149,340,205), (40,231,370,292),
                  (40,315,380,392), (40,425,330,451), (51,680,485,703), (49,743,490,802)]
    make("01-main-hud.png", main_source, (10,0,663,810), main_masks)
    make("05-main-chat.png", main_source, (10,305,663,665), main_masks)
    make("06-sales.png", main_source, (10,671,663,810), main_masks)
    overview = "4b7c3c4e-5c43-40d0-85e6-5660fa5c1acc"
    overview_masks = [(1053,167,1335,233), (1053,258,1385,327), (1053,343,1375,403),
                      (1053,429,1375,481), (1053,511,1400,564), (1053,595,1495,649),
                      (1070,688,1555,715), (1065,746,1570,852)]
    make("04-three-huds-overview.png", overview, (104,54,1702,878), overview_masks)
    make("07-gta-companion.png", overview, (111,92,490,411))
    make("08-business-unlocked.png", overview, (514,82,915,324))
    make("08-business-cargo.png", overview, (514,326,915,589))
    make("09-business-locked.png", "ea6d17ee-8369-4c6b-8f58-3d345c36ecaf", (15,464,427,631))
    make("09-business-compact.png", "ea6d17ee-8369-4c6b-8f58-3d345c36ecaf", (15,22,427,419))
    make("09-general-timer.png", overview, (514,805,915,854))
    make("02-discord-connected.png", "1dcb22ee-9e2d-4d3e-9c36-bbd0fa7076b3", (270,112,936,390))
    hotkeys = "32709d20-ed48-4843-ae1b-9ac2bf1514eb"
    make("03-settings-hud-hotkeys.png", hotkeys, (270,224,938,620))
    make("10-settings-hotkey-capture.png", hotkeys, (270,295,938,620))
    make("11-settings-visual-media.png", "4697bf06-9f42-4217-9d7d-b5bcd2cb6365", (270,1050,936,1353))
    make("11-settings-themes.png", "20d98497-ccf7-4183-88de-0361440e0bc6", (270,285,850,669))
    make("11-settings-media.png", "3ab01f2e-9517-45d3-bc20-92b82e66d8d1", (270,143,936,555))
    make("12-settings-diagnostics.png", "820567d3-99e4-4f9a-9c7f-8d02afd84e89", (270,421,936,825))
    make("12-diagnostics-button.png", "820567d3-99e4-4f9a-9c7f-8d02afd84e89", (270,739,936,825))
    make("13-settings-sales.png", "de4cc7b5-9968-43c8-a198-9e9b27dd95ea", (270,313,936,562))
    make("14-sales-history.png", "8cc537b0-314a-4173-af84-04f95f2a518b", (270,112,936,538))
    make("15-settings-companion.png", "0875987d-6f50-4522-9bf0-473fc1fff7d7", (270,187,936,623))
    business = "765805cc-330c-4195-92d4-ff7b67ab2298"
    make("16-settings-business.png", business, (270,183,936,613))
    make("17-settings-business-options.png", business, (279,638,936,990))
    make("18-settings-timer-sound.png", business, (270,1565,936,1699))
    (output / "provenance.json").write_text(json.dumps(records, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared {len(records)} actual-UI documentation crops; raw inputs are not copied.")


if __name__ == "__main__":
    main()
