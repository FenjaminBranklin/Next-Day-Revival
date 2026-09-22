"""Blender source generator for the Revival toxic crocodile.

Run through crocodile_build.py, not with ordinary CPython.  The model uses
Blender primitives that are joined, bevelled, smooth shaded and UV unwrapped
inside Blender.  The final evaluated mesh is converted to the toolkit's small
ndmesh format for Unity 2018 while the editable .blend remains beside this
script.
"""

import math
import os
import struct

import bpy
from mathutils import Vector


ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "assets")
SOURCE = os.path.join(ASSETS, "src")
MESH_PATH = os.path.join(ASSETS, "crocodile.ndmesh")
DIFFUSE_PATH = os.path.join(ASSETS, "crocodile_diffuse.png")
NORMAL_PATH = os.path.join(ASSETS, "crocodile_normal.png")
BLEND_PATH = os.path.join(SOURCE, "crocodile.blend")
PREVIEW_PATH = os.path.join(SOURCE, "crocodile_preview.png")
TEXTURE_SIZE = 512

os.makedirs(ASSETS, exist_ok=True)
os.makedirs(SOURCE, exist_ok=True)


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.materials,
                       bpy.data.cameras, bpy.data.lights):
        for block in list(datablocks):
            if block.users == 0:
                datablocks.remove(block)


def assign_material(obj, material):
    obj.data.materials.append(material)
    for polygon in obj.data.polygons:
        polygon.material_index = 0


def smooth(obj):
    for polygon in obj.data.polygons:
        polygon.use_smooth = True


