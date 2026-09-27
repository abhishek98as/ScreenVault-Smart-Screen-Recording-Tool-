"""Generates ScreenVault's brand assets (app icon + installer artwork).

Usage (from the repository root):
    python tools/branding/generate_assets.py

Requires Pillow (pip install pillow). Outputs:
    src/ScreenVault.App/Resources/app.ico        multi-size app icon (16-256 px)
    installer/assets/ScreenVault.ico             same icon for Setup/Uninstall
    installer/assets/wizard-*.png                welcome/finish banner (light + dark, several DPIs)
    installer/assets/wizard-small-*.png          header logo for inner wizard pages (several DPIs)

The palette matches the app's design tokens (src/ScreenVault.App/UI/Theming/Palette.cs).
"""

from __future__ import annotations

import os
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[2]
APP_ICON = ROOT / "src" / "ScreenVault.App" / "Resources" / "app.ico"
INSTALLER_ASSETS = ROOT / "installer" / "assets"

ACCENT_LIGHT = (124, 124, 242)   # top-left of the icon gradient
ACCENT_DARK = (70, 70, 196)      # bottom-right of the icon gradient
RECORD_RED = (239, 68, 68)
WHITE = (255, 255, 255)

ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
# Base sizes are for 100% DPI; Inno Setup picks the closest file for the current scaling.
WIZARD_SCALES = [1.0, 1.25, 1.5, 1.75, 2.0, 2.5]
WIZARD_BASE = (164, 314)
SMALL_SIZES = [55, 64, 83, 92, 110, 138]


def lerp(a: tuple[int, ...], b: tuple[int, ...], t: float) -> tuple[int, ...]:
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def diagonal_gradient(size: int, start: tuple[int, int, int], end: tuple[int, int, int]) -> Image.Image:
    small = Image.new("RGB", (64, 64))
    px = small.load()
    for y in range(64):
        for x in range(64):
            px[x, y] = lerp(start, end, (x + y) / 126)
    return small.resize((size, size), Image.BICUBIC)


def vertical_gradient(width: int, height: int, stops: list[tuple[float, tuple[int, int, int]]]) -> Image.Image:
    img = Image.new("RGB", (1, height))
    px = img.load()
    for y in range(height):
        t = y / max(1, height - 1)
        for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
            if t0 <= t <= t1:
                px[0, y] = lerp(c0, c1, (t - t0) / max(1e-6, t1 - t0))
                break
    return img.resize((width, height), Image.BICUBIC)


def rounded_mask(size: tuple[int, int], radius: float) -> Image.Image:
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=radius, fill=255)
    return mask


def render_logo(size: int) -> Image.Image:
    """The app mark: indigo tile, white screen, red record dot. Rendered 8x and downsampled."""
    ss = 8
    s = size * ss
    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    margin = 0.03 * s if size >= 32 else 0.0
    tile = int(s - 2 * margin)
    gradient = diagonal_gradient(tile, ACCENT_LIGHT, ACCENT_DARK).convert("RGBA")
    canvas.paste(gradient, (int(margin), int(margin)), rounded_mask((tile, tile), tile * 0.23))

    draw = ImageDraw.Draw(canvas)
    small = size < 32
    screen_w = s * (0.64 if small else 0.60)
    screen_h = s * (0.46 if small else 0.40)
    cx = s / 2
    cy = s * (0.47 if small else 0.44)
    screen = (cx - screen_w / 2, cy - screen_h / 2, cx + screen_w / 2, cy + screen_h / 2)
    draw.rounded_rectangle(screen, radius=screen_h * 0.16, fill=WHITE)

    if not small:
        stand_w = s * 0.24
        stand_h = s * 0.055
        top = screen[3] + s * 0.07
        draw.rounded_rectangle((cx - stand_w / 2, top, cx + stand_w / 2, top + stand_h), radius=stand_h / 2, fill=WHITE)

    dot = s * (0.24 if small else 0.19)
    if not small:
        ring = dot * 1.55
        # Opaque tint (ImageDraw replaces pixels rather than blending, so no alpha here).
        draw.ellipse((cx - ring / 2, cy - ring / 2, cx + ring / 2, cy + ring / 2), fill=lerp(WHITE, RECORD_RED, 0.18))
    draw.ellipse((cx - dot / 2, cy - dot / 2, cx + dot / 2, cy + dot / 2), fill=RECORD_RED)

    return canvas.resize((size, size), Image.LANCZOS)


