"""Read a glTF 2.0 file (.glb binary or .gltf JSON) into flat geometry.

Pure Python, no Blender, no dependencies beyond the standard library. glTF is
the format every no-login source (Sketchfab's auto-convert, Poly Pizza) always
offers, so this widens the usable model pool far past OBJ without needing a
DCC tool installed.

read(path) returns (verts, uvs, normals, faces) in exactly the shape
obj_import.parse_obj returns, so obj_import can treat both the same:
  verts   : list of (x, y, z)
  uvs     : list of (u, v)          (v is flipped to OBJ/ndmesh convention)
  normals : list of (nx, ny, nz)
  faces   : list of [(vi, ti, ni), ...] triangles

All node transforms are baked into world space at read time (glTF shares one
index per vertex across attributes, and models are often nested under scaled
nodes). Multiple primitives / meshes are concatenated with index offsets.
"""

import base64
import json
import os
import struct

# glTF componentType -> (struct format char, byte size)
_CT = {
    5120: ("b", 1),   # BYTE
    5121: ("B", 1),   # UNSIGNED_BYTE
    5122: ("h", 2),   # SHORT
    5123: ("H", 2),   # UNSIGNED_SHORT
    5125: ("I", 4),   # UNSIGNED_INT
    5126: ("f", 4),   # FLOAT
}
_NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4,
          "MAT2": 4, "MAT3": 9, "MAT4": 16}


def _load_glb(path):
    with open(path, "rb") as f:
        data = f.read()
    magic, version, length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        raise ValueError("not a GLB (bad magic) in %s" % path)
    off = 12
    js = None
    bin_chunk = b""
    while off < length:
        clen, ctype = struct.unpack_from("<II", data, off)
        off += 8
        chunk = data[off:off + clen]
        off += clen
        if ctype == 0x4E4F534A:      # 'JSON'
            js = json.loads(chunk.decode("utf-8"))
        elif ctype == 0x004E4942:    # 'BIN\0'
            bin_chunk = chunk
    if js is None:
        raise ValueError("GLB has no JSON chunk: %s" % path)
    return js, [bin_chunk]


def _load_gltf(path):
    with open(path, "r", encoding="utf-8") as f:
        js = json.load(f)
    base = os.path.dirname(os.path.abspath(path))
    buffers = []
    for buf in js.get("buffers", []):
        uri = buf.get("uri")
        if uri is None:
            buffers.append(b"")
        elif uri.startswith("data:"):
            buffers.append(base64.b64decode(uri.split(",", 1)[1]))
        else:
            with open(os.path.join(base, uri), "rb") as bf:
                buffers.append(bf.read())
    return js, buffers


def _accessor(js, buffers, idx):
    acc = js["accessors"][idx]
    ct, csize = _CT[acc["componentType"]]
    ncomp = _NCOMP[acc["type"]]
    count = acc["count"]
    bv = js["bufferViews"][acc["bufferView"]]
    buf = buffers[bv.get("buffer", 0)]
    start = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    stride = bv.get("byteStride") or (csize * ncomp)
    out = []
    for i in range(count):
        base_i = start + i * stride
        comps = struct.unpack_from("<" + ct * ncomp, buf, base_i)
        out.append(comps if ncomp > 1 else comps[0])
    return out


def _compose(node):
    """4x4 column-major-ish local matrix from a node's matrix or TRS.
    Returned as a flat row-major 4x4 list for _mul/_apply below."""
    if "matrix" in node:
        m = node["matrix"]  # glTF matrix is column-major
        # transpose into row-major
        return [m[0], m[4], m[8], m[12],
                m[1], m[5], m[9], m[13],
                m[2], m[6], m[10], m[14],
                m[3], m[7], m[11], m[15]]
    t = node.get("translation", [0.0, 0.0, 0.0])
    r = node.get("rotation", [0.0, 0.0, 0.0, 1.0])  # quat x,y,z,w
    s = node.get("scale", [1.0, 1.0, 1.0])
    x, y, z, w = r
    # rotation matrix from quaternion (row-major)
    rot = [
        1 - 2 * (y * y + z * z), 2 * (x * y - z * w),     2 * (x * z + y * w),     0.0,
        2 * (x * y + z * w),     1 - 2 * (x * x + z * z), 2 * (y * z - x * w),     0.0,
        2 * (x * z - y * w),     2 * (y * z + x * w),     1 - 2 * (x * x + y * y), 0.0,
        0.0,                     0.0,                     0.0,                     1.0,
    ]
    scl = [s[0], 0, 0, 0, 0, s[1], 0, 0, 0, 0, s[2], 0, 0, 0, 0, 1]
    trn = [1, 0, 0, t[0], 0, 1, 0, t[1], 0, 0, 1, t[2], 0, 0, 0, 1]
    return _mul(trn, _mul(rot, scl))


