"""Генерирует иконку PC MATE (ico + png) — монитор с кнопкой питания.

Запуск:  python tools/make_icon.py
Результат: agent/src/PcMate.Agent.Tray/pcmate.ico, mobile/assets/icon.png и др.
"""
from __future__ import annotations

import math
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

BG_TOP = (33, 118, 255)
BG_BOTTOM = (16, 72, 190)
FG = (255, 255, 255)
ACCENT = (86, 230, 160)


def rounded_gradient(size: int, radius_ratio: float = 0.22) -> Image.Image:
    """Скруглённый квадрат с вертикальным градиентом."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    grad = Image.new("RGBA", (size, size))
    px = grad.load()
    for y in range(size):
        t = y / max(1, size - 1)
        r = int(BG_TOP[0] + (BG_BOTTOM[0] - BG_TOP[0]) * t)
        g = int(BG_TOP[1] + (BG_BOTTOM[1] - BG_TOP[1]) * t)
        b = int(BG_TOP[2] + (BG_BOTTOM[2] - BG_TOP[2]) * t)
        for x in range(size):
            px[x, y] = (r, g, b, 255)

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=int(size * radius_ratio), fill=255
    )
    img.paste(grad, (0, 0), mask)
    return img


def draw_glyph(img: Image.Image) -> None:
    """Монитор с символом питания на экране."""
    size = img.size[0]
    d = ImageDraw.Draw(img)
    s = size / 100.0

    # Корпус монитора
    left, top, right, bottom = 18 * s, 24 * s, 82 * s, 64 * s
    d.rounded_rectangle([left, top, right, bottom], radius=6 * s, fill=FG)

    # Подставка
    d.rounded_rectangle([46 * s, 64 * s, 54 * s, 72 * s], radius=1 * s, fill=FG)
    d.rounded_rectangle([34 * s, 72 * s, 66 * s, 78 * s], radius=3 * s, fill=FG)

    # Экран
    inner = [left + 5 * s, top + 5 * s, right - 5 * s, bottom - 5 * s]
    d.rounded_rectangle(inner, radius=3 * s, fill=BG_BOTTOM)

    # Символ питания: дуга + вертикальная черта
    cx, cy = (left + right) / 2, (top + bottom) / 2
    r = 9 * s
    width = max(2, int(3 * s))
    d.arc([cx - r, cy - r, cx + r, cy + r], start=-60, end=240, fill=ACCENT, width=width)
    d.line([cx, cy - r - 2 * s, cx, cy - 1 * s], fill=ACCENT, width=width)


def make(size: int) -> Image.Image:
    img = rounded_gradient(size)
    draw_glyph(img)
    return img


def main() -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [make(s) for s in sizes]

    ico_path = os.path.join(ROOT, "agent", "src", "PcMate.Agent.Tray", "pcmate.ico")
    os.makedirs(os.path.dirname(ico_path), exist_ok=True)
    images[-1].save(ico_path, format="ICO", sizes=[(s, s) for s in sizes])
    print("ico  ->", ico_path)

    targets = [
        (os.path.join(ROOT, "mobile", "assets", "icon.png"), 1024),
        (os.path.join(ROOT, "mobile", "assets", "adaptive-icon.png"), 1024),
        (os.path.join(ROOT, "mobile", "assets", "favicon.png"), 64),
        (os.path.join(ROOT, "mobile", "assets", "splash-icon.png"), 512),
        (os.path.join(ROOT, "docs", "logo.png"), 256),
    ]
    for path, size in targets:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        make(size).save(path, format="PNG")
        print("png  ->", path)


if __name__ == "__main__":
    main()