def apply_transform(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    obj.select_set(False)


def sphere(name, location, scale, material, segments=16, rings=8, rotation=None):
    bpy.ops.mesh.primitive_uv_sphere_add(
        segments=segments, ring_count=rings, location=location,
        rotation=rotation or (0.0, 0.0, 0.0))
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    apply_transform(obj)
    smooth(obj)
    assign_material(obj, material)
    return obj


def bevelled_cube(name, location, scale, bevel, material, rotation=None):
    bpy.ops.mesh.primitive_cube_add(location=location,
                                   rotation=rotation or (0.0, 0.0, 0.0))
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    apply_transform(obj)
    modifier = obj.modifiers.new("Organic bevel", "BEVEL")
    modifier.width = bevel
    modifier.segments = 3
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    obj.select_set(False)
    smooth(obj)
    assign_material(obj, material)
    return obj


def cone(name, location, radius, depth, material, vertices=6, rotation=None):
    bpy.ops.mesh.primitive_cone_add(
        vertices=vertices, radius1=radius, radius2=0.02, depth=depth,
        location=location, rotation=rotation or (0.0, 0.0, 0.0))
    obj = bpy.context.object
    obj.name = name
    smooth(obj)
    assign_material(obj, material)
    return obj


def material(name):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


def make_texture(path, normal=False):
    size = TEXTURE_SIZE
    pixels = [0.0] * (size * size * 4)
    heights = [0.0] * (size * size)

    for y in range(size):
        v = (y + 0.5) / size
        for x in range(size):
            u = (x + 0.5) / size
            idx = y * size + x
            if v < 0.72:
                # Offset hexagonal scutes, dark in their seams and brighter in
                # the centres.  The extra waves break the computer-perfect grid.
                row = int(v * 46.0)
                hx = (u * 34.0 + (0.5 if row & 1 else 0.0)) % 1.0 - 0.5
                hy = (v * 46.0) % 1.0 - 0.5
                cell = max(0.0, 1.0 - math.sqrt(hx * hx * 2.7 + hy * hy * 3.5))
                noise = (math.sin(u * 91.0 + v * 37.0)
                         + math.sin(u * 43.0 - v * 83.0)) * 0.035
                h = max(0.0, min(1.0, cell * 0.82 + noise + 0.08))
                heights[idx] = h
                r = 0.035 + h * 0.08
                g = 0.105 + h * 0.25
                b = 0.055 + h * 0.08
                # Sparse bioluminescent toxin veins.
                vein = abs(math.sin(u * 24.0 + math.sin(v * 13.0) * 1.8))
                if vein < 0.045:
                    glow = (0.045 - vein) / 0.045
                    r += 0.10 * glow
                    g += 0.58 * glow
                    b += 0.17 * glow
            elif u < 0.50:
                # Dorsal scutes and toxin nodules.
                pulse = 0.5 + 0.5 * math.sin(u * 95.0 + v * 72.0)
                heights[idx] = 0.35 + pulse * 0.15
                r, g, b = 0.09 + pulse * 0.06, 0.52 + pulse * 0.38, 0.16 + pulse * 0.14
            elif u < 0.75:
                # Amber eyes with a narrow reptile pupil.
                pupil = abs((u - 0.625) / 0.125 - 0.5)
                r, g, b = (0.015, 0.02, 0.01) if pupil < 0.09 else (0.82, 0.93, 0.12)
                heights[idx] = 0.1
            else:
                # Teeth and claws.
                grain = 0.04 * math.sin(u * 140.0 + v * 51.0)
                r, g, b = 0.72 + grain, 0.76 + grain, 0.60 + grain * 0.5
                heights[idx] = 0.04

            if not normal:
                off = idx * 4
                pixels[off:off + 4] = [max(0.0, min(1.0, r)),
                                       max(0.0, min(1.0, g)),
                                       max(0.0, min(1.0, b)), 1.0]

    if normal:
        strength = 3.3
        for y in range(size):
            ym = max(0, y - 1)
            yp = min(size - 1, y + 1)
            for x in range(size):
                xm = max(0, x - 1)
                xp = min(size - 1, x + 1)
                dx = (heights[y * size + xp] - heights[y * size + xm]) * strength
                dy = (heights[yp * size + x] - heights[ym * size + x]) * strength
                nx, ny, nz = -dx, -dy, 1.0
                length = math.sqrt(nx * nx + ny * ny + nz * nz)
                off = (y * size + x) * 4
                pixels[off:off + 4] = [nx / length * 0.5 + 0.5,
                                       ny / length * 0.5 + 0.5,
                                       nz / length * 0.5 + 0.5, 1.0]

    name = os.path.splitext(os.path.basename(path))[0]
    image = bpy.data.images.new(name, width=size, height=size, alpha=True)
    image.pixels.foreach_set(pixels)
    image.filepath_raw = path
    image.file_format = "PNG"
    if normal:
        image.colorspace_settings.name = "Non-Color"
    image.save()
    return image


def connect_material(mat, diffuse, normal):
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    for node in list(nodes):
        nodes.remove(node)
    output = nodes.new("ShaderNodeOutputMaterial")
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    shader.inputs["Roughness"].default_value = 0.48
    shader.inputs["Metallic"].default_value = 0.08
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = diffuse
    ntex = nodes.new("ShaderNodeTexImage")
    ntex.image = normal
    ntex.image.colorspace_settings.name = "Non-Color"
    nmap = nodes.new("ShaderNodeNormalMap")
    nmap.inputs["Strength"].default_value = 0.55
    links.new(tex.outputs["Color"], shader.inputs["Base Color"])
    links.new(ntex.outputs["Color"], nmap.inputs["Color"])
    links.new(nmap.outputs["Normal"], shader.inputs["Normal"])
    if "Emission Color" in shader.inputs:
        shader.inputs["Emission Color"].default_value = (0.01, 0.08, 0.015, 1.0)
        shader.inputs["Emission Strength"].default_value = 0.3
    links.new(shader.outputs["BSDF"], output.inputs["Surface"])


def build_crocodile(materials):
    scales, toxin, eyes, teeth = materials
    parts = []

    parts.append(sphere("Armoured body", (0.0, 0.0, 0.20),
                        (1.12, 2.25, 0.52), scales, 24, 12))
    parts.append(sphere("Shoulders", (0.0, 1.45, 0.27),
                        (0.92, 1.15, 0.46), scales, 20, 10))
    parts.append(bevelled_cube("Skull", (0.0, 2.40, 0.27),
                               (0.77, 0.82, 0.34), 0.19, scales))
    parts.append(bevelled_cube("Snout", (0.0, 3.18, 0.12),
                               (0.67, 0.69, 0.23), 0.15, scales))
    parts.append(bevelled_cube("Lower jaw", (0.0, 3.13, -0.10),
                               (0.59, 0.63, 0.12), 0.09, scales))

    tail = [
        ((0.0, -1.95, 0.14), (0.88, 1.18, 0.40)),
        ((0.0, -2.92, 0.11), (0.67, 1.00, 0.32)),
        ((0.0, -3.72, 0.08), (0.47, 0.78, 0.24)),
        ((0.0, -4.34, 0.05), (0.31, 0.59, 0.17)),
        ((0.0, -4.80, 0.03), (0.16, 0.43, 0.10)),
    ]
    for index, (location, scale) in enumerate(tail):
        parts.append(sphere("Tail %02d" % index, location, scale,
                            scales, 16, 8))

    for side in (-1.0, 1.0):
        for fore, y in ((True, 1.22), (False, -1.08)):
            x = side * (1.02 if fore else 1.00)
            rotation = (0.0, (0.30 if fore else -0.22) * side,
                        (-0.33 if fore else 0.26) * side)
            parts.append(sphere(
                ("Front" if fore else "Rear") + " leg",
                (x, y, -0.04), (0.62, 0.27, 0.22), scales,
                14, 7, rotation))
            parts.append(sphere(
                ("Front" if fore else "Rear") + " webbed foot",
                (side * 1.48, y + (0.12 if fore else -0.08), -0.15),
                (0.42, 0.31, 0.09), scales, 12, 6, rotation))

    # Two irregular rows of armour plates make the silhouette recognisably
    # crocodilian even when only the back breaks the water surface.
    plate_index = 0
    for y in [i * 0.43 - 3.65 for i in range(14)]:
        taper = max(0.28, min(1.0, (y + 4.9) / 2.0)) if y < -1.6 else 1.0
        for side in (-1.0, 1.0):
            x = side * (0.24 + 0.05 * math.sin(y * 2.1)) * taper
            z = 0.53 * taper + 0.16
            parts.append(cone("Toxic scute %02d" % plate_index,
                              (x, y, z), 0.15 * taper, 0.27 * taper,
                              toxin, 5))
            plate_index += 1

    for side in (-1.0, 1.0):
        parts.append(sphere("Eye", (side * 0.48, 2.83, 0.61),
                            (0.14, 0.18, 0.14), eyes, 14, 7))
        parts.append(sphere("Toxin gland", (side * 0.67, 2.13, 0.54),
                            (0.18, 0.25, 0.13), toxin, 12, 6))

    tooth_index = 0
    for y in (2.73, 3.02, 3.30, 3.56):
        for side in (-1.0, 1.0):
            parts.append(cone("Tooth %02d" % tooth_index,
                              (side * 0.55, y, -0.02), 0.065, 0.23,
                              teeth, 7, (math.pi, 0.0, 0.0)))
            tooth_index += 1

    bpy.ops.object.select_all(action="DESELECT")
    for part in parts:
        part.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    croc = bpy.context.object
    croc.name = "NDR_Toxic_Crocodile"

    # One triangulated, smart-unwrapped mesh is the runtime contract. Material
    # islands are remapped into explicit atlas bands after the unwrap.
    bpy.context.view_layer.objects.active = croc
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(62.0), island_margin=0.012)
    bpy.ops.object.mode_set(mode="OBJECT")

    uv_layer = croc.data.uv_layers.active
    regions = {
        "Crocodile scales": (0.0, 0.0, 1.0, 0.72),
        "Toxic scutes": (0.0, 0.72, 0.50, 1.0),
        "Reptile eyes": (0.50, 0.72, 0.75, 1.0),
        "Ivory teeth": (0.75, 0.72, 1.0, 1.0),
    }
    slots = [slot.material.name if slot.material else "" for slot in croc.material_slots]
    for polygon in croc.data.polygons:
        key = slots[polygon.material_index]
        u0, v0, u1, v1 = regions.get(key, regions["Crocodile scales"])
        for loop_index in polygon.loop_indices:
            uv = uv_layer.data[loop_index].uv
            uv.x = u0 + uv.x * (u1 - u0)
            uv.y = v0 + uv.y * (v1 - v0)

    triangulate = croc.modifiers.new("Runtime triangles", "TRIANGULATE")
    bpy.context.view_layer.objects.active = croc
    bpy.ops.object.modifier_apply(modifier=triangulate.name)
    return croc


