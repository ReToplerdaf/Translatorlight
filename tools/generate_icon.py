#!/usr/bin/env python3
"""Draws src/Translatorlight/Assets/translatorlight.ico - the icon of the exe itself.

The shape matches the one the tray icon is drawn with at run time: a speech bubble with the
letter the text is translated into. Run it only when the picture has to change:

    pip install Pillow
    python3 tools/generate_icon.py
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUTPUT = Path(__file__).resolve().parent.parent / "src" / "Translatorlight" / "Assets" / "translatorlight.ico"
SIZES = (16, 20, 24, 32, 48, 64, 128, 256)

# The same 100x100 design canvas the C# renderer uses.
DESIGN = 100
SUPERSAMPLE = 4
BUBBLE = (5, 7, 95, 75)
BUBBLE_RADIUS = 20
TAIL = ((26, 68), (26, 95), (52, 71))
LETTER = "Я"

FILL = (0x24, 0x5D, 0xD2, 0xFF)
INK = (0xFF, 0xFF, 0xFF, 0xFF)

FONT_CANDIDATES = (
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
    "C:/Windows/Fonts/segoeuib.ttf",
    "C:/Windows/Fonts/arialbd.ttf",
)


def load_font(pixels):
    for candidate in FONT_CANDIDATES:
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, pixels)
    raise SystemExit("No bold TrueType font found; install DejaVu or Liberation fonts.")


def render(size):
    """Draws one square icon, oversampled and shrunk back down so the edges stay smooth."""
    canvas = size * SUPERSAMPLE
    scale = canvas / DESIGN
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    box = [value * scale for value in BUBBLE]
    draw.rounded_rectangle(box, radius=BUBBLE_RADIUS * scale, fill=FILL)
    draw.polygon([(x * scale, y * scale) for x, y in TAIL], fill=FILL)

    font = load_font(int(round(52 * scale)))
    centre = ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2)
    draw.text(centre, LETTER, font=font, fill=INK, anchor="mm")

    return image.resize((size, size), Image.LANCZOS)


def main():
    frames = [render(size) for size in SIZES]
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)

    try:
        frames[-1].save(OUTPUT, format="ICO", sizes=[(size, size) for size in SIZES],
                        append_images=frames[:-1])
    except TypeError:
        # Older Pillow cannot take ready-made frames and resizes the largest one itself.
        frames[-1].save(OUTPUT, format="ICO", sizes=[(size, size) for size in SIZES])

    print(f"{OUTPUT} written ({OUTPUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
