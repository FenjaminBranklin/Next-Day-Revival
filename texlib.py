"""Gemeinsame Bausteine fuer die Waffentexturen.

Alle Texturen des Toolkits sind 1024er-Atlanten mit vier Vierteln, passend zu den
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

S = 1024
H = S // 2
PX = S / 512.0


def px(value, minimum=1):
    """Pixelmass aus dem bisherigen 512er-Bezug auf die Atlasgroesse skalieren."""
    return max(minimum, int(round(value * PX)))


def rng(seed):
    return np.random.default_rng(seed)


def grain(r, w, h, scale, amp):
    """Weiches, periodisches Rauschen als randlos kachelbare Grundstruktur."""
    a = r.normal(0.0, 1.0, (h, w)).astype(np.float32)
    a = periodic_blur(a, max(0.5, px(scale, minimum=0) * 0.45))
    std = float(a.std())
    if std > 1.0e-6:
        a = a / std
    return np.clip(a, -2.8, 2.8) * amp


def periodic_blur(a, sigma):
    """Gaussfilter mit periodischem Rand; gegenueberliegende Kanten passen zusammen."""
    h, w = a.shape
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.fftfreq(w)[None, :]
    kernel = np.exp(-2.0 * (np.pi * sigma) ** 2 * (fx * fx + fy * fy))
    return np.fft.ifft2(np.fft.fft2(a) * kernel).real.astype(np.float32)


def base(r, w, h, rgb, rough=0.06, scale=8):
    """Grundflaeche aus vier Oktaven - die feinste traegt am meisten.

    Die Gewichtung ist nicht beliebig. Mit der groebsten Oktave als staerkster
    (rough, rough/2, rough/4, rough/8) bekommt die Flaeche gesprenkelte Flecken
    von rund 15 Pixeln, und genau so sieht sie dann aus: wie Granit, nicht wie
    mattes Metall. Mattes Metall hat feine Koernung und nur wenig grobe
    Schwankung. Deshalb steht die Reihenfolge hier auf dem Kopf.

    `grain` normiert jede Oktave auf Standardabweichung 1 und multipliziert
    dann mit der Amplitude - ein `rough` von 0.04 sind also rund plus/minus
    10 Helligkeitsstufen je Oktave, nicht "ein bisschen Rauschen".
    """
    g = (grain(r, w, h, scale, rough * 0.22)
         + grain(r, w, h, max(1, scale // 2), rough * 0.38)
         + grain(r, w, h, max(1, scale // 4), rough * 0.65)
         + grain(r, w, h, 2, rough * 1.0))
    out = np.zeros((h, w, 3), np.float32)
    for i in range(3):
        out[..., i] = np.clip(rgb[i] / 255.0 + g, 0, 1)
    return out


def mottle(r, arr, amount=0.06, scale=14, tint=(1.0, 1.0, 1.0)):
    """Weiche, periodische Grosstruktur fuer Oel, Staub und ungleichen Lack."""
    h, w, _ = arr.shape
    cloud = grain(r, w, h, scale, amount)[..., None]
    color = np.asarray(tint, np.float32).reshape(1, 1, 3)
    return np.clip(arr + cloud * color, 0, 1)


def _scratch_angle(r, direction):
    if r.random() < 0.70:
        return np.deg2rad(direction + r.normal(0.0, 15.0))
    return r.random() * np.pi


def _wrapped_line(draw, points, fill, width, w, h):
    for ox in (-w, 0, w):
        for oy in (-h, 0, h):
            draw.line([(x + ox, y + oy) for x, y in points], fill=fill,
                      width=width, joint="curve")


def _scratch_path(r, x, y, angle, length, segments, bend):
    points = [(float(x), float(y))]
    step = length / float(segments)
    for _ in range(segments):
        angle += r.normal(0.0, bend)
        x += np.cos(angle) * step
        y += np.sin(angle) * step
        points.append((float(x), float(y)))
    return points


def scratches(r, arr, n, bright, length=60, direction=0.0):
    """Gebrauchsspuren: Haarkratzer, gekruemmte Riefen und wenige tiefe Grate."""
    h, w, _ = arr.shape
    light = Image.new("L", (w, h), 0)
    dark = Image.new("L", (w, h), 0)
    dl = ImageDraw.Draw(light)
    dd = ImageDraw.Draw(dark)

    def one(kind):
        x, y = r.integers(0, w), r.integers(0, h)
        ang = _scratch_angle(r, direction)
        if kind == "hair":
            ln = px(length) * (0.15 + 0.55 * r.random())
            segments, bend = 2, np.deg2rad(2.0)
            fill, width, gain = int(255 * (0.35 + 0.35 * r.random())), px(1), 0.15
        elif kind == "deep":
            ln = px(length) * (0.45 + 0.75 * r.random())
            segments, bend = int(r.integers(3, 6)), np.deg2rad(4.0)
            fill, width, gain = int(255 * (0.70 + 0.30 * r.random())), px(2), 1.0
        else:
            ln = px(length) * (0.30 + 0.70 * r.random())
            segments, bend = int(r.integers(3, 6)), np.deg2rad(3.5)
            fill, width, gain = int(255 * (0.35 + 0.55 * r.random())), px(1), 0.55
        target = dd if r.random() < (1.0 / 3.0) else dl
        signed_fill = max(1, int(round(fill * gain)))
        path = _scratch_path(r, x, y, ang, ln, segments, bend)
        _wrapped_line(target, path, signed_fill, width, w, h)

    for _ in range(max(1, 6 * n)):
        one("hair")
    for _ in range(n):
        one("medium")
    for _ in range(max(1, n // 8)):
        one("deep")

    blur = px(0.35, minimum=0)
    if blur:
        light = light.filter(ImageFilter.GaussianBlur(blur))
        dark = dark.filter(ImageFilter.GaussianBlur(blur))
    ml = np.asarray(light, np.float32)[..., None] / 255.0
    md = np.asarray(dark, np.float32)[..., None] / 255.0
    return np.clip(arr + ml * bright - md * bright * 0.82, 0, 1)


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


def rivets(r, arr, rows, cols, y0=40, dy=84, x0=24, dx=28, radius=3,
           bright=0.16, layout=None):
    return rivets_with_layout(r, arr, rows, cols, y0, dy, x0, dx,
                              radius, bright, layout)


def rivet_layout(r, w, h, rows, cols, y0=40, dy=84, x0=24, dx=28, radius=3):
    layout = []
    for row in range(rows):
        yy = px(y0) + row * px(dy)
        for col in range(cols):
            xx = px(x0) + col * px(dx)
            layout.append((xx + r.uniform(-PX, PX), yy + r.uniform(-PX, PX),
                           px(radius) * r.uniform(0.95, 1.05), r.random()))
    return layout


def rivets_with_layout(r, arr, rows, cols, y0=40, dy=84, x0=24, dx=28,
                       radius=3, bright=0.16, layout=None):
    h, w, _ = arr.shape
    if layout is None:
        layout = rivet_layout(r, w, h, rows, cols, y0, dy, x0, dx, radius)
    shadow = Image.new("L", (w, h), 0)
    shine = Image.new("L", (w, h), 0)
    ds, dl = ImageDraw.Draw(shadow), ImageDraw.Draw(shine)
    for xx, yy, rr, worn in layout:
        ds.ellipse([xx - rr - px(1), yy - rr - px(1),
                    xx + rr + px(1), yy + rr + px(1)], fill=150)
        level = 245 if worn < 0.18 else 195
        dl.ellipse([xx - rr, yy - rr, xx + rr, yy + rr], fill=level)
        dl.ellipse([xx - rr * 0.55, yy - rr * 0.55,
                    xx, yy], fill=255)
    sb = np.asarray(shadow.filter(ImageFilter.GaussianBlur(px(0.8))), np.float32)[..., None] / 255.0
    hl = np.asarray(shine.filter(ImageFilter.GaussianBlur(px(0.55))), np.float32)[..., None] / 255.0
    return np.clip(arr * (1.0 - sb * bright * 0.48) + hl * bright, 0, 1)


def perforation_layout(r, w, h, step=30, radius=9):
    layout = []
    step_px = px(step)
    for row in range(0, h // step_px + 1):
        for col in range(0, w // step_px + 1):
            cx = col * step_px + step_px // 2 + (step_px // 2 if row % 2 else 0)
            cy = row * step_px + step_px // 2
            layout.append((cx + r.uniform(-PX, PX), cy + r.uniform(-PX, PX),
                           px(radius) * r.uniform(0.95, 1.05)))
    return layout


def perforation(r, arr, step=30, radius=9, depth=0.88, rim=0.10, layout=None):
    """Kuehlbohrungen im Laufmantel, versetzt in Reihen."""
    h, w, _ = arr.shape
    if layout is None:
        layout = perforation_layout(r, w, h, step, radius)
    holes = Image.new("L", (w, h), 0)
    rims = Image.new("L", (w, h), 0)
    dh, dr = ImageDraw.Draw(holes), ImageDraw.Draw(rims)
    for cx, cy, rr in layout:
        dr.ellipse([cx - rr - px(2), cy - rr - px(2),
                    cx + rr + px(2), cy + rr + px(2)], fill=235)
        dh.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=255)
    hb = np.asarray(holes.filter(ImageFilter.GaussianBlur(px(0.8))), np.float32)[..., None] / 255.0
    rm = np.asarray(rims.filter(ImageFilter.GaussianBlur(px(1.3))), np.float32)[..., None] / 255.0
    ring = np.clip(rm - hb * 0.72, 0, 1)
    return np.clip(arr * (1.0 - hb * depth) + ring * rim, 0, 1)


def couple_height(arr, height, cavity=0.22, edge=0.10, radius=12,
                  cavity_tint=None, tint_strength=0.0, smooth=2.0):
    """Diffuse mit Vertiefungsdunkel und blankem Grat derselben Hoehenkarte koppeln."""
    height = periodic_blur(np.asarray(height, np.float32),
                           max(0.5, px(smooth, minimum=0)))
    local = periodic_blur(height, max(0.5, px(radius) / 3.0))
    low = np.clip(local - height, 0, None)
    high = np.clip(height - local, 0, None)

    def norm_mask(mask):
        nz = mask[mask > 1.0e-5]
        if nz.size == 0:
            return mask
        limit = max(1.0e-5, float(np.percentile(nz, 94.0)))
        return np.clip(mask / limit, 0, 1)

    low = norm_mask(low)[..., None]
    high = norm_mask(high)[..., None]
    out = arr * (1.0 - low * cavity)
    out = out + (1.0 - out) * high * edge
    if cavity_tint is not None and tint_strength > 0:
        color = np.asarray(cavity_tint, np.float32).reshape(1, 1, 3)
        mix = low * tint_strength
        out = out * (1.0 - mix) + color * mix
    return np.clip(out, 0, 1)


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


def height_perforation(r, w, h, step=30, radius=9, layout=None):
    holes = Image.new("L", (w, h), 128)
    d = ImageDraw.Draw(holes)
    if layout is None:
        layout = perforation_layout(r, w, h, step, radius)
    for cx, cy, rr in layout:
        d.ellipse([cx - rr - px(2), cy - rr - px(2),
                   cx + rr + px(2), cy + rr + px(2)], fill=190)
        d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=20)
    a = np.asarray(holes.filter(ImageFilter.GaussianBlur(px(0.7))), np.float32) / 255.0
    return a + grain(r, w, h, 6, 0.04)


def height_rivets(r, w, h, rows=3, cols=9, seams=(40, 168), layout=None):
    rec = Image.new("L", (w, h), 128)
    d = ImageDraw.Draw(rec)
    if layout is None:
        layout = rivet_layout(r, w, h, rows, cols, radius=4)
    for xx, yy, rr, worn in layout:
        d.ellipse([xx - rr - px(1), yy - rr - px(1),
                   xx + rr + px(1), yy + rr + px(1)], fill=86)
        d.ellipse([xx - rr, yy - rr, xx + rr, yy + rr],
                  fill=int(205 + 30 * worn))
    for yy in seams:
        sy = px(yy)
        offset = grain(r, w, 1, 48, 1.0)[0] * px(2)
        depth = grain(r, w, 1, 40, 1.0)[0]
        for x in range(w):
            seam_y = sy + int(round(offset[x]))
            fill = int(np.clip(70 + depth[x] * 10, 48, 92))
            d.line([(x, seam_y), (x, seam_y + px(3))], fill=fill)
    a = np.asarray(rec.filter(ImageFilter.GaussianBlur(px(0.5))), np.float32) / 255.0
    return a + grain(r, w, h, 5, 0.05)


def height_scratches(r, w, h, n=140, length=30):
    det = Image.new("L", (w, h), 128)
    d = ImageDraw.Draw(det)
    for _ in range(n):
        x, y = r.integers(0, w), r.integers(0, h)
        ang = r.random() * np.pi
        ln = px(length) * (0.3 + r.random())
        d.line([(x, y), (x + np.cos(ang) * ln, y + np.sin(ang) * ln)],
               fill=int(150 + 80 * r.random()), width=px(1))
    a = np.asarray(det.filter(ImageFilter.GaussianBlur(px(0.35))), np.float32) / 255.0
    return a + grain(r, w, h, 4, 0.05)


def height_bakelite(r, w, h):
    return 0.5 + grain(r, w, h, 12, 0.10) + grain(r, w, h, 4, 0.05)


def height_checker(r, w, h, pitch=14, depth=0.16):
    """Fischhaut - die gekreuzte Riffelung auf Griffschalen."""
    yy, xx = np.mgrid[0:h, 0:w]
    pitch_px = px(pitch)
    a = 0.5 + depth * 0.5 * (np.sin((xx + yy) * np.pi / pitch_px)
                             * np.sin((xx - yy) * np.pi / pitch_px))
    return a + grain(r, w, h, 6, 0.03)


def save_height_atlas(quads, path, strength=3.2):
    hgt = np.full((S, S), 0.5, np.float32)
    hgt[0:H, 0:H] = quads["shroud"]
    hgt[0:H, H:S] = quads["receiver"]
    hgt[H:S, 0:H] = quads["stock"]
    hgt[H:S, H:S] = quads["detail"]
    img = height_to_normal(np.clip(hgt, 0.0, 1.0), strength * PX)
    img.save(path)
    return img


# ----------------------------------------------------- Metall statt Tapete
#
# WARUM ES `machined` NEBEN `base` GIBT
# ------------------------------------
# `base` gewichtet bewusst die FEINSTE Oktave am staerksten (siehe dort). Das
# ist richtig fuer Lack, Gummi und Bakelit - und falsch fuer Metall. Bei einem
# `rough` von 0.04 traegt die Oktave auf Skala 2 rund plus/minus zehn
# Helligkeitsstufen, in jedem zweiten Pixel ein anderer Wert. Aus einem halben
# Meter Abstand - und naeher steht die Waffe in der ersten Person nie - liest
# sich das nicht als Metall, sondern als Raufasertapete: dichtes, ungerichtetes
# Korn ohne Glanz.
#
# Geschmiedeter, gefraester oder bruenierter Stahl sieht anders aus. Er ist
# fast gleichmaessig. Was ihn belebt, ist erstens eine WEICHE Grosstruktur
# (ungleicher Abrieb, Oel, Anlassfarben) und zweitens feine, GERICHTETE
# Polierzuege aus der Fertigung. Beides hat wenig Amplitude. Der sichtbare
# Charakter kommt nicht aus der Bemalung, sondern aus der Lichtantwort - also
# aus Metallic und Smoothness, siehe `gloss_quarter` weiter unten.
#
# Genau diesen Weg ist der T-72 in 0.5.2 schon gegangen: erst als Farbe und
# Kontrast zurueckgenommen und die Oberflaeche dem Material ueberlassen wurden,
# stand er neben dem MTW nicht mehr wie bemaltes Papier da.


def _snapped_wavelength(n, pitch):
    """Wellenlaenge so runden, dass sie ganzzahlig in n passt - sonst hat die
    Riffelung an der Viertelgrenze eine Kante."""
    wl = max(3.0, float(px(pitch)))
    k = max(1, int(round(n / wl)))
    return float(n) / k


def polish(r, arr, amount=0.010, pitch=3, axis="u", octaves=4):
    """Feine, gerichtete Polierzuege - der Unterschied zwischen Metall und Papier.

    Sehr schwach dosiert: die Zuege sollen den Blick ueber die Flaeche fuehren,
    nicht als Riffelblech auffallen. `axis` waehlt die Richtung: "u" laeuft
    laengs (konstant ueber Spalten), "v" quer.

    Die Staerke schwankt ueber die Flaeche (`fade`), sonst entsteht eine
    durchgehende, gleichmaessige Riffelung - und die sieht wieder nach Textil
    aus statt nach Metall.
    """
    h, w, _ = arr.shape
    n = w if axis == "u" else h
    line = np.zeros(n, np.float32)
    coord = np.arange(n, dtype=np.float32)
    for i in range(octaves):
        wl = _snapped_wavelength(n, pitch * (1.0 + 0.9 * i))
        gain = 1.0 / (1.0 + 1.1 * i)
        line += np.sin(coord * 2.0 * np.pi / wl + r.random() * 2.0 * np.pi) * gain
    std = float(line.std())
    if std > 1.0e-6:
        line = line / std
    band = line[None, :] if axis == "u" else line[:, None]
    fade = np.clip(0.55 + grain(r, w, h, 24, 0.60), 0.0, 1.6)
    return np.clip(arr + (band * fade)[..., None] * amount, 0, 1)


def machined(r, w, h, rgb, unruhe=0.013, sheen=0.010, pitch=3, axis="u"):
    """Grundflaeche fuer Metall: weiche Grosstruktur, kaum Feinkorn.

    Die Gewichtung steht genau andersherum als in `base`. Die groebste Oktave
    fuehrt, das Feinkorn ist nur noch eine Andeutung - es soll die Flaeche vor
    dem Bandenmuster einer reinen Verlaufsflaeche bewahren und sonst nichts.
    """
    g = (grain(r, w, h, 28, unruhe)
         + grain(r, w, h, 12, unruhe * 0.55)
         + grain(r, w, h, 5, unruhe * 0.26)
         + grain(r, w, h, 2, unruhe * 0.09))
    out = np.zeros((h, w, 3), np.float32)
    for i in range(3):
        out[..., i] = np.clip(rgb[i] / 255.0 + g, 0, 1)
    if sheen > 0:
        out = polish(r, out, sheen, pitch, axis)
    return out


def gunmetal(r, w, h, rgb, unruhe=0.006, sheen=0.011, pitch=3, axis="u",
             wolken=0.004):
    """Grundflaeche fuer bruenierten oder parkerisierten Stahl.

    Noch eine Stufe ruhiger als `machined`, und das ist der Punkt. Echte
    Waffenbruenierung ist im ALBEDO fast einfarbig - alles, was man an ihr
    sieht, ist Glanz, und der steht in der Metallic/Roughness-Map, nicht hier.
    Jede Wolke, die man dem Diffusebild mitgibt, ist Farbe, die auch im
    Schatten sichtbar bleibt, und Farbwolken auf einer glatten Flaeche sehen
    aus wie Putz.

    Gegenueber `machined`: die grobe Oktave ist auf ein Drittel gedaempft
    (`wolken`), das Feinkorn bleibt als Kachelschutz, und die gerichteten
    Polierzuege sind das staerkste Element der Flaeche.

    NICHT ZU VERWECHSELN mit `steel` weiter oben. Das ist die alte, viel
    groebere Flaeche auf `base` plus kraeftigen Kratzern; Drohne, Stoersender
    und Rakete benutzen sie und sollen sie behalten - dort sind es gestanzte
    Blechteile, keine polierte Waffe.
    """
    g = (grain(r, w, h, 28, wolken)
         + grain(r, w, h, 11, unruhe * 0.7)
         + grain(r, w, h, 4, unruhe)
         + grain(r, w, h, 2, unruhe * 0.5))
    out = np.zeros((h, w, 3), np.float32)
    for i in range(3):
        out[..., i] = np.clip(rgb[i] / 255.0 + g, 0, 1)
    if sheen > 0:
        out = polish(r, out, sheen, pitch, axis)
    return out


def wear_edges(arr, height, color, strength=0.10, radius=11,
               low=86.0, high=98.5):
    """Nur die hoechsten Grate zum blanken Traegermaterial abreiben.

    Dieselbe Rechnung, die bisher dreimal getrennt in mg42_texture
    (`blue_edge_wear`) und sniper50_texture (`anodize_wear`) stand.
    """
    local = periodic_blur(np.asarray(height, np.float32), px(radius) / 3.0)
    ridge = np.clip(height - local, 0, None)
    lo = float(np.percentile(ridge, low))
    hi = max(lo + 1.0e-5, float(np.percentile(ridge, high)))
    mask = np.clip((ridge - lo) / (hi - lo), 0, 1)[..., None]
    c = np.asarray(color, np.float32).reshape(1, 1, 3) / 255.0
    mix = mask * strength
    return np.clip(arr * (1.0 - mix) + c * mix, 0, 1)


# ------------------------------------------------- Metallic/Smoothness-Map
#
# WAS DIE MAP IM SPIEL TUT - UND WARUM SIE FRUEHER FEHLTE
# ------------------------------------------------------
# Der Unity-Standardshader liest bei gesetztem `_MetallicGlossMap` das Metallic
# aus dem ROTEN Kanal und die Smoothness aus dem ALPHAKANAL
# (`_SmoothnessTextureChannel` = 0). Die Skalarwerte `_Metallic` und
# `_Glossiness` sind dann wirkungslos.
#
# Bis hierher stand in RevivalPlugin.cs, kein Spielmaterial benutze eine solche
# Map. Das war 0.2.0 richtig gemessen und ist seit 0.5.2 ueberholt:
# `research/dump_material.py btr-80a_alb` hat am MTW genau diese Map gefunden,
# und der T-72 benutzt sie seitdem. Der Panzer ist damit das einzige Stueck des
# Toolkits, dem der Benutzer eine metallische Oberflaeche bescheinigt.
#
# DIE ZAHLEN BLEIBEN MASSVOLL. Ein Metallic nahe 1.0 nimmt seine Farbe
# vollstaendig aus der Umgebungsspiegelung; in einem dunklen Innenraum wird die
# Waffe dann schwarz. Der MTW steht auf Metallic 0.15 und Smoothness 0.40, und
# genau in dieser Groessenordnung bleiben auch die Waffen - nur die blanken
# Stahlteile gehen darueber.
#
# WARUM ES ZWEI DATEIEN SIND (2026-08-30)
# ---------------------------------------
# Die Map allein hat nichts geaendert, und der Grund ist der Shader. Gemessen
# an `resources.assets`, 1708 Materialien nach ihren Texturslots sortiert:
#
#     Shader 56  Standard                    _MetallicGlossMap          773
#     Shader 55  Standard (Specular setup)   _SpecGlossMap              466
#     Shader 57  Standard (Roughness setup)  BEIDE                      165
#
# Der T-72 erbt sein Material vom MTW und landet auf 56 - dort ist die
# Smoothness der Alphakanal der Metallic-Map, und der Panzer sieht metallisch
# aus. Die WAFFEN erben ihr Material von ihrer Spende-Waffe und landen auf 57.
# In dieser Fassung des Standardshaders kommt die Smoothness NICHT aus dem
# Alpha, sondern als ROUGHNESS aus `_SpecGlossMap`, und ein nicht gesetzter
# Slot ist dort "white" - Roughness 1.0, Smoothness 0, vollstaendig matt. Genau
# das war "sieht extrem nach Raufasertapete aus": das Metallic stimmte, es gab
# nur nirgends ein Glanzlicht.
#
# Deshalb schreibt jedes Waffenskript jetzt BEIDE Dateien:
#     <stamm>_metal.png   RGBA, R = Metallic, A = Smoothness   (Shader 56)
#     <stamm>_rough.png   RGB,  = 1 - Smoothness               (Shader 57)
# Das Plugin setzt beide, wenn das Material den jeweiligen Slot hat. Welcher
# Shader am Ende gewinnt, ist damit egal.


def gloss_quarter(r, metallic, smoothness, unruhe=0.05, mask=None,
                  mask_metallic=0.02, mask_smoothness=0.10):
    """Ein Viertel der Metallic/Gloss-Map: R = Metallic, A = Smoothness.

    `unruhe` streut beide Werte leicht. Eine Flaeche mit exakt konstanter
    Smoothness bekommt ein Glanzlicht wie eine Billardkugel - gleichmaessig und
    tot. Erst die Streuung laesst sie nach gewalztem Blech aussehen.

    `mask` (0..1) mischt einen zweiten, matten Satz ein - fuer Gummi, Lack oder
    Griffschalen, die im selben Viertel liegen.

    Gruen und Blau bekommen denselben Wert wie Rot, damit die Datei beim
    Ansehen lesbar ist; der Shader benutzt sie nicht.
    """
    # Zwei Oktaven statt einer. Die feine bricht das Glanzlicht pro Zentimeter
    # auf, die grobe laesst ganze Handflaechen matter oder blanker werden - das
    # ist der Unterschied zwischen gewalztem Blech und einer Billardkugel, und
    # er faellt erst auf, wenn sich die Waffe im Licht dreht.
    n = grain(r, H, H, 5, unruhe) + grain(r, H, H, 22, unruhe * 0.9)
    m = np.clip(metallic + n * 0.6, 0.0, 1.0)
    g = np.clip(smoothness + n, 0.0, 1.0)
    if mask is not None:
        k = np.clip(np.asarray(mask, np.float32), 0.0, 1.0)
        m = m * (1.0 - k) + mask_metallic * k
        g = g * (1.0 - k) + mask_smoothness * k
    out = np.zeros((H, H, 4), np.float32)
    out[..., 0] = m
    out[..., 1] = m
    out[..., 2] = m
    out[..., 3] = g
    return out


def save_gloss_atlas(quads, path):
    """Wie save_atlas, aber mit Alphakanal - der traegt die Smoothness."""
    tex = np.zeros((S, S, 4), np.float32)
    tex[0:H, 0:H] = quads["shroud"]
    tex[0:H, H:S] = quads["receiver"]
    tex[H:S, 0:H] = quads["stock"]
    tex[H:S, H:S] = quads["detail"]
    img = Image.fromarray((np.clip(tex, 0, 1) * 255).astype(np.uint8), "RGBA")
    img.save(path)
    return img


def save_rough_atlas(quads, path):
    """Dieselben Viertel als ROUGHNESS-Map fuer `_SpecGlossMap`.

    Roughness ist das Gegenteil von Smoothness, und die Smoothness steht im
    Alphakanal der Gloss-Viertel. Es ist also genau eine Umkehrung - keine
    zweite Kunst, sondern dieselbe Aussage fuer den anderen Shader (siehe den
    Abschnitt ueber die zwei Dateien weiter oben).

    Geschrieben wird RGB ohne Alpha: `Standard (Roughness setup)` liest den
    ROTEN Kanal, und eine Datei ohne Alphakanal kann von keiner Importregel
    versehentlich als Transparenz gelesen werden.
    """
    tex = np.zeros((S, S, 3), np.float32)
    for name, (y0, y1, x0, x1) in (("shroud", (0, H, 0, H)),
                                   ("receiver", (0, H, H, S)),
                                   ("stock", (H, S, 0, H)),
                                   ("detail", (H, S, H, S))):
        rough = 1.0 - np.clip(quads[name][..., 3], 0.0, 1.0)
        tex[y0:y1, x0:x1] = rough[..., None]
    img = Image.fromarray((np.clip(tex, 0, 1) * 255).astype(np.uint8), "RGB")
    img.save(path)
    return img