def loop_normal(mesh, loop_index):
    try:
        return mesh.corner_normals[loop_index].vector
    except (AttributeError, IndexError):
        loop = mesh.loops[loop_index]
        try:
            return loop.normal
        except AttributeError:
            return mesh.vertices[loop.vertex_index].normal


def write_ndmesh(obj, path):
    mesh = obj.data
    mesh.calc_loop_triangles()
    uv_layer = mesh.uv_layers.active
    vertices, normals, uvs, indices = [], [], [], []
    matrix = obj.matrix_world
    normal_matrix = matrix.to_3x3()

    for triangle in mesh.loop_triangles:
        base = len(vertices)
        for loop_index in triangle.loops:
            loop = mesh.loops[loop_index]
            point = matrix @ mesh.vertices[loop.vertex_index].co
            normal = (normal_matrix @ loop_normal(mesh, loop_index)).normalized()
            uv = uv_layer.data[loop_index].uv
            # Blender: x right, y forward, z up. Unity: x right, y up,
            # z forward. Swapping axes changes handedness, so the triangle
            # indices below are reversed exactly once.
            vertices.append((point.x, point.z, point.y))
            normals.append((normal.x, normal.z, normal.y))
            uvs.append((max(0.0, min(1.0, uv.x)),
                        max(0.0, min(1.0, uv.y))))
        indices.extend((base, base + 2, base + 1))

    for normal in normals:
        length = math.sqrt(sum(component * component for component in normal))
        if not 0.99 < length < 1.01:
            raise RuntimeError("non-unit crocodile normal")
    winding_errors = 0
    signed_volume = 0.0
    for i in range(0, len(indices), 3):
        a = Vector(vertices[indices[i]])
        b = Vector(vertices[indices[i + 1]])
        c = Vector(vertices[indices[i + 2]])
        face = (b - a).cross(c - a)
        if face.length_squared < 1.0e-12:
            raise RuntimeError("degenerate crocodile triangle")
        normal = Vector(normals[indices[i]])
        if face.dot(normal) < 0.0:
            winding_errors += 1
        signed_volume += a.dot(b.cross(c)) / 6.0
    if winding_errors:
        raise RuntimeError("%d crocodile triangles face inward" % winding_errors)
    if signed_volume <= 0.0:
        raise RuntimeError("crocodile signed volume is not positive")

    with open(path, "wb") as handle:
        handle.write(b"NDMS")
        handle.write(struct.pack("<i", 1))
        handle.write(struct.pack("<i", len(vertices)))
        for point in vertices:
            handle.write(struct.pack("<3f", *point))
        for normal in normals:
            handle.write(struct.pack("<3f", *normal))
        for uv in uvs:
            handle.write(struct.pack("<2f", *uv))
        handle.write(struct.pack("<i", len(indices)))
        for index in indices:
            handle.write(struct.pack("<i", index))

    xs = [point[0] for point in vertices]
    ys = [point[1] for point in vertices]
    zs = [point[2] for point in vertices]
    print("crocodile.ndmesh: %d vertices, %d triangles, bounds %.2f x %.2f x %.2f m, volume %.2f" %
          (len(vertices), len(indices) // 3,
           max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs),
           signed_volume))


