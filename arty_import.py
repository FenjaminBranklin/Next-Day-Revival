"""Import the supplied Bohdana, never generate a substitute vehicle.

Run: python arty_import.py [source.glb]
Requires numpy, Pillow and fast-simplification==0.2.0 (build-time only).
The supplied SketchUp export contains two trucks, a ground plane and invisible
edge geometry. Keep the deployed truck, assign weathered vehicle materials, split
the original geometry at the gun joints and reduce its rendering cost.
Source coordinates are inches. Runtime uses 3 game units per metre, Y up,
Z forward, matching the native BTR/T-72 rather than assuming Unity metres.
"""
import hashlib
import math
from pathlib import Path
import sys

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / 'build/arty_deps'))
import gltf_read
from ndmesh import Mesh

SOURCE_SHA = '252fb424c52b4a5139cc7e607e399af9c3c53b0bbf289ebeba2ec9708b8e3f1f'
ORIGIN = np.array([205., -3.3137567, -88.25])
RING = np.array([342., 79., -88.25])
PIVOT = np.array([340., 134.5, -69.5])
# Measured along the long barrel component of the supplied model.
FORWARD = np.array([-.54013170, .58368559, -.60627458])
FORWARD /= np.linalg.norm(FORWARD)
FLAT = FORWARD.copy(); FLAT[1] = 0; FLAT /= np.linalg.norm(FLAT)
RIGHT = np.cross([0., 1., 0.], FLAT)
UP = np.cross(FORWARD, RIGHT)
YAW_FRAME = np.stack([RIGHT, [0., 1., 0.], FLAT])
GUN_FRAME = np.stack([RIGHT, UP, FORWARD])
BODY_FRAME = np.array([[0., 0., 1.], [0., 1., 0.], [-1., 0., 0.]])
SCALE = .0254 * 3.0


def surface_uv(points, normal, material, low, span):
    """Planar mapping in the source frame, continuous across coplanar faces.

    The authored atlas is retained on rebuild: olive / rubber above steel /
    glass. Insets keep mipmap filtering away from neighbouring materials.
    """
    if material in (11, 12, 13, 24, 37):
        tile = (1, 1)
    elif material in (35, 36):
        tile = (1, 0)
    elif material in (2, 8, 19, 27, 31, 39):
        tile = (0, 0)
    else:
        tile = (0, 1)
    axis = int(np.argmax(np.abs(normal)))
    axes = [i for i in range(3) if i != axis]
    uv = (points[:, axes] - low[axes]) / span[axes]
    return np.array(tile)*.5 + .015 + uv*.47


