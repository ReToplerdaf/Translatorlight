#!/usr/bin/env python3
"""Draws docs/preview.png - the picture of the widget used in the README.

It is a mock-up, not a screenshot: the strip is laid out exactly as WidgetForm lays it out,
so the picture can be regenerated without a Windows machine.

    pip install Pillow
    python3 tools/generate_preview.py
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

DOCS = Path(__file__).resolve().parent.parent / "docs"
OUTPUT = DOCS / "preview.png"
DOCKED_OUTPUT = DOCS / "docked.png"

SCALE = 2
PAGE_WIDTH, PAGE_HEIGHT = 760, 132

# The bar, in the same units WidgetForm uses.
BAR_WIDTH, BAR_HEIGHT = 580, 42
EDGE, BUTTON, RESIZE = 6, 26, 6

TASKBAR_HEIGHT = 48
GAP = 8

DESKTOP = (0x30, 0x4A, 0x6E)
TASKBAR = (0xF1, 0xF1, 0xF5)
WINDOW = (0xF6, 0xF6, 0xF9)
SURFACE = (0xFF, 0xFF, 0xFF)
BORDER = (0xD3, 0xD3, 0xDB)
TEXT = (0x1A, 0x1A, 0x1F)
MUTED = (0x69, 0x69, 0x75)
ACCENT = (0x2F, 0x6F, 0xEC)
SELECTION = (0xCD, 0xDE, 0xFB)

RESULT = "Перетащите текст на полоску, и перевод встанет на его место — целиком выделенный."

REGULAR = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"


def font(points):
    if not Path(REGULAR).exists():
        raise SystemExit(f"Font not found: {REGULAR}")
    return ImageFont.truetype(REGULAR, int(round(points * SCALE)))


def docked():
    """The second picture: the strip registered with the shell as a desktop toolbar."""
    page_width, page_height = 760, 300
    bar_height, taskbar = 40, 40

    image = Image.new("RGB", (page_width * SCALE, page_height * SCALE), DESKTOP)
    draw = ImageDraw.Draw(image)
    body = font(9)
    small = font(8.25)

    # A maximised window: the shell keeps it out of the strip, so it stops above the bar.
    window_bottom = (page_height - taskbar - bar_height) * SCALE
    draw.rectangle((0, 0, page_width * SCALE, window_bottom), fill=SURFACE)
    draw.rectangle((0, 0, page_width * SCALE, 26 * SCALE), fill=(0xEA, 0xEA, 0xF0))
    draw.text((14 * SCALE, 13 * SCALE), "Развёрнутое окно заканчивается здесь",
              font=small, fill=MUTED, anchor="lm")
    for line in range(6):
        y = (48 + line * 18) * SCALE
        draw.rectangle((14 * SCALE, y, (120 + line * 84) * SCALE, y + 6 * SCALE), fill=(0xE4, 0xE4, 0xEC))

    # The bar itself, spanning the whole width with square ends.
    bar_top = window_bottom
    bar_bottom = bar_top + bar_height * SCALE
    draw.rectangle((0, bar_top, page_width * SCALE, bar_bottom), fill=WINDOW, outline=BORDER)
    middle = (bar_top + bar_bottom) / 2

    field_left = EDGE * SCALE
    field_right = (page_width - BUTTON - RESIZE) * SCALE
    draw.rounded_rectangle((field_left, bar_top + 6 * SCALE, field_right, bar_bottom - 6 * SCALE),
                           radius=2 * SCALE, fill=SURFACE, outline=BORDER)
    draw.text((field_left + 9 * SCALE, middle), "перетащите текст", font=body, fill=MUTED, anchor="lm")

    close = field_right + (BUTTON / 2) * SCALE
    draw.line((close - 4 * SCALE, middle - 4 * SCALE, close + 4 * SCALE, middle + 4 * SCALE), fill=MUTED, width=SCALE)
    draw.line((close + 4 * SCALE, middle - 4 * SCALE, close - 4 * SCALE, middle + 4 * SCALE), fill=MUTED, width=SCALE)

    # The real taskbar underneath.
    draw.rectangle((0, bar_bottom, page_width * SCALE, page_height * SCALE), fill=TASKBAR)
    for index in range(5):
        left = (page_width / 2 - 60 + index * 26) * SCALE
        draw.rounded_rectangle((left, bar_bottom + 11 * SCALE, left + 18 * SCALE, bar_bottom + 29 * SCALE),
                               radius=4 * SCALE, fill=(0xC8, 0xC8, 0xD2))

    image.save(DOCKED_OUTPUT, optimize=True)
    print(f"{DOCKED_OUTPUT} written ({DOCKED_OUTPUT.stat().st_size} bytes)")


def main():
    image = Image.new("RGB", (PAGE_WIDTH * SCALE, PAGE_HEIGHT * SCALE), DESKTOP)
    draw = ImageDraw.Draw(image)

    body = font(10)
    small = font(8.25)

    # A hint of the taskbar the strip is meant to sit on.
    taskbar_top = (PAGE_HEIGHT - TASKBAR_HEIGHT) * SCALE
    draw.rectangle((0, taskbar_top, PAGE_WIDTH * SCALE, PAGE_HEIGHT * SCALE), fill=TASKBAR)
    for index in range(5):
        left = (PAGE_WIDTH / 2 - 60 + index * 26) * SCALE
        draw.rounded_rectangle((left, taskbar_top + 14 * SCALE, left + 18 * SCALE, taskbar_top + 32 * SCALE),
                               radius=4 * SCALE, fill=(0xC8, 0xC8, 0xD2))

    # The strip itself, parked in the corner just above the taskbar.
    left = (PAGE_WIDTH - BAR_WIDTH - 20) * SCALE
    top = (PAGE_HEIGHT - TASKBAR_HEIGHT - GAP - BAR_HEIGHT) * SCALE
    right = left + BAR_WIDTH * SCALE
    bottom = top + BAR_HEIGHT * SCALE
    draw.rounded_rectangle((left, top, right, bottom), radius=7 * SCALE, fill=WINDOW, outline=BORDER)

    middle = (top + bottom) / 2

    # The one line everything happens in, drawn separately so the text is clipped by its edge.
    field_left = left + EDGE * SCALE
    field_right = right - (BUTTON * 2 + RESIZE) * SCALE
    field_top = top + 6 * SCALE
    field_bottom = bottom - 6 * SCALE
    draw.rounded_rectangle((field_left, field_top, field_right, field_bottom), radius=2 * SCALE,
                           fill=SURFACE, outline=ACCENT)

    field = Image.new("RGB", (int(field_right - field_left) - 2 * SCALE, int(field_bottom - field_top) - 2 * SCALE), SURFACE)
    field_draw = ImageDraw.Draw(field)
    length = field_draw.textlength(RESULT, font=body)
    field_draw.rectangle((9 * SCALE, 2 * SCALE, 9 * SCALE + length, field.height - 2 * SCALE), fill=SELECTION)
    field_draw.text((9 * SCALE, field.height / 2), RESULT, font=body, fill=TEXT, anchor="lm")
    image.paste(field, (int(field_left) + SCALE, int(field_top) + SCALE))

    # The padlock and the cross; nothing else has a permanent place on the strip.
    lock = field_right + (BUTTON / 2) * SCALE
    draw.arc((lock - 3.5 * SCALE, middle - 8 * SCALE, lock + 3.5 * SCALE, middle - 1 * SCALE),
             180, 360, fill=MUTED, width=SCALE)
    draw.rounded_rectangle((lock - 5 * SCALE, middle - 2 * SCALE, lock + 5 * SCALE, middle + 6 * SCALE),
                           radius=2 * SCALE, outline=MUTED, width=SCALE)

    close = field_right + (BUTTON + BUTTON / 2) * SCALE
    draw.line((close - 4 * SCALE, middle - 4 * SCALE, close + 4 * SCALE, middle + 4 * SCALE), fill=MUTED, width=SCALE)
    draw.line((close + 4 * SCALE, middle - 4 * SCALE, close - 4 * SCALE, middle + 4 * SCALE), fill=MUTED, width=SCALE)

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    image.save(OUTPUT, optimize=True)
    print(f"{OUTPUT} written ({OUTPUT.stat().st_size} bytes)")

    docked()


if __name__ == "__main__":
    main()