def _mul(a, b):
    out = [0.0] * 16
    for r in range(4):
        for c in range(4):
            out[r * 4 + c] = sum(a[r * 4 + k] * b[k * 4 + c] for k in range(4))
    return out


def _apply_point(m, p):
    x, y, z = p
    return (m[0] * x + m[1] * y + m[2] * z + m[3],
            m[4] * x + m[5] * y + m[6] * z + m[7],
            m[8] * x + m[9] * y + m[10] * z + m[11])


def _apply_dir(m, v):
    x, y, z = v
    return (m[0] * x + m[1] * y + m[2] * z,
            m[4] * x + m[5] * y + m[6] * z,
            m[8] * x + m[9] * y + m[10] * z)


def _srgb(c):
    """glTF baseColorFactor is LINEAR; PNGs are viewed/authored sRGB."""
    if c <= 0.0031308:
        return 12.92 * c
    return 1.055 * (c ** (1.0 / 2.4)) - 0.055


def materials(path):
    """Return one dict per glTF material: sRGB (r,g,b) 0..1, metallic,
    smoothness (= 1 - roughness). Flat baseColor factors only; texture-mapped
    materials fall back to their factor colour."""
    ext = os.path.splitext(path)[1].lower()
    js, _ = _load_glb(path) if ext == ".glb" else _load_gltf(path)
    out = []
    for m in js.get("materials", []):
        p = m.get("pbrMetallicRoughness", {})
        c = p.get("baseColorFactor", [1.0, 1.0, 1.0, 1.0])
        out.append({
            "color": (_srgb(c[0]), _srgb(c[1]), _srgb(c[2])),
            "metallic": p.get("metallicFactor", 1.0),
            "smoothness": 1.0 - p.get("roughnessFactor", 1.0),
        })
    return out


def read(path, with_materials=False):
    ext = os.path.splitext(path)[1].lower()
    js, buffers = _load_glb(path) if ext == ".glb" else _load_gltf(path)

    verts, uvs, normals, faces, face_mat = [], [], [], [], []
    have_uv = [False]
    have_n = [False]

    def emit_mesh(mesh_idx, world):
        for prim in js["meshes"][mesh_idx].get("primitives", []):
            if prim.get("mode", 4) != 4:
                continue  # only plain triangles
            mat_idx = prim.get("material", -1)
            attr = prim["attributes"]
            pos = _accessor(js, buffers, attr["POSITION"])
            nrm = _accessor(js, buffers, attr["NORMAL"]) if "NORMAL" in attr else None
            uv = _accessor(js, buffers, attr["TEXCOORD_0"]) if "TEXCOORD_0" in attr else None
            base = len(verts)
            for i in range(len(pos)):
                verts.append(_apply_point(world, pos[i]))
                if nrm is not None:
                    normals.append(_apply_dir(world, nrm[i])); have_n[0] = True
                else:
                    normals.append((0.0, 1.0, 0.0))
                if uv is not None:
                    u, v = uv[i]
                    uvs.append((u, 1.0 - v)); have_uv[0] = True  # glTF V is top-down
                else:
                    uvs.append((0.0, 0.0))
            if "indices" in prim:
                idx = _accessor(js, buffers, prim["indices"])
            else:
                idx = list(range(len(pos)))
            for t in range(0, len(idx) - 2, 3):
                a, b, c = base + idx[t], base + idx[t + 1], base + idx[t + 2]
                faces.append([(a, a, a), (b, b, b), (c, c, c)])
                face_mat.append(mat_idx)

    def walk(node_idx, parent):
        node = js["nodes"][node_idx]
        world = _mul(parent, _compose(node))
        if "mesh" in node:
            emit_mesh(node["mesh"], world)
        for child in node.get("children", []):
            walk(child, world)

    ident = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
    scenes = js.get("scenes")
    if scenes:
        scene = js.get("scene", 0)
        for root in scenes[scene].get("nodes", []):
            walk(root, ident)
    else:
        # No scene graph: emit every mesh at identity.
        for mi in range(len(js.get("meshes", []))):
            emit_mesh(mi, ident)

    # Strip attributes the model did not actually carry, so obj_import
    # recomputes normals / defaults UVs consistently.
    if not have_n[0]:
        normals = []
        faces = [[(c[0], c[1], None) for c in tri] for tri in faces]
    if not have_uv[0]:
        uvs = []
        faces = [[(c[0], None, c[2]) for c in tri] for tri in faces]
    if with_materials:
        return verts, uvs, normals, faces, face_mat, materials(path)
    return verts, uvs, normals, faces


if __name__ == "__main__":
    import sys
    v, t, n, f = read(sys.argv[1])
    print("glTF %s: %d verts, %d uvs, %d normals, %d tris"
          % (os.path.basename(sys.argv[1]), len(v), len(t), len(n), len(f)))
