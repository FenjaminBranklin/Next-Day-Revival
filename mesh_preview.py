"""Software-Renderer fuer .ndmesh - Kontrollblick vor dem Einbau ins Spiel.

Orthografische Projektion mit Z-Buffer und Lambert-Shading. Drei Ansichten
nebeneinander: Seite, Draufsicht, Dreiviertel.
"""

import struct
import os
import sys

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

SRC = os.path.join(HIER, "assets", "mg42.ndmesh")
OUT = os.path.join(HIER, "assets", "mg42_preview.png")

W, H = 1280, 340
CULL = True    # zum Pruefen abschaltbar
LIGHT = np.array([0.4, 0.55, 0.72])
LIGHT = LIGHT / np.linalg.norm(LIGHT)


def load(path):
    with open(path, "rb") as f:
        assert f.read(4) == b"NDMS"
        struct.unpack("<i", f.read(4))
        n = struct.unpack("<i", f.read(4))[0]
        v = np.frombuffer(f.read(12 * n), dtype="<f4").reshape(n, 3).copy()
        nm = np.frombuffer(f.read(12 * n), dtype="<f4").reshape(n, 3).copy()
        f.read(8 * n)  # uvs
        m = struct.unpack("<i", f.read(4))[0]
        idx = np.frombuffer(f.read(4 * m), dtype="<i4").reshape(-1, 3).copy()
    return v, nm, idx


def load_full(path):
    """Wie load(), liefert zusaetzlich die UVs - fuer den texturierten Icon-Render."""
    with open(path, "rb") as f:
        assert f.read(4) == b"NDMS"
        struct.unpack("<i", f.read(4))
        n = struct.unpack("<i", f.read(4))[0]
        v = np.frombuffer(f.read(12 * n), dtype="<f4").reshape(n, 3).copy()
        nm = np.frombuffer(f.read(12 * n), dtype="<f4").reshape(n, 3).copy()
        uv = np.frombuffer(f.read(8 * n), dtype="<f4").reshape(n, 2).copy()
        m = struct.unpack("<i", f.read(4))[0]
        idx = np.frombuffer(f.read(4 * m), dtype="<i4").reshape(-1, 3).copy()
    return v, nm, idx, uv


def rot(yaw, pitch):
    cy, sy = np.cos(yaw), np.sin(yaw)
    cp, sp = np.cos(pitch), np.sin(pitch)
    ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    rx = np.array([[1, 0, 0], [0, cp, -sp], [0, sp, cp]])
    return rx @ ry


def render(v, nm, idx, yaw, pitch, label):
    R = rot(yaw, pitch)
    p = v @ R.T
    n = nm @ R.T

    # Unity-Y (Laenge) waagerecht, Unity-Z (hoch) senkrecht
    sx, sy, sz = p[:, 1], p[:, 2], p[:, 0]
    span_x = max(sx.max() - sx.min(), 1e-6)
    span_y = max(sy.max() - sy.min(), 1e-6)
    scale = min(W * 0.94 / span_x, H * 0.90 / span_y)
    ox = W / 2 - (sx.min() + sx.max()) / 2 * scale
    oy = H / 2 + (sy.min() + sy.max()) / 2 * scale

    px = sx * scale + ox
    py = -sy * scale + oy

    img = np.full((H, W, 3), 24, dtype=np.float32)
    zbuf = np.full((H, W), 1e9, dtype=np.float32)

    shade = np.clip(n @ LIGHT, 0, 1) * 0.78 + 0.22

    for tri in idx:
        a, b, c = tri
        x = np.array([px[a], px[b], px[c]])
        y = np.array([py[a], py[b], py[c]])
        area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0])
        if CULL and area >= 0:
            continue  # Rueckseite
        if area == 0:
            continue
        x0, x1 = int(max(0, x.min())), int(min(W - 1, x.max()) + 1)
        y0, y1 = int(max(0, y.min())), int(min(H - 1, y.max()) + 1)
        if x1 <= x0 or y1 <= y0:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
        w0 = ((x[1] - x[0]) * (gy - y[0]) - (gx - x[0]) * (y[1] - y[0])) / area
        w1 = ((gx - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (gy - y[0])) / area
        w2 = 1.0 - w0 - w1
        m = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not m.any():
            continue
        zz = w2 * sz[a] + w1 * sz[b] + w0 * sz[c]
        sub = zbuf[y0:y1, x0:x1]
        better = m & (zz < sub)
        if not better.any():
            continue
        sub[better] = zz[better]
        col = (w2 * shade[a] + w1 * shade[b] + w0 * shade[c])[better]
        tgt = img[y0:y1, x0:x1]
        tgt[better] = np.stack([col * 205, col * 208, col * 214], -1)

    out = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    return out, label


if __name__ == "__main__":
    v, nm, idx = load(SRC)
    print("geladen: %d Vertices, %d Dreiecke" % (len(v), len(idx)))
    views = [
        (0.0, 0.0, "Seite"),
        (0.0, np.pi / 2 - 0.001, "Draufsicht"),
        (0.55, 0.35, "Dreiviertel"),
    ]
    tiles = [render(v, nm, idx, y, p, lbl)[0] for y, p, lbl in views]
    sheet = Image.new("RGB", (W, H * len(tiles)), (16, 16, 16))
    for i, t in enumerate(tiles):
        sheet.paste(t, (0, i * H))
    sheet.save(OUT)
    print("Vorschau: %s   (%s)" % (OUT, ", ".join(v[2] for v in views)))
