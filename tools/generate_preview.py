#!/usr/bin/env python3
"""Draws docs/preview.png - the picture of the widget used in the README.

It is a mock-up, not a screenshot: the widget is laid out exactly as WidgetForm lays it out,
so the picture can be regenerated without a Windows machine.

    pip install Pillow
    python3 tools/generate_preview.py
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUTPUT = Path(__file__).resolve().parent.parent / "docs" / "preview.png"

SCALE = 2
WIDTH, HEIGHT = 372, 252
HEADER = 32
FOOTER = 26
INPUT = 48
MARGIN = 28

WINDOW = (0xF6, 0xF6, 0xF9)
HEADER_FILL = (0xEA, 0xEA, 0xF0)
SURFACE = (0xFF, 0xFF, 0xFF)
BORDER = (0xD3, 0xD3, 0xDB)
TEXT = (0x1A, 0x1A, 0x1F)
MUTED = (0x69, 0x69, 0x75)
SELECTION = (0xCD, 0xDE, 0xFB)
ACCENT = (0x2F, 0x6F, 0xEC)
PAGE = (0xE8, 0xE8, 0xEE)

SOURCE = "Type here, or drop text on the widget."
RESULT = "Напечатайте здесь или бросьте текст на виджет."

REGULAR = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"


def font(path, points):
    if not Path(path).exists():
        raise SystemExit(f"Font not found: {path}")
    return ImageFont.truetype(path, int(round(points * SCALE)))


def wrap(draw, text, typeface, limit):
    lines, current = [], ""
    for word in text.split():
        candidate = f"{current} {word}".strip()
        if draw.textlength(candidate, font=typeface) <= limit or not current:
            current = candidate
        else:
            lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines


def main():
    width = (WIDTH + MARGIN * 2) * SCALE
    height = (HEIGHT + MARGIN * 2) * SCALE
    image = Image.new("RGB", (width, height), PAGE)
    draw = ImageDraw.Draw(image)

    left, top = MARGIN * SCALE, MARGIN * SCALE
    right, bottom = left + WIDTH * SCALE, top + HEIGHT * SCALE

    # The window itself, with the rounded corners Windows 11 gives it.
    draw.rounded_rectangle((left, top, right, bottom), radius=8 * SCALE, fill=WINDOW, outline=BORDER)
    draw.rounded_rectangle((left, top, right, top + HEADER * SCALE), radius=8 * SCALE, fill=HEADER_FILL)
    draw.rectangle((left, top + (HEADER - 8) * SCALE, right, top + HEADER * SCALE), fill=HEADER_FILL)

    small = font(REGULAR, 9)
    tiny = font(REGULAR, 8)
    body = font(REGULAR, 10.5)

    draw.text((left + 11 * SCALE, top + HEADER * SCALE / 2), "Авто · EN → RU",
              font=small, fill=MUTED, anchor="lm")

    # The three header buttons: swap, pin, hide.
    centre = top + HEADER * SCALE / 2
    swap = right - 75 * SCALE
    draw.line((swap - 5 * SCALE, centre - 2 * SCALE, swap + 5 * SCALE, centre - 2 * SCALE), fill=MUTED, width=SCALE)
    draw.line((swap + 2 * SCALE, centre - 5 * SCALE, swap + 5 * SCALE, centre - 2 * SCALE), fill=MUTED, width=SCALE)
    draw.line((swap - 5 * SCALE, centre + 2 * SCALE, swap + 5 * SCALE, centre + 2 * SCALE), fill=MUTED, width=SCALE)
    draw.line((swap - 5 * SCALE, centre + 2 * SCALE, swap - 2 * SCALE, centre + 5 * SCALE), fill=MUTED, width=SCALE)

    pin = right - 45 * SCALE
    draw.ellipse((pin - 4 * SCALE, centre - 6 * SCALE, pin + 4 * SCALE, centre + 2 * SCALE),
                 outline=MUTED, width=SCALE)
    draw.line((pin, centre + 2 * SCALE, pin, centre + 6 * SCALE), fill=MUTED, width=SCALE)

    close = right - 17 * SCALE
    draw.line((close - 4 * SCALE, centre - 4 * SCALE, close + 4 * SCALE, centre + 4 * SCALE), fill=MUTED, width=SCALE)
    draw.line((close + 4 * SCALE, centre - 4 * SCALE, close - 4 * SCALE, centre + 4 * SCALE), fill=MUTED, width=SCALE)

    # The box that is typed into, with the caret sitting after the text.
    input_top = top + (HEADER + 6) * SCALE
    input_bottom = input_top + (INPUT - 6) * SCALE
    draw.rectangle((left + 10 * SCALE, input_top, right - 10 * SCALE, input_bottom),
                   fill=SURFACE, outline=ACCENT)

    typed_left = left + 18 * SCALE
    typed_top = input_top + 8 * SCALE
    draw.text((typed_left, typed_top), SOURCE, font=body, fill=TEXT)
    caret = typed_left + draw.textlength(SOURCE, font=body) + 2 * SCALE
    draw.line((caret, typed_top, caret, typed_top + 15 * SCALE), fill=TEXT, width=SCALE)

    # The translation, selected the moment it arrives.
    result_top = input_bottom + 6 * SCALE
    result_bottom = bottom - (FOOTER + 4) * SCALE
    draw.rectangle((left + 10 * SCALE, result_top, right - 10 * SCALE, result_bottom),
                   fill=SURFACE, outline=BORDER)

    text_left = left + 18 * SCALE
    line_height = 19 * SCALE
    y = result_top + 10 * SCALE
    for line in wrap(draw, RESULT, body, (WIDTH - 44) * SCALE):
        length = draw.textlength(line, font=body)
        draw.rectangle((text_left - 1 * SCALE, y - 2 * SCALE, text_left + length + 1 * SCALE, y + line_height - 4 * SCALE),
                       fill=SELECTION)
        draw.text((text_left, y), line, font=body, fill=TEXT)
        y += line_height

    # The footer says what just happened and offers the translation for dragging out.
    footer_centre = bottom - FOOTER * SCALE / 2
    draw.text((left + 11 * SCALE, footer_centre), "Перевод выделен и скопирован — жмите Ctrl+V",
              font=tiny, fill=MUTED, anchor="lm")
    draw.text((right - 20 * SCALE, footer_centre), "⇗ перетащить", font=tiny, fill=MUTED, anchor="rm")

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    image.save(OUTPUT, optimize=True)
    print(f"{OUTPUT} written ({OUTPUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
