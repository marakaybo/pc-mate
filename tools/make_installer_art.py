"""Картинки для мастера установки Inno Setup.

Inno Setup ждёт BMP: большая панель слева (164×314 в 100 % масштабе, плюс
варианты для HiDPI) и маленький значок в шапке (55×58).

Запуск:  python tools/make_installer_art.py
"""
from __future__ import annotations

import os
import sys

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from make_icon import draw_glyph, BG_TOP, BG_BOTTOM, ACCENT  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "agent", "installer")


def gradient(width: int, height: int) -> Image.Image:
    """Диагональный градиент — фон панели мастера."""
    img = Image.new("RGB", (width, height))
    px = img.load()
    for y in range(height):
        for x in range(width):
            t = (x / max(1, width - 1) * 0.35) + (y / max(1, height - 1) * 0.65)
            px[x, y] = (
                int(BG_TOP[0] + (BG_BOTTOM[0] - BG_TOP[0]) * t),
                int(BG_TOP[1] + (BG_BOTTOM[1] - BG_TOP[1]) * t),
                int(BG_TOP[2] + (BG_BOTTOM[2] - BG_TOP[2]) * t),
            )
    return img


def wizard_image(width: int, height: int) -> Image.Image:
    img = gradient(width, height)
    scale = width / 164.0

    # Монитор с кнопкой питания в верхней трети панели.
    glyph_size = int(96 * scale)
    glyph = Image.new("RGBA", (glyph_size, glyph_size), (0, 0, 0, 0))
    draw_glyph(glyph)
    img.paste(glyph, (int((width - glyph_size) / 2), int(42 * scale)), glyph)

    d = ImageDraw.Draw(img)

    # Подпись: рисуем блоками, чтобы не зависеть от наличия шрифтов в системе.
    base_y = int(170 * scale)
    d.rectangle(
        [int(24 * scale), base_y, int(140 * scale), base_y + int(3 * scale)],
        fill=ACCENT,
    )

    # Три «строки» текста-заглушки разной длины — читается как аккуратный блок.
    for index, length in enumerate((116, 92, 70)):
        y = base_y + int((18 + index * 13) * scale)
        d.rectangle(
            [int(24 * scale), y, int((24 + length) * scale), y + int(6 * scale)],
            fill=(255, 255, 255, 40),
        )

    return img


def small_image(size: int) -> Image.Image:
    img = gradient(size, size)
    glyph = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw_glyph(glyph)
    img.paste(glyph, (0, 0), glyph)
    return img


def main() -> None:
    os.makedirs(OUT, exist_ok=True)

    targets = [
        ("wizard-image.bmp", wizard_image(164, 314)),
        ("wizard-image@2x.bmp", wizard_image(328, 628)),
        ("wizard-small.bmp", small_image(55)),
        ("wizard-small@2x.bmp", small_image(110)),
    ]

    for name, image in targets:
        path = os.path.join(OUT, name)
        image.convert("RGB").save(path, format="BMP")
        print("bmp  ->", path)


if __name__ == "__main__":
    main()
