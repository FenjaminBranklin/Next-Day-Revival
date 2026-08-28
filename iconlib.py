"""Rendert .ndmesh-Waffen als Inventar-Icons im Stil des Spiels.

WAS DAS SPIEL WIRKLICH BENUTZT (aus resources.assets ausgelesen)
----------------------------------------------------------------
Jede Waffe hat ZWEI Icons, nicht eines:

    ItemIcon    300 x 300   RPD_Item, svd_Item, PSG-1_Item
                Waffe diagonal, Muendung nach rechts oben, fast bildfuellend,
                transparenter Hintergrund, weicher Schlagschatten
    WeaponIcon  317 x 183   RPD_Weapon, svd_Weapon, PSG-1_Weapon
                Waffe waagerecht, Muendung nach rechts, Schaft links

Das Plugin hat bisher ein einziges quadratisches 256er-Bild in beide Felder
geschrieben. Im breiten Waffenslot wurde daraus ein gestauchtes Quadrat, und die
Muendung zeigte nach links statt nach rechts. Beides ist hier behoben.

Gegen Blockigkeit helfen drei Dinge:
  - Supersampling: intern in vierfacher Kantenlaenge rendern und mit LANCZOS
    herunterskalieren. Das glaettet die Treppenstufen an den Silhouettenkanten.
  - Zwei Lichter plus Umgebungsanteil statt eines harten Frontlichts.
  - Leichter Rim-Anteil an den Silhouettenkanten, damit sich die Form vom
    dunklen Inventarhintergrund abhebt.
"""

import math
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ndmesh

KEY = np.array([0.45, 0.62, 0.65])
KEY = KEY / np.linalg.norm(KEY)
FILL = np.array([-0.55, 0.25, 0.40])
FILL = FILL / np.linalg.norm(FILL)

SS = 4                      # Supersampling-Faktor
SPEC = 0.62                 # Staerke des Glanzlichts

# Beleuchtungsanteile. Nicht gewaehlt, sondern am Kontrastumfang der
# Spielvorlagen gemessen: RPD_Item liegt bei Mittel 37 und 95. Perzentil 86,
# svd_Item bei 39 und 109 - also Faktor 2.3 bis 2.8. Der alte Satz
# (0.14 / 0.70 / 0.20 / 0.16) kam auf Faktor 4.1: dieselbe Grundhelligkeit,
# aber viel zu harte Lichter, sichtbar als helles Rautenmuster auf dem
# MG42-Laufmantel. Mehr Umgebungslicht und weniger gerichtetes Licht bei
# gleicher Summe druecken die Spitze, ohne die Flaeche flau zu machen.
AMB = 0.30
KEYW = 0.50
FILLW = 0.18
RIMW = 0.10


def rot(yaw, pitch):
    cy, sy = math.cos(yaw), math.sin(yaw)
    cp, sp = math.cos(pitch), math.sin(pitch)
    ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    rx = np.array([[1, 0, 0], [0, cp, -sp], [0, sp, cp]])
    return rx @ ry


def load(path):
    V, N, T, IDX = ndmesh.load(path)
    return (np.array(V, np.float32), np.array(N, np.float32),
            np.array(IDX, np.int32).reshape(-1, 3), np.array(T, np.float32))


def load_texture(path):
    if not os.path.exists(path):
        return None
    return np.asarray(Image.open(path).convert("RGB"), np.float32) / 255.0


def render(v, nm, idx, uv, tex, w, h, yaw=0.34, pitch=0.20, fill=0.94, gain=580.0):
    """Orthografischer Render mit Z-Buffer. Muendung zeigt nach rechts.

    Das Mesh laeuft entlang -Y zur Muendung. Die Bildachse ist deshalb -Y, damit
    die Muendung rechts landet - so herum zeichnet das Spiel seine Icons.
    """
    R = rot(yaw, pitch)
    p = v @ R.T
    n = nm @ R.T
    sx, sy, sz = -p[:, 1], p[:, 2], p[:, 0]

    span_x = max(sx.max() - sx.min(), 1e-6)
    span_y = max(sy.max() - sy.min(), 1e-6)
    scale = min(w * fill / span_x, h * fill / span_y)
    px = sx * scale + (w / 2 - (sx.min() + sx.max()) / 2 * scale)
    py = -sy * scale + (h / 2 + (sy.min() + sy.max()) / 2 * scale)

    rgb = np.zeros((h, w, 3), np.float32)
    alpha = np.zeros((h, w), np.float32)
    zbuf = np.full((h, w), 1e9, np.float32)

    key = np.clip(n @ KEY, 0, 1)
    fillamt = np.clip(n @ FILL, 0, 1)
    rim = 1.0 - np.clip(np.abs(n[:, 0]), 0, 1)          # Blickrichtung ist X
    shade = AMB + KEYW * key + FILLW * fillamt + RIMW * rim ** 3

    # Glanzlicht. Ohne das bleibt jede Flaeche flach eingefaerbt, und genau so
    # sahen die eigenen Icons neben denen des Spiels aus: gleichmaessig graue
    # Platten. Die Spiel-Icons (RPD_Item, svd_Item) gehen bis 165..255 in den
    # Spitzen, der reine Diffusanteil kommt nie ueber die Texturhelligkeit.
    # Blinn-Phong: die Kamera blickt entlang +X, der Blickvektor ist also -X.
    view = np.array([-1.0, 0.0, 0.0])
    half = KEY + view
    half = half / np.linalg.norm(half)
    spec = np.clip(n @ half, 0, 1) ** 30.0

    for tri in idx:
        a, b, c = tri
        x = np.array([px[a], px[b], px[c]])
        y = np.array([py[a], py[b], py[c]])
        area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0])
        if area >= 0:
            continue                                     # Rueckseite
        x0, x1 = int(max(0, x.min())), int(min(w - 1, x.max()) + 1)
        y0, y1 = int(max(0, y.min())), int(min(h - 1, y.max()) + 1)
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
        col = np.clip((w2 * shade[a] + w1 * shade[b] + w0 * shade[c])[better], 0, 1.25)
        sp = (w2 * spec[a] + w1 * spec[b] + w0 * spec[c])[better] * SPEC * 255.0
        tgt = rgb[y0:y1, x0:x1]
        if tex is None:
            tgt[better] = np.stack([col * 188 + sp, col * 194 + sp, col * 206 + sp], -1)
        else:
            tu = (w2 * uv[a, 0] + w1 * uv[b, 0] + w0 * uv[c, 0])[better]
            tv = (w2 * uv[a, 1] + w1 * uv[b, 1] + w0 * uv[c, 1])[better]
            th, tw = tex.shape[0], tex.shape[1]
            xi = np.clip((tu * tw).astype(np.int32), 0, tw - 1)
            yi = np.clip(((1.0 - tv) * th).astype(np.int32), 0, th - 1)
            tgt[better] = tex[yi, xi] * col[:, None] * gain + sp[:, None]
        alpha[y0:y1, x0:x1][better] = 255.0

    return Image.fromarray(np.dstack([np.clip(rgb, 0, 255), alpha]).astype(np.uint8),
                           "RGBA")


