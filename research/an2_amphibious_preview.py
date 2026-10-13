"""Offline shaded preview of the actual shipped plane plus amphibious kit.

No Unity, Steam or game installation. Includes a 5 u player capsule at K=2.8.
Run python research/an2_amphibious_preview.py. Output is under build/.
"""
from pathlib import Path
import sys

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import an2_amphibious_build as kit
from ndmesh import load


def assembly():
    items = []
    body, ranges = kit.land_gear_mask()
    keep = np.ones(len(body[3]), dtype=bool)
    for start, length, _ in ranges:
        keep[start:start+length] = False
    original = Image.open(kit.ASSETS/'an2_diffuse.png').convert('RGB')
    amphib = Image.open(kit.ASSETS/'an2_amphib_diffuse.png').convert('RGB')
    items.append((body[:3]+(np.array(body[3])[keep],), (0, 0, 0), original))
    # Include all control surfaces in neutral position about the shipped rig pivots.
    for row in (kit.ASSETS/'an2_rig.txt').read_text().splitlines():
        p = row.split()
        if p and p[0] == 'pivot':
            items.append((load(str(kit.ASSETS/('an2_%s.ndmesh' % p[1]))), tuple(map(float, p[2:5])), original))
    items.append((load(str(kit.ASSETS/'an2_glass.ndmesh')), (0, 0, 0), original))
    items.append((load(str(kit.ASSETS/'an2_amphib_floats.ndmesh')), (0, 0, 0), amphib))
    for _, kind, x, y, z, *_ in kit.WHEELS:
        items.append((load(str(kit.ASSETS/('an2_amphib_%s.ndmesh' % kind))), (x, y, z), amphib))
    vertices, normals, triangles, colors = [], [], [], []
    base = 0
    for (v, n, uv, idx), position, texture in items:
        v, n, uv = np.array(v), np.array(n), np.array(uv)
        idx = np.array(idx).reshape(-1, 3)
        tex = np.array(texture)
        mean_uv = uv[idx].mean(axis=1)
        tx = np.clip((mean_uv[:, 0]*(tex.shape[1]-1)).astype(int), 0, tex.shape[1]-1)
        ty = np.clip(((1-mean_uv[:, 1])*(tex.shape[0]-1)).astype(int), 0, tex.shape[0]-1)
        vertices.extend(v+position+kit.OFFSET)
        normals.extend(n)
        triangles.extend(idx+base)
        colors.extend(tex[ty, tx])
        base += len(v)
    return np.array(vertices), np.array(normals), np.array(triangles), np.array(colors)


def render(v, n, triangles, colors, camera, label):
    width, height = 600, 360
    forward = np.array(camera, dtype=float)
    forward /= np.linalg.norm(forward)
    right = np.cross([0., 1., 0.], forward)
    right /= np.linalg.norm(right)
    up = np.cross(forward, right)
    basis = np.array([right, up, forward])
    q = v @ basis.T
    scale = min((width-55)/np.ptp(q[:, 0]), (height-85)/np.ptp(q[:, 1]))
    x = (q[:, 0]-(q[:, 0].min()+q[:, 0].max())*.5)*scale+width*.5
    y = -(q[:, 1]-q[:, 1].min())*scale+height-45
    zbuf = np.full((height, width), -np.inf)
    pixels = np.full((height, width, 3), (202, 207, 199), dtype=np.uint8)
    light = np.array([-.45, .85, .4]); light /= np.linalg.norm(light)
    for i, tri in enumerate(triangles):
        a, b, c = tri
        outward = np.cross(v[b]-v[a], v[c]-v[a])
        if np.dot(outward, forward) <= 0:
            continue
        xa, xb, xc = x[tri]; ya, yb, yc = y[tri]
        denominator = (yb-yc)*(xa-xc)+(xc-xb)*(ya-yc)
        if abs(denominator) < 1e-8:
            continue
        x0, x1 = max(0, int(min(x[tri]))), min(width-1, int(max(x[tri]))+1)
        y0, y1 = max(0, int(min(y[tri]))), min(height-1, int(max(y[tri]))+1)
        yy, xx = np.mgrid[y0:y1+1, x0:x1+1]
        u = ((yb-yc)*(xx-xc)+(xc-xb)*(yy-yc))/denominator
        w = ((yc-ya)*(xx-xc)+(xa-xc)*(yy-yc))/denominator
        t = 1-u-w
        depth = u*q[a, 2]+w*q[b, 2]+t*q[c, 2]
        region = zbuf[y0:y1+1, x0:x1+1]
        mask = (u >= 0) & (w >= 0) & (t >= 0) & (depth > region)
        normal = n[tri].mean(axis=0)
        shade = .42+.58*max(0, np.dot(normal, light))
        pixels[y0:y1+1, x0:x1+1][mask] = np.clip(colors[i]*shade, 0, 255)
        region[mask] = depth[mask]
    img = Image.fromarray(pixels)
    draw = ImageDraw.Draw(img)
    draw.text((12, 8), label, fill=(25, 30, 25))
    # Capsule is drawn at the same pixel scale; its height is measured in metres.
    ph, pw = kit.CAPSULE_U/kit.K*scale, 1.5/kit.K*scale
    px, py = 20, height-45
    draw.rounded_rectangle((px, py-ph, px+pw, py), radius=pw*.5, fill=(137, 48, 38))
    draw.text((12, height-28), '5 u player capsule (1.786 m); same scale', fill=(25, 30, 25))
    return img


def main():
    geometry = assembly()
    views = [((1., .05, 0.), 'SIDE / floats 7.9 m / wheels down'),
             ((0., .05, 1.), 'FRONT / twin floats / four wheel assemblies'),
             ((1., .32, 1.), 'THREE QUARTER / shipped An-2 + permanent kit')]
    result = Image.new('RGB', (1800, 360))
    for i, (camera, title) in enumerate(views):
        result.paste(render(*geometry, camera, title), (i*600, 0))
    output = ROOT/'build/an2-amphibious-preview.png'
    output.parent.mkdir(exist_ok=True)
    result.save(output)
    result.resize((1200, 240)).save(ROOT/'build/an2-amphibious-preview-small.png')
    print(output)


if __name__ == '__main__':
    main()
