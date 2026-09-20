"""Import the supplied FIM-92 launcher and its separate display missile.

Source: Pan_Ar4ik, CC-BY-4.0; see CREDITS.md. No replacement geometry.
Launcher: LAW hand frame, muzzle -Y, up +Z. Missile: nose +Z, centre pivot.
Run: python stinger_build.py
"""

import os

import numpy as np
from PIL import Image, ImageOps

import gltf_read
import iconlib
import ndmesh
import obj_import
import textured_import as ti

ROOT = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(ROOT, "assets")
SOURCE = os.path.join(ASSETS, "src", "fim-92_stinger.glb")
CELL = 1024
LAUNCHER_LENGTH = 1520.0 / 393.5
GRIP_TARGET = np.array([0.0, 0.624, -0.090])


def atlas(js, buffers, material_ids, name):
    """Retain image colour, normal, metallic and roughness, including MR pixels."""
    cols, rows = ti._grid(len(material_ids))
    dimensions = (cols * CELL, rows * CELL)
    diffuse = Image.new("RGB", dimensions)
    normal = Image.new("RGB", dimensions, (128, 128, 255))
    metallic = Image.new("RGBA", dimensions)
    roughness = Image.new("L", dimensions, 255)
    for cell, source_id in enumerate(material_ids):
        material = js["materials"][source_id]
        pbr = material["pbrMetallicRoughness"]
        origin = (cell % cols * CELL, cell // cols * CELL)

        def texture(reference):
            image_id = ti._tex_source(js, reference["index"])
            return ti._load_image(js, buffers, image_id).resize(
                (CELL, CELL), Image.Resampling.LANCZOS)

        diffuse.paste(texture(pbr["baseColorTexture"]).convert("RGB"), origin)
        normal.paste(texture(material["normalTexture"]).convert("RGB"), origin)
        mr = texture(pbr["metallicRoughnessTexture"])
        # glTF MR: metallic B, roughness G. Unity Standard: metallic R,
        # smoothness A. Preserve the authored response instead of flat cells.
        rough = mr.getchannel("G")
        met = mr.getchannel("B")
        zero = Image.new("L", (CELL, CELL), 0)
        metallic.paste(Image.merge("RGBA", (met, zero, zero,
                                           ImageOps.invert(rough))), origin)
        roughness.paste(rough, origin)
    for suffix, image in (("diffuse", diffuse), ("normal", normal),
                          ("metal", metallic), ("rough", roughness)):
        image.save(os.path.join(ASSETS, name + "_" + suffix + ".png"))
    return cols, rows


def part(data, material_ids, grid):
    vertices, uvs, normals, faces, face_material, _ = data
    compact = dict((source, index) for index, source in enumerate(material_ids))
    vertex_material = {}
    kept = []
    for face, material in zip(faces, face_material):
        if material not in compact:
            continue
        kept.append(face)
        for corner in face:
            vertex_material[corner[0]] = compact[material]
    cols, rows = grid
    remapped = []
    for index, (u, v) in enumerate(uvs):
        material = vertex_material.get(index, 0)
        remapped.append(((material % cols + u) / cols,
                         (rows - 1 - material // cols + v) / rows))
    return obj_import.build(vertices, remapped, normals, kept,
                            obj_import.parse_axes("x,y,z"), False, False)


def save(mesh, name):
    dropped = ti.prune_degenerate(mesh)
    reversed_faces = ti.align_winding_to_normals(mesh)
    path = os.path.join(ASSETS, name + ".ndmesh")
    mesh.write(path)
    stored = ndmesh.Mesh(name)
    stored.V, stored.N, stored.T, stored.IDX = ndmesh.load(path)
    stored.validate()
    print("%s: %d vertices, %d triangles; pruned %d, aligned %d" %
          (name, len(mesh.V), len(mesh.IDX) // 3, dropped, reversed_faces))
    print("  bounds: %s" % (mesh.bounds(),))
    return path


def main():
    js, buffers = gltf_read._load_glb(SOURCE)
    data = gltf_read.read(SOURCE, with_materials=True)
    launcher = part(data, [0, 1, 2], atlas(js, buffers, [0, 1, 2], "stinger"))
    positions = np.asarray(launcher.V)
    # The deep, narrow grip is spatially distinct from the forward battery.
    # Anchor its bounding-box centre, not the mesh box or the dense-face mean.
    grip = positions[(positions[:, 0] > -0.009) & (positions[:, 1] < 0.020)]
    if len(grip) == 0:
        raise ValueError("Source changed: cannot locate launcher pistol grip")
    grip_center = (grip.min(axis=0) + grip.max(axis=0)) * 0.5
    scale = LAUNCHER_LENGTH / np.ptp(positions[:, 0])
    # z,x,y is a proper rotation (determinant +1), not a mirrored model.
    launcher.V = [tuple((p - grip_center)[[2, 0, 1]] * scale + GRIP_TARGET)
                  for p in positions]
    launcher.N = [tuple(np.asarray(n)[[2, 0, 1]]) for n in launcher.N]
    print("launcher source grip %s -> %s; uniform scale %.6f" %
          (grip_center, GRIP_TARGET, scale))
    path = save(launcher, "stinger")
    texture = os.path.join(ASSETS, "stinger_diffuse.png")
    iconlib.item_icon(path, texture, os.path.join(ASSETS, "stinger_icon.png"))
    iconlib.weapon_icon(path, texture, os.path.join(ASSETS, "stinger_weapon_icon.png"))
    v, n, indices, uv = iconlib.load(path)
    image = iconlib.render(v, n, indices, uv, iconlib.load_texture(texture),
                           1400, 700, yaw=-0.25, pitch=-0.10)
    image.save(os.path.join(ASSETS, "stinger_preview.png"))

    missile_ids = [3, 4, 5, 6, 7]
    missile = part(data, missile_ids,
                   atlas(js, buffers, missile_ids, "stinger_missile"))
    points = np.asarray(missile.V)
    # Remove the artist's display tilt using the mesh's principal long axis.
    unique = np.unique(points, axis=0)
    _, vectors = np.linalg.eigh(np.cov(unique.T))
    forward = vectors[:, -1]
    if forward[0] > 0:
        forward = -forward
    right = np.cross(np.array([0.0, 0.0, 1.0]), forward)
    right /= np.linalg.norm(right)
    up = np.cross(forward, right)
    rotation = np.stack([right, up, forward])
    points = points @ rotation.T
    center = (points.min(axis=0) + points.max(axis=0)) * 0.5
    # World projectile is metre-sized; do not apply the enlarged hand frame.
    missile.V = [tuple(p) for p in (points - center) * (1.4 / np.ptp(points[:, 2]))]
    missile.N = [tuple(rotation @ np.asarray(n)) for n in missile.N]
    path = save(missile, "stinger_missile")
    v, n, indices, uv = iconlib.load(path)
    # Preview camera uses the weapon frame; asset itself remains nose +Z.
    v = v[:, [0, 2, 1]]
    n = n[:, [0, 2, 1]]
    v[:, 1] *= -1
    n[:, 1] *= -1
    texture = iconlib.load_texture(os.path.join(ASSETS, "stinger_missile_diffuse.png"))
    iconlib.render(v, n, indices, uv, texture, 1000, 350, yaw=0.2, pitch=0.05).save(
        os.path.join(ASSETS, "stinger_missile_preview.png"))


if __name__ == "__main__":
    main()
