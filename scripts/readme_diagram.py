"""Render docs/images/how-it-works.png for the README: the real chat panel on the left, the
flow (you -> Claude -> VibeModel API inside Revit -> model -> back to you) on the right.

    python -I scripts/readme_diagram.py

Pillow only; fonts from C:\\Windows\\Fonts (Segoe UI + Consolas), falls back to Pillow's default."""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
IMG = os.path.join(HERE, "..", "docs", "images")
PANE = os.path.join(IMG, "chat-table-sources.png")
OUT = os.path.join(IMG, "how-it-works.png")

S = 2  # supersampling: drawn at 2x, saved at 1x width 960
W, H = 960 * S, 620 * S
BG = (24, 25, 28)
CARD = (34, 36, 41)
EDGE = (62, 65, 72)
TXT = (232, 233, 236)
MUTED = (150, 154, 162)
ACCENT = (82, 196, 164)   # the pane's green
BLUE = (96, 150, 232)


def font(name, size, bold=False):
    cands = {
        "ui": ["segoeuib.ttf" if bold else "segoeui.ttf", "arialbd.ttf" if bold else "arial.ttf"],
        "mono": ["consola.ttf", "cour.ttf"],
    }[name]
    for c in cands:
        p = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", c)
        if os.path.exists(p):
            return ImageFont.truetype(p, size * S)
    return ImageFont.load_default()


F_T = font("ui", 15, True)
F_B = font("ui", 13)
F_S = font("ui", 11)
F_M = font("mono", 12)

im = Image.new("RGB", (W, H), BG)
d = ImageDraw.Draw(im)

# --- left: the real panel ---------------------------------------------------------------
pane = Image.open(PANE).convert("RGB")
ph = H - 60 * S
pw = int(pane.width * ph / pane.height)
pane = pane.resize((pw, ph), Image.LANCZOS)
px, py = 30 * S, 30 * S
d.rounded_rectangle([px - 2 * S, py - 2 * S, px + pw + 2 * S, py + ph + 2 * S], radius=10 * S, fill=EDGE)
im.paste(pane, (px, py))
d.text((px, py + ph + 10 * S), "the chat panel, docked in Revit", font=F_S, fill=MUTED)

# --- right: the flow ---------------------------------------------------------------------
x0 = px + pw + 60 * S
x1 = W - 30 * S
bw = x1 - x0
steps = [
    ("You", 'type what you want:  "give me a table of the 5 longest walls"', None, TXT),
    ("Claude", "reads the model state, decides which commands answer it", None, BLUE),
    ("VibeModel  ·  HTTP API inside Revit", "127.0.0.1:18884, one command per call, each on Revit's main thread in a transaction",
     "list walls   params 1728711   schedule walls --fields Type,Length", ACCENT),
    ("Your model", "one answer  ·  one Ctrl+Z  ·  one journal line  ·  sources you can click", None, TXT),
]
bh = 100 * S
gap = 34 * S
y = py + 8 * S
boxes = []
for title, body, code, col in steps:
    h = bh + (26 * S if code else 0)
    d.rounded_rectangle([x0, y, x1, y + h], radius=12 * S, fill=CARD, outline=EDGE, width=2 * S)
    d.rounded_rectangle([x0, y, x0 + 6 * S, y + h], radius=3 * S, fill=col)
    d.text((x0 + 22 * S, y + 14 * S), title, font=F_T, fill=col)
    # wrap body to the box width
    words, line, lines = body.split(), "", []
    for w_ in words:
        t = (line + " " + w_).strip()
        if d.textlength(t, font=F_B) > bw - 44 * S:
            lines.append(line); line = w_
        else:
            line = t
    lines.append(line)
    ty = y + 42 * S
    for ln in lines:
        d.text((x0 + 22 * S, ty), ln, font=F_B, fill=TXT)
        ty += 20 * S
    if code:
        d.rounded_rectangle([x0 + 22 * S, ty + 4 * S, x1 - 22 * S, ty + 28 * S], radius=6 * S, fill=(44, 47, 54))
        d.text((x0 + 32 * S, ty + 8 * S), code, font=F_M, fill=(200, 220, 210))
    boxes.append((y, y + h))
    y += h + gap

# arrows between boxes (down the middle) and one return arrow on the right edge
cx = x0 + bw // 2
for (a0, a1), (b0, b1) in zip(boxes, boxes[1:]):
    d.line([cx, a1 + 4 * S, cx, b0 - 8 * S], fill=MUTED, width=3 * S)
    d.polygon([(cx - 7 * S, b0 - 14 * S), (cx + 7 * S, b0 - 14 * S), (cx, b0 - 2 * S)], fill=MUTED)
# return arrow: last box -> panel
ly = boxes[-1][0] + (boxes[-1][1] - boxes[-1][0]) // 2
d.line([x0 - 4 * S, ly, px + pw + 24 * S, ly], fill=ACCENT, width=3 * S)
d.polygon([(px + pw + 24 * S, ly - 7 * S), (px + pw + 24 * S, ly + 7 * S), (px + pw + 10 * S, ly)], fill=ACCENT)
d.text((px + pw + 14 * S, ly + 10 * S), "answer", font=F_S, fill=ACCENT)

im = im.resize((W // S, H // S), Image.LANCZOS)
im.save(OUT, optimize=True)
print(OUT, im.size, os.path.getsize(OUT) // 1024, "KB")