def preview_scene(croc):
    bpy.ops.mesh.primitive_plane_add(size=30.0, location=(0.0, 0.0, -0.17))
    water = bpy.context.object
    water.name = "Preview water"
    water_mat = material("Preview water material")
    shader = water_mat.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (0.015, 0.11, 0.13, 1.0)
    shader.inputs["Roughness"].default_value = 0.20
    shader.inputs["Metallic"].default_value = 0.35
    assign_material(water, water_mat)

    bpy.ops.object.camera_add(location=(7.7, 8.8, 5.1))
    camera = bpy.context.object
    camera.name = "Crocodile preview camera"
    camera.data.lens = 55.0
    camera.rotation_euler = ((Vector((0.0, 0.2, 0.25)) - camera.location)
                             .to_track_quat("-Z", "Y").to_euler())
    bpy.context.scene.camera = camera

    bpy.ops.object.light_add(type="AREA", location=(1.5, 2.0, 7.0))
    key = bpy.context.object
    key.name = "Preview key"
    key.data.energy = 1250.0
    key.data.shape = "DISK"
    key.data.size = 5.0

    bpy.ops.object.light_add(type="AREA", location=(-5.0, 1.0, 2.8))
    fill = bpy.context.object
    fill.name = "Preview toxic rim"
    fill.data.energy = 900.0
    fill.data.color = (0.12, 1.0, 0.20)
    fill.data.size = 4.0
    fill.rotation_euler = ((croc.location - fill.location)
                           .to_track_quat("-Z", "Y").to_euler())

    bpy.ops.object.light_add(type="SUN", location=(0.0, 0.0, 8.0))
    sun = bpy.context.object
    sun.name = "Preview sun"
    sun.data.energy = 1.4
    sun.rotation_euler = (math.radians(28.0), math.radians(-24.0),
                          math.radians(22.0))

    scene = bpy.context.scene
    # Blender 4.x used BLENDER_EEVEE_NEXT while the 5.2 LTS build restored
    # BLENDER_EEVEE. Pick the available real-time renderer by capability.
    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = 900
    scene.render.resolution_y = 560
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = PREVIEW_PATH
    scene.render.film_transparent = False
    scene.world.color = (0.008, 0.012, 0.008)
    scene.view_settings.look = "AgX - Medium High Contrast"
    bpy.ops.render.render(write_still=True)


def main():
    clear_scene()
    scales = material("Crocodile scales")
    toxin = material("Toxic scutes")
    eyes = material("Reptile eyes")
    teeth = material("Ivory teeth")
    diffuse = make_texture(DIFFUSE_PATH, False)
    normal = make_texture(NORMAL_PATH, True)
    for mat in (scales, toxin, eyes, teeth):
        connect_material(mat, diffuse, normal)

    croc = build_crocodile((scales, toxin, eyes, teeth))
    write_ndmesh(croc, MESH_PATH)
    preview_scene(croc)

    diffuse.filepath = "//../crocodile_diffuse.png"
    normal.filepath = "//../crocodile_normal.png"
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH, compress=False)
    print("Saved " + BLEND_PATH)
    print("Saved " + PREVIEW_PATH)


main()
