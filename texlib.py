"""Gemeinsame Bausteine fuer die Waffentexturen.

Alle Texturen des Toolkits sind 512er-Atlanten mit vier Vierteln, passend zu den
UV-Regionen in ndmesh.py:

    oben links   shroud     Laufmantel / Handschutz
    oben rechts  receiver   Gehaeuse
    unten links  stock      Griff und Schaft
    unten rechts detail     Kleinteile

Jede Flaeche bekommt ihre Region gekachelt, nicht aufgespannt - deshalb tragen
die Viertel Muster, kein Layout.
"""

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

S = 512
H = S // 2


def rng(seed):
    return np.random.default_rng(seed)


def grain(r, w, h, scale, amp):
    """Weiches Rauschen als Grundstruktur."""
    small = r.random((max(2, h // scale), max(2, w // scale)))
    img = Image.fromarray((small * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC)
    a = np.asarray(img, dtype=np.float32) / 255.0
    return (a - a.mean()) * amp


def base(r, w, h, rgb, rough=0.06, scale=8):
    g = grain(r, w, h, scale, rough) + grain(r, w, h, 2, rough * 0.5)
    out = np.zeros((h, w, 3), np.float32)
    for i in range(3):
        out[..., i] = np.clip(rgb[i] / 255.0 + g, 0, 1)
    return out


def scratches(r, arr, n, bright, length=60):
    h, w, _ = arr.shape
    img = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(img)
    for _ in range(n):
        x, y = r.integers(0, w), r.integers(0, h)
        ang = r.random() * np.pi
        ln = length * (0.3 + r.random())
        d.line([(x, y), (x + np.cos(ang) * ln, y + np.sin(ang) * ln)],
               fill=int(255 * (0.3 + 0.7 * r.random())), width=1)
    img = img.filter(ImageFilter.GaussianBlur(0.6))
    m = np.asarray(img, np.float32)[..., None] / 255.0
    return np.clip(arr + m * bright, 0, 1)


def bakelite(r, w, h, rgb=(48, 33, 29), swirl=0.045):
    """Dunkles Bakelit - Griffschalen und Schaft an MG42 und Kar98-Klassen.

    Wichtig ist der Unterschied zu Holz: Holz hat gerichtete, lange Fasern, und
    genau die liessen den alten Griff wie ein Stueck Brett aussehen. Bakelit ist
    gepresstes Kunstharz mit fleckiger, ungerichteter Marmorierung und einem
    deutlich dunkleren, ins Rote gehenden Grundton.
    """
    out = base(r, w, h, rgb, 0.035, scale=14)
    marble = (grain(r, w, h, 22, swirl) + grain(r, w, h, 9, swirl * 0.55)
              + grain(r, w, h, 3, swirl * 0.25))
    # Nur Helligkeit modulieren, den Farbton aber leicht ins Rote ziehen -
    # gleichmaessige Multiplikation wuerde grau wirken.
    out = np.clip(out + marble[..., None] * np.array([1.0, 0.74, 0.66]), 0, 1)
    out = scratches(r, out, 26, 0.06, 34)
    return out


def steel(r, w, h, rgb=(58, 60, 66), scratch_n=90):
    out = base(r, w, h, rgb, 0.05, scale=6)
    return scratches(r, out, scratch_n, 0.18, 55)


def rivets(r, arr, rows, cols, y0=40, dy=84, x0=24, dx=28, radius=3, bright=0.16):
    h, w, _ = arr.shape
    img = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(img)
    for row in range(rows):
        yy = y0 + row * dy
        for col in range(cols):
            xx = x0 + col * dx
            d.ellipse([xx - radius, yy - radius, xx + radius, yy + radius], fill=200)
    v = np.asarray(img.filter(ImageFilter.GaussianBlur(1.0)), np.float32)[..., None] / 255.0
    return np.clip(arr + v * bright, 0, 1)


def perforation(r, arr, step=30, radius=9, depth=0.88, rim=0.10):
    """Kuehlbohrungen im Laufmantel, versetzt in Reihen."""
    h, w, _ = arr.shape
    holes = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(holes)
    for row in range(0, h // step + 1):
        for col in range(0, w // step + 1):
            cx = col * step + step // 2 + (step // 2 if row % 2 else 0)
            cy = row * step + step // 2
            d.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=255)
    hb = np.asarray(holes.filter(ImageFilter.GaussianBlur(1.2)), np.float32)[..., None] / 255.0
    rm = np.asarray(holes.filter(ImageFilter.GaussianBlur(3.0)), np.float32)[..., None] / 255.0
    return arr * (1.0 - hb * depth) + rm * rim


def save_atlas(quads, path):
    """quads = dict mit shroud/receiver/stock/detail, je (H, H, 3) in 0..1."""
    tex = np.zeros((S, S, 3), np.float32)
    tex[0:H, 0:H] = quads["shroud"]
    tex[0:H, H:S] = quads["receiver"]
    tex[H:S, 0:H] = quads["stock"]
    tex[H:S, H:S] = quads["detail"]
    img = Image.fromarray((np.clip(tex, 0, 1) * 255).astype(np.uint8))
    img.save(path)
    return img


def mean_rgb(a):
    return tuple(int(round(x)) for x in (np.asarray(a).reshape(-1, 3).mean(axis=0) * 255))


# ------------------------------------------------------------- Normal Maps

def height_to_normal(height, strength=3.2):
    """Sobel auf einer Hoehenkarte -> Tangentenraum-Normale als RGB-Bild.

    Die Viertelgrenzen werden bewusst nicht geglaettet, damit sich benachbarte
    Bauteile nicht ineinander ausbluten.
    """
    gx = np.zeros_like(height)
    gy = np.zeros_like(height)
    gx[:, 1:-1] = height[:, 2:] - height[:, :-2]
    gy[1:-1, :] = height[2:, :] - height[:-2, :]
    nx = -gx * strength
    ny = -gy * strength
    nz = np.ones_like(height)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    nx, ny, nz = nx / ln, ny / ln, nz / ln
    rgb = np.dstack([(nx * 0.5 + 0.5) * 255,
                     (ny * 0.5 + 0.5) * 255,
                     (nz * 0.5 + 0.5) * 255]).astype(np.uint8)
    return Image.fromarray(rgb, "RGB")


def height_perforation(r, w, h, step=30, radius=9):
    holes = Image.new("L", (w, h), 128)
    d = ImageDraw.Draw(holes)
    for row in range(0, h // step + 1):
        for col in range(0, w // step + 1):
            cx = col * step + step // 2 + (step // 2 if row % 2 else 0)
            cy = row * step + step // 2
            d.ellipse([cx - radius - 2, cy - radius - 2,
                       cx + radius + 2, cy + radius + 2], fill=190)   # Rand hoch
            d.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=20)
    a = np.asarray(holes.filter(ImageFilter.GaussianBlur(1.4)), np.float32) / 255.0
    return a + grain(r, w, h, 6, 0.04)


def height_rivets(r, w, h, rows=3, cols=9, seams=(12, 96, 180, 244)):
    rec = Image.new("L", (w, h), 128)
    d = ImageDraw.Draw(rec)
    for row in range(rows):
        yy = 40 + row * 84
        for col in range(cols):
            xx = 24 + col * 28
            d.ellipse([xx - 4, yy - 4, xx + 4, yy + 4], fill=215)
    for yy in seams:
        d.rectangle([0, yy, w, yy + 3], fill=70)
    a = np.asarray(rec.filter(ImageFilter.GaussianBlur(1.0)), np.float32) / 255.0
    return a + grain(r, w, h, 5, 0.05)


def height_scratches(r, w, h, n=140, length=30):
    det = Image.new("L", (w, h), 128)
    d = ImageDraw.Draw(det)
    for _ in range(n):
        x, y = r.integers(0, w), r.integers(0, h)
        ang = r.random() * np.pi
        ln = length * (0.3 + r.random())
        d.line([(x, y), (x + np.cos(ang) * ln, y + np.sin(ang) * ln)],
               fill=int(150 + 80 * r.random()), width=1)
    a = np.asarray(det.filter(ImageFilter.GaussianBlur(0.7)), np.float32) / 255.0
    return a + grain(r, w, h, 4, 0.05)


def height_bakelite(r, w, h):
    return 0.5 + grain(r, w, h, 12, 0.10) + grain(r, w, h, 4, 0.05)


def height_checker(r, w, h, pitch=14, depth=0.16):
    """Fischhaut - die gekreuzte Riffelung auf Griffschalen."""
    yy, xx = np.mgrid[0:h, 0:w]
    a = 0.5 + depth * 0.5 * (np.sin((xx + yy) * np.pi / pitch)
                             * np.sin((xx - yy) * np.pi / pitch))
    return a + grain(r, w, h, 6, 0.03)


def save_height_atlas(quads, path, strength=3.2):
    hgt = np.full((S, S), 0.5, np.float32)
    hgt[0:H, 0:H] = quads["shroud"]
    hgt[0:H, H:S] = quads["receiver"]
    hgt[H:S, 0:H] = quads["stock"]
    hgt[H:S, H:S] = quads["detail"]
    img = height_to_normal(np.clip(hgt, 0.0, 1.0), strength)
    img.save(path)
    return img