def drop_shadow(img, offset=(5, 7), blur=7, strength=0.55):
    """Weicher Schlagschatten wie auf den Spiel-Icons."""
    a = img.split()[3]
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    mask = a.filter(ImageFilter.GaussianBlur(blur)).point(
        lambda v: int(v * strength))
    shadow = Image.new("RGBA", img.size, (0, 0, 0, 255))
    shadow.putalpha(mask)
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    sh.paste(shadow, offset)
    return Image.alpha_composite(sh, img)


def fit(img, w, h, margin=0.94):
    """Zentriert das sichtbare Motiv in ein Bild der Zielgroesse."""
    bbox = img.split()[3].getbbox()
    if bbox is None:
        return img.resize((w, h), Image.LANCZOS)
    crop = img.crop(bbox)
    k = min(w * margin / crop.width, h * margin / crop.height)
    crop = crop.resize((max(1, int(crop.width * k)), max(1, int(crop.height * k))),
                       Image.LANCZOS)
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    out.paste(crop, ((w - crop.width) // 2, (h - crop.height) // 2), crop)
    return out


def item_icon(mesh_path, tex_path, out_path, size=300, tilt=49.0,
              yaw=0.42, pitch=0.0, gain=580.0, margin=0.82):
    """300x300, Waffe diagonal - wie RPD_Item, svd_Item, PSG-1_Item.

    tilt und margin sind an den Vorlagen gemessen, nicht gewaehlt. Der Kasten
    der nicht durchsichtigen Pixel liegt beim RPD bei 75.0 x 85.7 Prozent der
    Bildkante, bei der SVD bei 80.0 x 83.3. Eine lange duenne Waffe ergibt bei
    Neigung theta einen Kasten im Verhaeltnis tan(theta) - aus 86/75 folgt
    rund 49 Grad. Mit tilt 33 wurden daraus 97 x 70: zu flach, und links und
    rechts am Bildrand abgeschnitten. Die margin von 0.86 statt 0.94 laesst
    denselben Rand wie die Vorlagen.
    """
    v, nm, idx, uv = load(mesh_path)
    tex = load_texture(tex_path)
    big = render(v, nm, idx, uv, tex, size * SS * 3, size * SS, yaw, pitch,
                 fill=0.98, gain=gain)
    big = big.rotate(tilt, resample=Image.BICUBIC, expand=True)
    img = fit(big, size, size, margin=margin)
    img = drop_shadow(img, offset=(4, 6), blur=6, strength=0.5)
    img.save(out_path)
    return img


def weapon_icon(mesh_path, tex_path, out_path, w=317, h=183,
                yaw=0.42, pitch=0.0, gain=580.0, margin=0.88):
    """317x183, Waffe waagerecht - wie RPD_Weapon, svd_Weapon, PSG-1_Weapon.

    Hier ist der yaw der wirksame Regler, nicht die Groesse. Das RPD-Icon des
    Spiels zeigt eine Silhouette von rund 3.3 zu 1; die eigenen Waffen kamen
    auf 5.4 zu 1, weil sie fast von der Seite gerendert wurden. Mehr yaw
    dreht die Waffe zur Kamera, verkuerzt sie perspektivisch und macht die
    Silhouette gedrungener - genau der Dreiviertelblick der Vorlagen.
    """
    v, nm, idx, uv = load(mesh_path)
    tex = load_texture(tex_path)
    big = render(v, nm, idx, uv, tex, w * SS, h * SS, yaw, pitch,
                 fill=0.98, gain=gain)
    img = fit(big, w, h, margin=margin)
    img = drop_shadow(img, offset=(3, 4), blur=5, strength=0.5)
    img.save(out_path)
    return img


def report(path):
    img = Image.open(path)
    a = np.asarray(img)[..., 3]
    print("  %-26s %4dx%-4d  %5.1f%% gefuellt"
          % (os.path.basename(path), img.width, img.height, 100.0 * (a > 8).mean()))
