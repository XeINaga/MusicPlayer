"""Generate a multi-size app icon (music note on a rounded green tile)."""
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]
GREEN = (49, 194, 124, 255)      # #31c27c
GREEN2 = (31, 158, 158, 255)     # #1f9e9e
WHITE = (255, 255, 255, 255)
BG = (12, 12, 18, 255)           # #0c0c12  (matches app chrome)


def rounded_tile(d, s):
    """Draw the app-chrome background with a rounded green accent bar."""
    # base
    d.rectangle([0, 0, s, s], fill=BG)
    # rounded green tile inset
    m = int(s * 0.10)
    r = int(s * 0.22)
    d.rounded_rectangle([m, m, s - m, s - m], radius=r, fill=GREEN)
    return d


def music_note(d, s):
    """Draw a simple eighth-note (notehead + stem + flag) in white."""
    cx = s * 0.40
    head_r = s * 0.13
    stem_x = cx + head_r * 0.85
    # note head (slightly tilted ellipse)
    d.ellipse([cx - head_r, s * 0.62 - head_r,
               cx + head_r, s * 0.62 + head_r], fill=WHITE)
    # stem
    sw = max(2, int(s * 0.035))
    d.rectangle([stem_x - sw // 2, s * 0.20, stem_x + sw // 2, s * 0.62], fill=WHITE)
    # flag
    fw = s * 0.20
    fh = s * 0.16
    d.pieslice([stem_x - fw * 0.2, s * 0.20 - fh * 0.2,
                stem_x + fw, s * 0.20 + fh], start=0, end=90, fill=WHITE)
    return d


def make(size):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    rounded_tile(d, size)
    music_note(d, size)
    return img


# Build frames
frames = [(make(s), None) for s in SIZES]
out = "Assets/AppIcon.ico"
frames[0][0].save(out, sizes=[(s, s) for s in SIZES],
                  append_images=[f[0] for f in frames[1:]],
                  format="ICO")
print("wrote", out)