def write_icon() -> None:
    frames = [render_logo(size) for size in ICON_SIZES]
    largest = frames[-1]
    APP_ICON.parent.mkdir(parents=True, exist_ok=True)
    largest.save(APP_ICON, format="ICO", sizes=[(s, s) for s in ICON_SIZES], append_images=frames[:-1])
    (INSTALLER_ASSETS / "ScreenVault.ico").write_bytes(APP_ICON.read_bytes())
    largest.save(ROOT / "tools" / "branding" / "logo-256.png")


def find_font(bold: bool) -> str | None:
    candidates = [
        "C:/Windows/Fonts/seguisb.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf" if bold else "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" if bold else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    ]
    return next((c for c in candidates if os.path.exists(c)), None)


def render_wizard_image(width: int, height: int, dark: bool) -> Image.Image:
    stops = (
        [(0.0, (52, 52, 140)), (0.55, (30, 28, 82)), (1.0, (18, 17, 44))]
        if dark
        else [(0.0, (104, 104, 232)), (0.55, (76, 70, 200)), (1.0, (46, 40, 128))]
    )
    img = vertical_gradient(width, height, stops).convert("RGBA")

    # Soft glow behind the logo.
    glow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    cx, cy = width / 2, height * 0.33
    r = width * 0.62
    gd.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(255, 255, 255, 40 if not dark else 26))
    glow = glow.filter(ImageFilter.GaussianBlur(width * 0.18))
    img = Image.alpha_composite(img, glow)

    # "Recording waves": thin concentric rings.
    rings = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    rd = ImageDraw.Draw(rings)
    line = max(1, round(width / 164))
    for i, factor in enumerate((0.36, 0.52, 0.70, 0.90)):
        rr = width * factor
        rd.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), outline=(255, 255, 255, 46 - i * 9), width=line)
    img = Image.alpha_composite(img, rings)

    # Logo with a soft drop shadow.
    logo_size = round(width * 0.46)
    logo = render_logo(logo_size)
    shadow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    alpha = logo.getchannel("A").point(lambda a: int(a * 0.45))
    shadow_logo = Image.new("RGBA", logo.size, (8, 6, 30, 255))
    shadow_logo.putalpha(alpha)
    lx = round(cx - logo_size / 2)
    ly = round(cy - logo_size / 2)
    shadow.paste(shadow_logo, (lx, ly + round(width * 0.03)), shadow_logo)
    shadow = shadow.filter(ImageFilter.GaussianBlur(width * 0.035))
    img = Image.alpha_composite(img, shadow)
    img.paste(logo, (lx, ly), logo)

    # Wordmark and tagline.
    draw = ImageDraw.Draw(img)
    bold = find_font(True)
    regular = find_font(False)
    if bold and regular:
        title_font = ImageFont.truetype(bold, round(width * 0.125))
        tag_font = ImageFont.truetype(regular, round(width * 0.07))
        title = "ScreenVault"
        tw = draw.textlength(title, font=title_font)
        ty = height * 0.60
        draw.text((cx - tw / 2, ty), title, font=title_font, fill=(255, 255, 255, 255))
        for i, line_text in enumerate(("Always on.", "Crash-proof.", "Private by design.")):
            lw = draw.textlength(line_text, font=tag_font)
            draw.text((cx - lw / 2, ty + width * (0.20 + i * 0.105)), line_text, font=tag_font, fill=(255, 255, 255, 190))

    return img.convert("RGB")


def render_small_image(size: int) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    logo = render_logo(round(size * 0.84))
    offset = (size - logo.width) // 2
    canvas.paste(logo, (offset, offset), logo)
    return canvas


def write_installer_images() -> None:
    INSTALLER_ASSETS.mkdir(parents=True, exist_ok=True)
    for scale in WIZARD_SCALES:
        w = round(WIZARD_BASE[0] * scale)
        h = round(WIZARD_BASE[1] * scale)
        pct = round(scale * 100)
        render_wizard_image(w, h, dark=False).save(INSTALLER_ASSETS / f"wizard-{pct}.png", optimize=True)
        render_wizard_image(w, h, dark=True).save(INSTALLER_ASSETS / f"wizard-dark-{pct}.png", optimize=True)
    for size in SMALL_SIZES:
        render_small_image(size).save(INSTALLER_ASSETS / f"wizard-small-{size}.png", optimize=True)


if __name__ == "__main__":
    write_icon()
    write_installer_images()
    print("Brand assets written.")