def components(v, f):
    points, inv = np.unique(np.round(v[f].reshape(-1, 3), 3), axis=0,
                            return_inverse=True)
    faces = inv.reshape(-1, 3)
    parent = np.arange(len(points))
    def root(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    for a, b, c in faces:
        r = root(a); parent[root(b)] = r; parent[root(c)] = r
    return np.array([root(a) for a in faces[:, 0]])


def split_recoil(mesh):
    """Split connected bore/breech from its cradle without changing geometry.

    The source's long component measures 27.36 units along local +Z. The other
    three pieces are at most 9.62 long and remain attached to the trunnion.
    Works on the shipped mesh as well as on a fresh GLB import.
    """
    v = np.asarray(mesh.V)
    f = np.asarray(mesh.IDX).reshape(-1, 3)
    labels = components(v, f)
    moving = np.zeros(len(f), dtype=bool)
    for label in np.unique(labels):
        mask = labels == label
        if np.ptp(v[np.unique(f[mask])], axis=0)[2] > 20.0:
            moving[mask] = True
    if not moving.any() or moving.all():
        raise ValueError('Bohdana recoil component selection failed')
    result = []
    for mask, name in ((~moving, 'Bohdana cradle'), (moving, 'Bohdana recoil')):
        indices = f[mask].reshape(-1)
        part = Mesh(name)
        part.V = [mesh.V[i] for i in indices]
        part.N = [mesh.N[i] for i in indices]
        part.T = [mesh.T[i] for i in indices]
        part.IDX = list(range(len(indices)))
        result.append(part)
    return result


def render(v, f, uv, texture, path, yaw, pitch):
    """Orthographic preview of the actual imported parts, with face lighting."""
    cy, sy, cp, sp = math.cos(yaw), math.sin(yaw), math.cos(pitch), math.sin(pitch)
    r = np.array([[cy, 0, sy], [sy*sp, cp, -cy*sp], [-sy*cp, sp, cy*cp]])
    p = v @ r.T
    lo, hi = p.min(0), p.max(0)
    sc = min(1050/(hi[0]-lo[0]), 750/(hi[1]-lo[1]))
    xy = np.stack((75+(p[:, 0]-lo[0])*sc, 820-(p[:, 1]-lo[1])*sc), 1)
    normal = np.cross(v[f[:, 1]]-v[f[:, 0]], v[f[:, 2]]-v[f[:, 0]])
    normal /= np.maximum(np.linalg.norm(normal, axis=1)[:, None], 1e-12)
    shade = .62 + .38*np.abs(normal @ np.array([.3, .85, .43]))
    rgb = np.full((900, 1200, 3), 238., dtype=np.float32)
    depth = np.full((900, 1200), -np.inf)
    for i, tri in enumerate(f):
        q = xy[tri]; x, y = q[:, 0], q[:, 1]
        area = (x[1]-x[0])*(y[2]-y[0])-(x[2]-x[0])*(y[1]-y[0])
        if area>=-1e-8: continue
        x0, x1 = max(0, int(x.min())), min(1200, int(x.max())+1)
        y0, y1 = max(0, int(y.min())), min(900, int(y.max())+1)
        gx, gy = np.meshgrid(np.arange(x0,x1)+.5, np.arange(y0,y1)+.5)
        c = ((x[1]-x[0])*(gy-y[0])-(gx-x[0])*(y[1]-y[0]))/area
        b = ((gx-x[0])*(y[2]-y[0])-(x[2]-x[0])*(gy-y[0]))/area
        a = 1-b-c
        z = a*p[tri[0],2]+b*p[tri[1],2]+c*p[tri[2],2]
        sub = depth[y0:y1,x0:x1]
        mask = (a>=0)&(b>=0)&(c>=0)&(z>sub)
        sub[mask] = z[mask]
        coords = a[..., None]*uv[tri[0]] + b[..., None]*uv[tri[1]] + c[..., None]*uv[tri[2]]
        tx = np.clip((coords[..., 0]*(texture.shape[1]-1)).astype(int), 0, texture.shape[1]-1)
        ty = np.clip(((1-coords[..., 1])*(texture.shape[0]-1)).astype(int), 0, texture.shape[0]-1)
        rgb[y0:y1,x0:x1][mask] = (texture[ty, tx]*shade[i])[mask]
    Image.fromarray(np.clip(rgb,0,255).astype(np.uint8)).save(path)


def main():
    src = Path(sys.argv[1]) if len(sys.argv)>1 else ROOT/'test_2s22_bohdana_self-propelled_artillery.glb'
    if not src.exists() and len(sys.argv)==1:
        # Client packages contain the finished model, not the original download.
        for name in ('hull.ndmesh', 'turret.ndmesh', 'barrel.ndmesh', 'recoil.ndmesh',
                     'diffuse.png', 'metal.png', 'normal.png'):
            if not (ROOT/'assets'/('arty_'+name)).exists():
                raise FileNotFoundError('Missing Bohdana source and shipped assets: '+str(src))
        print('Bohdana: keeping shipped assets; pass source.glb to rebuild')
        return
    import fast_simplification
    if hashlib.sha256(src.read_bytes()).hexdigest() != SOURCE_SHA:
        raise ValueError('Source changed: measure the selection and joints again')
    v, _, n, faces, mats, materials = gltf_read.read(str(src), True)
    v, n = np.array(v), np.array(n)
    f = np.array([[c[0] for c in t] for t in faces]); mats = np.array(mats)
    js, _ = gltf_read._load_glb(str(src))
    visible = np.array([m.get('pbrMetallicRoughness', {}).get('baseColorFactor', [1,1,1,1])[3]>0
                        for m in js['materials']])
    keep = visible[mats] & (v[f][:, :, 2].min(1)>-250) & (v[f][:, :, 1].max(1)>0) & (mats!=1)
    f, mats = f[keep], mats[keep]
    labels = components(v, f)
    part = np.zeros(len(f), dtype=int)
    # Whole connected components, never a coordinate cut through a triangle.
    # Barrel, breech cover and recoil cradle measured in the source preview.
    barrel_labels = {58943, 58904, 41334, 54196}
    for label in np.unique(labels):
        mask = labels == label
        q = v[np.unique(f[mask])]
        lo, hi = q.min(0), q.max(0)
        if label in barrel_labels:
            part[mask] = 2
        elif lo[0]>280 and lo[1]>65 and hi[1]>90:
            part[mask] = 1
    # SketchUp exports coincident front/back surfaces, often with a default
    # grey back material. Simplifying the two separately makes them intersect.
    # Keep the coloured front once and restore an exact matching reverse face
    # AFTER simplification. The artist's thin panels stay visible on both sides.
    _, corner = np.unique(np.round(v[f].reshape(-1, 3), 4), axis=0,
                          return_inverse=True)
    keys = np.sort(corner.reshape(-1, 3), axis=1)
    preference = np.array([2 if m==2 else 1 if materials[m]['color']==(1.,1.,1.) else 0
                           for m in mats])
    order = np.argsort(preference, kind='stable')
    _, first = np.unique(keys[order], axis=0, return_index=True)
    pick = order[first]
    f, mats, part = f[pick], mats[pick], part[pick]
    # Correct authored outward winding before the simplifier drops normals.
    face_normal = np.cross(v[f[:, 1]]-v[f[:, 0]], v[f[:, 2]]-v[f[:, 0]])
    flip = np.einsum('ij,ij->i', face_normal, n[f].mean(1)) < 0
    f[flip] = f[flip][:, [0, 2, 1]]
    assets = ROOT/'assets'
    texture = np.array(Image.open(assets/'arty_diffuse.png').convert('RGB'))
    if min(texture.shape[:2]) < 1024:
        raise ValueError('Weathered artillery atlas missing; refusing the old flat palette')
    selected = v[np.unique(f)]
    low = selected.min(0)
    span = np.maximum(selected.max(0)-low, 1e-6)
    preview_v, preview_f, preview_c = [], [], []
    offset = 0
    for index, name, origin, frame, budget in [
            (0, 'hull', ORIGIN, BODY_FRAME, 23000),
            (1, 'turret', RING, YAW_FRAME, 7000),
            (2, 'barrel', PIVOT, GUN_FRAME, 6000)]:
        mesh = Mesh('Bohdana '+name)
        total = np.count_nonzero(part==index)
        for mat in np.unique(mats[part==index]):
            ff = f[(part==index)&(mats==mat)]
            # Weld source split corners before simplification, within material.
            vv, inverse = np.unique(np.round(v[ff].reshape(-1, 3), 5), axis=0,
                                    return_inverse=True)
            ff = inverse.reshape(-1, 3)
            target = min(len(ff), max(12, int(budget*len(ff)/total)))
            if target < len(ff):
                vv, ff = fast_simplification.simplify(vv, ff, target_count=target, agg=8.)
            local = ((vv-origin) @ frame.T * SCALE).astype(np.float32)
            if np.linalg.det(frame)<0: ff = ff[:, [0, 2, 1]]
            for tri in ff:
                points = local[tri]
                normal = np.cross(points[1]-points[0], points[2]-points[0])
                length = np.linalg.norm(normal)
                if length < 2e-5: continue
                normal /= length
                source_points = vv[tri]
                source_normal = np.cross(source_points[1]-source_points[0],
                                         source_points[2]-source_points[0])
                uvs = surface_uv(source_points, source_normal, int(mat), low, span)
                for positions, outward in ((points,normal),(points[[0,2,1]],-normal)):
                    base = len(mesh.V)
                    mesh.V.extend(map(tuple, positions)); mesh.N.extend([tuple(outward)]*3)
                    mesh.T.extend(map(tuple, uvs if outward is normal else uvs[[0,2,1]]))
                    mesh.IDX.extend([base, base+1, base+2])
            # Restore original joints for the preview of the deployed posture.
        if name == 'barrel':
            cradle, sliding = split_recoil(mesh)
            cradle.write(str(assets/'arty_barrel.ndmesh'))
            sliding.write(str(assets/'arty_recoil.ndmesh'))
        else:
            mesh.write(str(assets/('arty_'+name+'.ndmesh')))
        print(name, len(mesh.V), 'vertices', len(mesh.IDX)//3, 'triangles')
        pv = np.array(mesh.V)/SCALE @ frame + origin
        pf = np.array(mesh.IDX).reshape(-1, 3)
        preview_v.append((pv-ORIGIN) @ BODY_FRAME.T*SCALE)
        preview_f.append(pf+offset); offset += len(pv)
        preview_c.extend(mesh.T)
    pv, pf, pc = np.concatenate(preview_v), np.concatenate(preview_f), np.array(preview_c)
    render(pv, pf, pc, texture, assets/'arty_preview.png', -.85, .25)
    render(pv, pf, pc, texture, assets/'arty_side_preview.png', math.pi/2, 0)
    print('runtime ring', (RING-ORIGIN)@BODY_FRAME.T*SCALE)
    print('runtime trunnion', (PIVOT-RING)@YAW_FRAME.T*SCALE)
    muzzle_source = np.array([194.92429479, 291.13868353, -233.19519393])
    print('runtime muzzle', (muzzle_source-PIVOT)@GUN_FRAME.T*SCALE)
    print('source', SOURCE_SHA)


if __name__ == '__main__':
    main()
