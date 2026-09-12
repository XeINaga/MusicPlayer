"""Generate the multi-size app icon.

Design: a full-bleed rounded square with a vertical purple gradient (the
app's accent color #8F0FC2 family), a white beamed eighth-note pair, a soft
drop shadow under the note and a gentle top glow for depth.

Everything is drawn at 4x supersampling (4096px) and downscaled with Lanczos,
so the small frames (16/24/32) stay smooth instead of getting the jaggies.
"""
import math
import os

from PIL import Image, ImageDraw, ImageFilter

SIZES = [16, 24, 32, 48, 64, 128, 256]
S = 4096                       # supersampled master canvas
U = S / 1024.0                 # design units live on a 1024 grid

# Gradient stops: lighter violet on top -> deep purple at the bottom.
TOP = (183, 92, 255)           # #B75CFF
BOTTOM = (104, 6, 190)         # #6806BE
TILE_RADIUS = 230              # Win11-style full-bleed rounded square


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def make_tile():
    """Rounded square with a vertical gradient + soft top glow."""
    tile = Image.new("RGBA", (S, S))
    d = ImageDraw.Draw(tile)
    for y in range(S):
        d.line([(0, y), (S, y)], fill=lerp(TOP, BOTTOM, y / (S - 1)) + (255,))

    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    dg = ImageDraw.Draw(glow)
    dg.ellipse([S * 0.10, -S * 0.28, S * 0.90, S * 0.42], fill=(255, 255, 255, 70))
    glow = glow.filter(ImageFilter.GaussianBlur(S * 0.07))
    tile = Image.alpha_composite(tile, glow)

    # Clip everything to the rounded-square silhouette.
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, S - 1, S - 1], radius=int(TILE_RADIUS * U), fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(tile, (0, 0), mask)

    # Hairline inner highlight tracing the tile edge (very subtle).
    edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    de = ImageDraw.Draw(edge)
    de.rounded_rectangle([int(8 * U), int(8 * U), S - 1 - int(8 * U), S - 1 - int(8 * U)],
                         radius=int((TILE_RADIUS - 8) * U),
                         outline=(255, 255, 255, 46), width=int(7 * U))
    edge.putalpha(Image.composite(edge.getchannel("A"), Image.new("L", (S, S), 0), mask))
    return Image.alpha_composite(out, edge)


def rotated_ellipse(draw, cx, cy, rx, ry, deg, fill):
    ang = math.radians(deg)
    cos, sin = math.cos(ang), math.sin(ang)
    pts = []
    for i in range(180):
        a = 2 * math.pi * i / 180
        x, y = rx * math.cos(a), ry * math.sin(a)
        pts.append((cx + x * cos - y * sin, cy + x * sin + y * cos))
    draw.polygon(pts, fill=fill)


def make_note():
    """White beamed eighth-note pair, centered on the canvas."""
    note = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    dn = ImageDraw.Draw(note)
    white = (255, 255, 255, 255)

    # Stems; the beam slants up to the right, so the right stem starts higher.
    dn.rounded_rectangle([392 * U, 330 * U, 434 * U, 706 * U],
                         radius=int(16 * U), fill=white)
    dn.rounded_rectangle([678 * U, 272 * U, 720 * U, 654 * U],
                         radius=int(16 * U), fill=white)

    # Beam connecting the stem tops.
    dn.polygon([(392 * U, 316 * U), (720 * U, 254 * U),
                (720 * U, 344 * U), (392 * U, 406 * U)], fill=white)

    # Slightly tilted note heads.
    rotated_ellipse(dn, 336 * U, 694 * U, 102 * U, 76 * U, -20, white)
    rotated_ellipse(dn, 626 * U, 642 * U, 102 * U, 76 * U, -20, white)
    return note


def make():
    icon = make_tile()

    # Soft drop shadow under the note, offset down-right.
    note = make_note()
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    black = Image.new("RGBA", (S, S), (10, 0, 20, 130))
    shadow.paste(black, (int(6 * U), int(14 * U)), note.getchannel("A"))
    shadow = shadow.filter(ImageFilter.GaussianBlur(S * 0.012))
    icon = Image.alpha_composite(icon, shadow)
    icon = Image.alpha_composite(icon, note)
    return icon


def main():
    master = make()
    frames = [master.resize((s, s), Image.LANCZOS) for s in SIZES]

    out = os.path.join("Assets", "AppIcon.ico")
    frames[0].save(out, sizes=[(s, s) for s in SIZES],
                   append_images=frames[1:], format="ICO")
    print("wrote", out)

    # Contact sheet for quick visual inspection (not shipped).
    sheet = Image.new("RGBA", (4 * 300 + 40, 340), (40, 40, 48, 255))
    x = 20
    for s in (256, 64, 32, 16):
        f = master.resize((s, s), Image.LANCZOS)
        sheet.paste(f, (x, 20), f)
        x += s + 24
    sheet.save(os.path.join("Assets", "icon_preview.png"))
    print("wrote", os.path.join("Assets", "icon_preview.png"))


if __name__ == "__main__":
    main()
