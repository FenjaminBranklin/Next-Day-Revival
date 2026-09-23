"""Anti-personnel mine PMN-2, modelled, textured and exported by Blender.

Run with apmine_build.py. One background run builds the mesh from real-size
parts (metres, Blender +Z up), unwraps it, bakes the procedural authoring
materials into four runtime maps, writes the ndmesh the plugin loads, renders
the 300 x 300 inventory icon and a review image from the SAME exported maps,
and saves the editable assets/src/apmine.blend plus a portable GLB.

Shape (artistic reconstruction of the Soviet PMN-2 blast mine, no third-party
model or photograph used): a 125 mm olive plastic body 54 mm tall with a
moulded band, a black rubber pressure diaphragm carrying the raised cross, a
side arming housing with its screw cap, safety pin and pull ring, and a
detonator plug opposite it.

Runtime conventions (ndmesh.py): 1 item unit = 393.5 mm, Unity +Y up, the
base of the mine at y = 0 so a placed mine can be set onto the ground point
directly. Blender (x, y, z) becomes Unity (x, z, y); the triangle order is
reversed with it so the winding stays outward.
"""
import math
import os
import shutil
import struct

import bpy
import bmesh
import numpy as np
from mathutils import Matrix, Vector

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, 'assets')
SOURCE = os.path.join(ASSETS, 'src')
MESH_PATH = os.path.join(ASSETS, 'apmine.ndmesh')
DIFFUSE_PATH = os.path.join(ASSETS, 'apmine_diffuse.png')
NORMAL_PATH = os.path.join(ASSETS, 'apmine_normal.png')
METAL_PATH = os.path.join(ASSETS, 'apmine_metal.png')
ROUGH_PATH = os.path.join(ASSETS, 'apmine_rough.png')
ICON_PATH = os.path.join(ASSETS, 'apmine_icon.png')
BLEND_PATH = os.path.join(SOURCE, 'apmine.blend')
GLB_PATH = os.path.join(SOURCE, 'apmine.glb')
PREVIEW_PATH = os.path.join(SOURCE, 'apmine_preview.png')

TEXTURE_SIZE = 1024
UNIT = 0.3935            # metres per ndmesh item unit (LAW bore convention)
SEGMENTS = 72            # around the body
TRIANGLE_BUDGET = 8000

# Real PMN-2 proportions in metres.
R_BODY = 0.0625
H_TOP = 0.0540           # top of the rubber cross
Z_FUZE = 0.0215          # height of the side arming housing axis


# ------------------------------------------------------------------ geometry

def lathe(bm, profile, segments):
    """Revolve (r, z, material) around Blender Z. The material index belongs
    to the band between a profile point and the next one. r == 0 closes the
    solid at the axis. Face order gives outward normals for a profile that
    runs bottom-centre -> outside -> top-centre."""
    angles = [2.0 * math.pi * i / segments for i in range(segments)]
    rings = []
    for r, z, _m in profile:
        if r < 1e-9:
            rings.append([bm.verts.new((0.0, 0.0, z))])
        else:
            rings.append([bm.verts.new((r * math.cos(a), r * math.sin(a), z)) for a in angles])
    for i in range(len(profile) - 1):
        a_ring, b_ring, mat = rings[i], rings[i + 1], profile[i][2]
        for j in range(segments):
            k = (j + 1) % segments
            if len(a_ring) == 1:
                verts = (a_ring[0], b_ring[k], b_ring[j])
            elif len(b_ring) == 1:
                verts = (a_ring[j], a_ring[k], b_ring[0])
            else:
                verts = (a_ring[j], a_ring[k], b_ring[k], b_ring[j])
            face = bm.faces.new(verts)
            face.material_index = mat


def cylinder(bm, radius, length, segments, matrix, mat, radius2=None):
    """Closed cylinder along local Z from 0 to length, placed by matrix."""
    r2 = radius if radius2 is None else radius2
    bottom = [bm.verts.new(matrix @ Vector((radius * math.cos(2 * math.pi * i / segments),
                                            radius * math.sin(2 * math.pi * i / segments), 0.0)))
              for i in range(segments)]
    top = [bm.verts.new(matrix @ Vector((r2 * math.cos(2 * math.pi * i / segments),
                                         r2 * math.sin(2 * math.pi * i / segments), length)))
           for i in range(segments)]
    for j in range(segments):
        k = (j + 1) % segments
        bm.faces.new((bottom[j], bottom[k], top[k], top[j])).material_index = mat
    bm.faces.new(list(reversed(bottom))).material_index = mat
    bm.faces.new(top).material_index = mat


def box(bm, centre, size, mat):
    result = bmesh.ops.create_cube(bm, size=1.0,
                                   matrix=Matrix.Translation(Vector(centre))
                                   @ Matrix.Diagonal(Vector((size[0], size[1], size[2], 1.0))))
    for v in result['verts']:
        for f in v.link_faces:
            f.material_index = mat


def torus(bm, centre, axis, major, minor, seg_major, seg_minor, mat):
    """Torus whose ring lies in the plane normal to axis."""
    axis = Vector(axis).normalized()
    u = axis.orthogonal().normalized()
    w = axis.cross(u).normalized()
    grid = []
    for i in range(seg_major):
        a = 2 * math.pi * i / seg_major
        radial = u * math.cos(a) + w * math.sin(a)
        ring = []
        for j in range(seg_minor):
            b = 2 * math.pi * j / seg_minor
            p = Vector(centre) + radial * (major + minor * math.cos(b)) + axis * (minor * math.sin(b))
            ring.append(bm.verts.new(p))
        grid.append(ring)
    for i in range(seg_major):
        n = (i + 1) % seg_major
        for j in range(seg_minor):
            m = (j + 1) % seg_minor
            bm.faces.new((grid[i][j], grid[n][j], grid[n][m], grid[i][m])).material_index = mat


def part(name, build, materials, bevel=0.0, bevel_segments=2, bevel_angle=40.0):
    """One authoring part: a bmesh builder, material slots and an optional
    angle-limited bevel, applied so the joined mesh carries it."""
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    build(bm)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-7)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    for m in materials:
        obj.data.materials.append(m)
    if bevel > 0.0:
        mod = obj.modifiers.new('Bevel', 'BEVEL')
        mod.width = bevel
        mod.segments = bevel_segments
        mod.limit_method = 'ANGLE'
        mod.angle_limit = math.radians(bevel_angle)
        mod.harden_normals = False
        apply_modifiers(obj)
    return obj


def apply_modifiers(obj):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = bpy.data.meshes.new_from_object(evaluated)
    old = obj.data
    obj.modifiers.clear()
    obj.data = mesh
    bpy.data.meshes.remove(old)


def build_parts(mats):
    plastic, rubber, fuze, steel = mats
    P, R, F, S = 0, 1, 2, 3
    everything = [plastic, rubber, fuze, steel]

    # The body: one lathe from the underside to the diaphragm's centre.
    profile = [
        (0.0000, 0.0000, P),
        (0.0598, 0.0000, P),     # flat underside
        (0.0612, 0.0012, P),     # base chamfer
        (0.0620, 0.0040, P),
        (0.0623, 0.0118, P),
        (0.0634, 0.0124, P),     # moulded band
        (0.0634, 0.0176, P),
        (0.0623, 0.0182, P),
        (0.0617, 0.0372, P),     # slight draft to the top
        (0.0608, 0.0404, P),
        (0.0596, 0.0414, P),     # rim top
        (0.0524, 0.0414, P),
        (0.0512, 0.0404, P),
        (0.0507, 0.0386, P),     # groove the diaphragm sits in
        (0.0499, 0.0384, R),
        (0.0470, 0.0396, R),     # rubber diaphragm, slightly domed
        (0.0380, 0.0412, R),
        (0.0240, 0.0424, R),
        (0.0100, 0.0430, R),
        (0.0000, 0.0431, R),
    ]
    body = part('PMN2 body', lambda bm: lathe(bm, profile, SEGMENTS), everything)

    # Raised rubber cross of the pressure cover plus the centre boss.
    def cross(bm):
        box(bm, (0.0, 0.0, 0.0452), (0.086, 0.0175, 0.0100), R)
        box(bm, (0.0, 0.0, 0.0452), (0.0175, 0.086, 0.0100), R)
    cross_obj = part('PMN2 pressure cross', cross, everything,
                     bevel=0.0026, bevel_segments=3, bevel_angle=30.0)

    def boss(bm):
        cylinder(bm, 0.0125, 0.0112, 40, Matrix.Translation((0, 0, 0.0392)), R, radius2=0.0112)
    boss_obj = part('PMN2 pressure boss', boss, everything,
                    bevel=0.0018, bevel_segments=3, bevel_angle=30.0)

    # Side arming housing along +X: sleeve, screw cap, safety pin, pull ring.
    along_x = Matrix.Rotation(math.radians(90.0), 4, 'Y')

    def housing(bm):
        cylinder(bm, 0.0086, 0.0165, 32,
                 Matrix.Translation((0.0555, 0.0, Z_FUZE)) @ along_x, F)
    housing_obj = part('PMN2 arming housing', housing, everything,
                       bevel=0.0009, bevel_segments=2, bevel_angle=40.0)

    def cap(bm):
        cylinder(bm, 0.0099, 0.0052, 18,
                 Matrix.Translation((0.0712, 0.0, Z_FUZE)) @ along_x, F)
    cap_obj = part('PMN2 arming cap', cap, everything,
                   bevel=0.0007, bevel_segments=2, bevel_angle=30.0)

    pin_x = 0.0738

    def pin(bm):
        cylinder(bm, 0.00115, 0.0250, 10,
                 Matrix.Translation((pin_x, -0.0125, Z_FUZE))
                 @ Matrix.Rotation(math.radians(-90.0), 4, 'X'), S)
    pin_obj = part('PMN2 safety pin', pin, everything)

    def ring(bm):
        torus(bm, (pin_x, 0.0132, Z_FUZE - 0.0088), (1.0, 0.0, 0.0),
              0.0088, 0.00125, 32, 8, S)
    ring_obj = part('PMN2 pull ring', ring, everything)

    # Detonator plug opposite the arming housing, with a screwdriver slot.
    def plug(bm):
        cylinder(bm, 0.0072, 0.0032, 24,
                 Matrix.Translation((-0.0605, 0.0, Z_FUZE))
                 @ Matrix.Rotation(math.radians(-90.0), 4, 'Y'), F)
        box(bm, (-0.0640, 0.0, Z_FUZE), (0.0012, 0.0100, 0.0016), F)
    plug_obj = part('PMN2 detonator plug', plug, everything,
                    bevel=0.0006, bevel_segments=2, bevel_angle=40.0)

    return [body, cross_obj, boss_obj, housing_obj, cap_obj, pin_obj, ring_obj, plug_obj]


def join(parts):
    bpy.ops.object.select_all(action='DESELECT')
    for obj in parts:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    obj = bpy.context.object
    obj.name = 'NDR_APMine_PMN2_GAME_MESH'
    tri = obj.modifiers.new('Runtime triangles', 'TRIANGULATE')
    tri.quad_method = 'BEAUTY'
    apply_modifiers(obj)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.dissolve_degenerate(bm, dist=1e-6, edges=bm.edges)
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 3])
    bm.to_mesh(obj.data)
    bm.free()
    for poly in obj.data.polygons:
        poly.use_smooth = True
    if hasattr(obj.data, 'set_sharp_from_angle'):
        obj.data.set_sharp_from_angle(angle=math.radians(48.0))
    obj.data.update()
    return obj


def unwrap(obj):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(62.0), island_margin=0.012,
                             area_weight=0.0, scale_to_bounds=False)
    bpy.ops.uv.pack_islands(margin=0.008, rotate=True)
    bpy.ops.object.mode_set(mode='OBJECT')


# ----------------------------------------------------------------- materials

def node(tree, kind, location, **values):
    n = tree.nodes.new(kind)
    n.location = location
    for key, value in values.items():
        n.inputs[key].default_value = value
    return n


def mix_color(tree, location, factor, a, b):
    """ShaderNodeMix in RGBA mode; inputs 6/7 are the colour pair."""
    n = tree.nodes.new('ShaderNodeMix')
    n.data_type = 'RGBA'
    n.location = location
    link_or_set(tree, factor, n.inputs[0])
    link_or_set(tree, a, n.inputs[6])
    link_or_set(tree, b, n.inputs[7])
    return n.outputs[2]


def link_or_set(tree, value, socket):
    if isinstance(value, bpy.types.NodeSocket):
        tree.links.new(value, socket)
    else:
        socket.default_value = value


def ramp(tree, location, source, stops):
    n = tree.nodes.new('ShaderNodeValToRGB')
    n.location = location
    elements = n.color_ramp.elements
    while len(elements) > len(stops):
        elements.remove(elements[-1])
    while len(elements) < len(stops):
        elements.new(0.5)
    for element, (pos, colour) in zip(elements, stops):
        element.position = pos
        element.color = colour
    tree.links.new(source, n.inputs['Fac'])
    return n.outputs['Color']


def maprange(tree, location, source, a, b):
    n = tree.nodes.new('ShaderNodeMapRange')
    n.location = location
    n.clamp = True
    tree.links.new(source, n.inputs['Value'])
    n.inputs['From Min'].default_value = a
    n.inputs['From Max'].default_value = b
    return n.outputs['Result']


def rgb(r, g, b):
    return (r, g, b, 1.0)


def authoring_material(name, base, dark, rough, metallic, grain, bump_scale,
                       wear=None, dust=0.35, cavity=0.55):
    """Procedural material in object space (seamless over UV seams):
    two-tone mottling, cavity darkening from Cycles AO, dust on the upward
    faces and in the pores, optional bright wear on sharp edges, roughness
    breakup and a fine grain bump."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    tree = mat.node_tree
    bsdf = tree.nodes.get('Principled BSDF')
    bsdf.location = (900, 0)
    coords = tree.nodes.new('ShaderNodeTexCoord')
    coords.location = (-1300, 0)
    obj_space = coords.outputs['Object']

    mottle = node(tree, 'ShaderNodeTexNoise', (-1000, 300), Scale=90.0, Detail=6.0, Roughness=0.62)
    tree.links.new(obj_space, mottle.inputs['Vector'])
    colour = ramp(tree, (-760, 300), mottle.outputs['Fac'],
                  [(0.30, rgb(*dark)), (0.72, rgb(*base))])

    ao = tree.nodes.new('ShaderNodeAmbientOcclusion')
    ao.location = (-760, 0)
    ao.samples = 24
    ao.inputs['Distance'].default_value = 0.006
    cav = maprange(tree, (-520, 0), ao.outputs['AO'], 0.25, 0.95)
    darkened = mix_color(tree, (-300, 200), cav,
                         rgb(base[0] * cavity, base[1] * cavity, base[2] * cavity), colour)

    geometry = tree.nodes.new('ShaderNodeNewGeometry')
    geometry.location = (-1000, -300)
    if wear is not None:
        edges = maprange(tree, (-760, -300), geometry.outputs['Pointiness'], 0.53, 0.62)
        scuff = node(tree, 'ShaderNodeTexNoise', (-760, -520), Scale=260.0, Detail=4.0)
        tree.links.new(obj_space, scuff.inputs['Vector'])
        scuff_mask = maprange(tree, (-520, -520), scuff.outputs['Fac'], 0.42, 0.62)
        mult = tree.nodes.new('ShaderNodeMath')
        mult.operation = 'MULTIPLY'
        mult.location = (-300, -300)
        tree.links.new(edges, mult.inputs[0])
        tree.links.new(scuff_mask, mult.inputs[1])
        darkened = mix_color(tree, (-80, 200), mult.outputs[0], darkened, rgb(*wear))

    # Dust: upward normals plus a large blotchy breakup, and in the cavities.
    sep = tree.nodes.new('ShaderNodeSeparateXYZ')
    sep.location = (-1000, -700)
    tree.links.new(geometry.outputs['Normal'], sep.inputs['Vector'])
    up = maprange(tree, (-760, -700), sep.outputs['Z'], 0.35, 1.0)
    blotch = node(tree, 'ShaderNodeTexNoise', (-760, -900), Scale=38.0, Detail=3.0)
    tree.links.new(obj_space, blotch.inputs['Vector'])
    blotch_mask = maprange(tree, (-520, -900), blotch.outputs['Fac'], 0.45, 0.70)
    inv_cav = tree.nodes.new('ShaderNodeMath')
    inv_cav.operation = 'SUBTRACT'
    inv_cav.location = (-520, -700)
    inv_cav.inputs[0].default_value = 1.0
    tree.links.new(cav, inv_cav.inputs[1])
    dust_sum = tree.nodes.new('ShaderNodeMath')
    dust_sum.operation = 'MULTIPLY_ADD'
    dust_sum.location = (-300, -700)
    tree.links.new(up, dust_sum.inputs[0])
    tree.links.new(blotch_mask, dust_sum.inputs[1])
    tree.links.new(inv_cav.outputs[0], dust_sum.inputs[2])
    dust_amount = tree.nodes.new('ShaderNodeMath')
    dust_amount.operation = 'MULTIPLY'
    dust_amount.use_clamp = True
    dust_amount.location = (-80, -700)
    tree.links.new(dust_sum.outputs[0], dust_amount.inputs[0])
    dust_amount.inputs[1].default_value = dust
    final_colour = mix_color(tree, (160, 200), dust_amount.outputs[0], darkened,
                             rgb(0.170, 0.132, 0.082))
    tree.links.new(final_colour, bsdf.inputs['Base Color'])

    # Roughness: base value, breakup, dust is matte.
    breakup = node(tree, 'ShaderNodeTexNoise', (-520, -1150), Scale=140.0, Detail=5.0)
    tree.links.new(obj_space, breakup.inputs['Vector'])
    rough_map = tree.nodes.new('ShaderNodeMapRange')
    rough_map.location = (-300, -1150)
    rough_map.clamp = True
    tree.links.new(breakup.outputs['Fac'], rough_map.inputs['Value'])
    rough_map.inputs['From Min'].default_value = 0.3
    rough_map.inputs['From Max'].default_value = 0.7
    rough_map.inputs['To Min'].default_value = max(0.05, rough - 0.10)
    rough_map.inputs['To Max'].default_value = min(1.0, rough + 0.10)
    rough_dust = tree.nodes.new('ShaderNodeMix')
    rough_dust.data_type = 'FLOAT'
    rough_dust.location = (160, -1000)
    tree.links.new(dust_amount.outputs[0], rough_dust.inputs[0])
    tree.links.new(rough_map.outputs['Result'], rough_dust.inputs[2])
    rough_dust.inputs[3].default_value = 0.95
    tree.links.new(rough_dust.outputs[0], bsdf.inputs['Roughness'])

    bsdf.inputs['Metallic'].default_value = metallic

    # Fine grain bump: moulded plastic, stippled rubber, machined steel.
    fine = node(tree, 'ShaderNodeTexNoise', (-520, -1400), Scale=grain, Detail=8.0, Roughness=0.7)
    tree.links.new(obj_space, fine.inputs['Vector'])
    bump = tree.nodes.new('ShaderNodeBump')
    bump.location = (500, -600)
    bump.inputs['Strength'].default_value = bump_scale
    bump.inputs['Distance'].default_value = 0.0004
    tree.links.new(fine.outputs['Fac'], bump.inputs['Height'])
    tree.links.new(bump.outputs['Normal'], bsdf.inputs['Normal'])
    return mat


def authoring_materials():
    # Linear colours. Olive plastic after RAL 6014 / the TM-62's own olive.
    plastic = authoring_material('PMN2 olive plastic', base=(0.070, 0.082, 0.036),
                                 dark=(0.050, 0.060, 0.026), rough=0.58, metallic=0.0,
                                 grain=700.0, bump_scale=0.10, wear=(0.120, 0.128, 0.078))
    rubber = authoring_material('PMN2 black rubber', base=(0.016, 0.016, 0.015),
                                dark=(0.009, 0.009, 0.009), rough=0.86, metallic=0.0,
                                grain=1600.0, bump_scale=0.30, dust=0.22, cavity=0.6)
    fuze = authoring_material('PMN2 arming housing paint', base=(0.046, 0.052, 0.026),
                              dark=(0.032, 0.036, 0.018), rough=0.46, metallic=0.0,
                              grain=900.0, bump_scale=0.08, wear=(0.20, 0.20, 0.19))
    steel = authoring_material('PMN2 steel pin', base=(0.42, 0.41, 0.39),
                               dark=(0.26, 0.24, 0.21), rough=0.34, metallic=1.0,
                               grain=2000.0, bump_scale=0.05, dust=0.2, cavity=0.5)
    return [plastic, rubber, fuze, steel]


# --------------------------------------------------------------------- bake

def new_image(name, alpha=False, colour=True):
    image = bpy.data.images.new(name, width=TEXTURE_SIZE, height=TEXTURE_SIZE, alpha=alpha)
    image.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
    return image


def target(obj, image):
    for mat in obj.data.materials:
        tree = mat.node_tree
        n = tree.nodes.get('NDR bake target')
        if n is None:
            n = tree.nodes.new('ShaderNodeTexImage')
            n.name = 'NDR bake target'
            n.location = (900, 500)
        n.image = image
        for other in tree.nodes:
            other.select = False
        n.select = True
        tree.nodes.active = n


def route_to_emission(obj, socket_name):
    """Temporarily shade every material as pure emission of what feeds one
    Principled input, so an EMIT bake records that input exactly (unlit,
    no metallic darkening of the albedo)."""
    saved = []
    for mat in obj.data.materials:
        tree = mat.node_tree
        bsdf = tree.nodes.get('Principled BSDF')
        out = tree.nodes.get('Material Output')
        emit = tree.nodes.new('ShaderNodeEmission')
        emit.name = 'NDR bake emission'
        emit.inputs['Strength'].default_value = 1.0
        source = bsdf.inputs[socket_name]
        if source.is_linked:
            tree.links.new(source.links[0].from_socket, emit.inputs['Color'])
        else:
            v = source.default_value
            emit.inputs['Color'].default_value = (tuple(v) if hasattr(v, '__len__')
                                                  else (v, v, v, 1.0))
        old = out.inputs['Surface'].links[0].from_socket
        tree.links.new(emit.outputs['Emission'], out.inputs['Surface'])
        saved.append((tree, old, out, emit))
    return saved


def restore(saved):
    for tree, old, out, emit in saved:
        tree.links.new(old, out.inputs['Surface'])
        tree.nodes.remove(emit)


def bake(obj, kind, image, **options):
    target(obj, image)
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.bake(type=kind, margin=8, use_clear=True, **options)


def pixels(image):
    data = np.empty(TEXTURE_SIZE * TEXTURE_SIZE * 4, np.float32)
    image.pixels.foreach_get(data)
    return data.reshape(TEXTURE_SIZE, TEXTURE_SIZE, 4)


def save(image, path):
    image.filepath_raw = path
    image.file_format = 'PNG'
    image.save()


def bake_maps(obj):
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.samples = 48
    scene.render.bake.margin = 8

    albedo = new_image('PMN2 albedo %d' % TEXTURE_SIZE)
    saved = route_to_emission(obj, 'Base Color')
    bake(obj, 'EMIT', albedo)
    restore(saved)
    save(albedo, DIFFUSE_PATH)

    rough = new_image('PMN2 roughness %d' % TEXTURE_SIZE, colour=False)
    saved = route_to_emission(obj, 'Roughness')
    bake(obj, 'EMIT', rough)
    restore(saved)

    metal = new_image('PMN2 metallic %d' % TEXTURE_SIZE, colour=False)
    saved = route_to_emission(obj, 'Metallic')
    bake(obj, 'EMIT', metal)
    restore(saved)

    normal = new_image('PMN2 tangent normal %d' % TEXTURE_SIZE, colour=False)
    bake(obj, 'NORMAL', normal, normal_space='TANGENT',
         normal_r='POS_X', normal_g='POS_Y', normal_b='POS_Z')
    save(normal, NORMAL_PATH)

    # Unity maps (ItemFactory.MakeMaterial): <stem>_metal.png carries metallic
    # in RGB and SMOOTHNESS in alpha; <stem>_rough.png is roughness in RGB.
    r = pixels(rough)[..., 0]
    m = pixels(metal)[..., 0]
    gloss = np.zeros((TEXTURE_SIZE, TEXTURE_SIZE, 4), np.float32)
    gloss[..., 0] = gloss[..., 1] = gloss[..., 2] = m
    gloss[..., 3] = 1.0 - r
    metal_out = bpy.data.images.new('PMN2 metallic-gloss', TEXTURE_SIZE, TEXTURE_SIZE, alpha=True)
    metal_out.colorspace_settings.name = 'Non-Color'
    metal_out.alpha_mode = 'STRAIGHT'
    metal_out.pixels.foreach_set(gloss.ravel())
    save(metal_out, METAL_PATH)
    rough_out = bpy.data.images.new('PMN2 roughness-rgb', TEXTURE_SIZE, TEXTURE_SIZE, alpha=False)
    rough_out.colorspace_settings.name = 'Non-Color'
    rgb_rough = np.ones((TEXTURE_SIZE, TEXTURE_SIZE, 4), np.float32)
    rgb_rough[..., 0] = rgb_rough[..., 1] = rgb_rough[..., 2] = r
    rough_out.pixels.foreach_set(rgb_rough.ravel())
    save(rough_out, ROUGH_PATH)
    for image in (rough, metal):
        bpy.data.images.remove(image)
    return DIFFUSE_PATH, NORMAL_PATH, METAL_PATH, ROUGH_PATH


def runtime_material():
    """The material the game builds, from the files on disk."""
    def load(path, colour):
        image = bpy.data.images.load(path, check_existing=False)
        image.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
        return image
    albedo = load(DIFFUSE_PATH, True)
    normal = load(NORMAL_PATH, False)
    gloss = load(METAL_PATH, False)
    mat = bpy.data.materials.new('PMN2 runtime maps')
    mat.use_nodes = True
    tree = mat.node_tree
    bsdf = tree.nodes.get('Principled BSDF')
    t_albedo = node(tree, 'ShaderNodeTexImage', (-600, 300))
    t_albedo.image = albedo
    t_normal = node(tree, 'ShaderNodeTexImage', (-600, -300))
    t_normal.image = normal
    t_gloss = node(tree, 'ShaderNodeTexImage', (-600, 0))
    t_gloss.image = gloss
    nmap = tree.nodes.new('ShaderNodeNormalMap')
    nmap.location = (-250, -300)
    tree.links.new(t_albedo.outputs['Color'], bsdf.inputs['Base Color'])
    tree.links.new(t_normal.outputs['Color'], nmap.inputs['Color'])
    tree.links.new(nmap.outputs['Normal'], bsdf.inputs['Normal'])
    sep = tree.nodes.new('ShaderNodeSeparateColor')
    sep.location = (-300, 80)
    tree.links.new(t_gloss.outputs['Color'], sep.inputs['Color'])
    tree.links.new(sep.outputs[0], bsdf.inputs['Metallic'])
    inv = tree.nodes.new('ShaderNodeMath')
    inv.operation = 'SUBTRACT'
    inv.location = (-300, -80)
    inv.inputs[0].default_value = 1.0
    tree.links.new(t_gloss.outputs['Alpha'], inv.inputs[1])
    tree.links.new(inv.outputs[0], bsdf.inputs['Roughness'])
    return mat, (albedo, normal, gloss)


# ------------------------------------------------------------------- export

def write_ndmesh(obj, path):
    mesh = obj.data
    mesh.calc_loop_triangles()
    uv_layer = mesh.uv_layers.active
    vertices, normals, uvs, indices = [], [], [], []
    shared = {}
    matrix = obj.matrix_world
    scale = 1.0 / UNIT
    for triangle in mesh.loop_triangles:
        ids = []
        for loop_index in triangle.loops:
            loop = mesh.loops[loop_index]
            normal = mesh.corner_normals[loop_index].vector
            uv = uv_layer.data[loop_index].uv
            key = (loop.vertex_index, round(uv.x, 6), round(uv.y, 6),
                   round(normal.x, 5), round(normal.y, 5), round(normal.z, 5))
            if key not in shared:
                shared[key] = len(vertices)
                p = (matrix @ mesh.vertices[loop.vertex_index].co) * scale
                n = (matrix.to_3x3() @ normal).normalized()
                vertices.append((p.x, p.z, p.y))
                normals.append((n.x, n.z, n.y))
                uvs.append((min(1.0, max(0.0, uv.x)), min(1.0, max(0.0, uv.y))))
            ids.append(shared[key])
        indices.extend((ids[0], ids[2], ids[1]))

    bad_normals = 0
    volume = 0.0
    for offset in range(0, len(indices), 3):
        a, b, c = [Vector(vertices[indices[offset + i]]) for i in range(3)]
        face = (b - a).cross(c - a)
        if face.length_squared < 1e-16:
            raise RuntimeError('Degenerate exported triangle at %s' % (tuple(a),))
        if any(face.dot(Vector(normals[indices[offset + i]])) < 0 for i in range(3)):
            bad_normals += 1
        volume += a.dot(b.cross(c)) / 6.0
    # A smooth normal may lean past a sliver's face normal on the bevel rounds;
    # a handful is harmless, a winding flip would show as thousands.
    if volume <= 0 or bad_normals > len(indices) // 3 // 50:
        raise RuntimeError('Invalid winding: %d faces, volume %.6f' % (bad_normals, volume))
    if any(not 0.99 < sum(c * c for c in n) < 1.01 for n in normals):
        raise RuntimeError('Invalid normal')
    triangles = len(indices) // 3
    if triangles > TRIANGLE_BUDGET:
        raise RuntimeError('PMN-2 exceeds its %d triangle budget: %d' % (TRIANGLE_BUDGET, triangles))
    with open(path, 'wb') as out:
        out.write(b'NDMS' + struct.pack('<ii', 1, len(vertices)))
        for values, fmt in ((vertices, '<3f'), (normals, '<3f'), (uvs, '<2f')):
            for value in values:
                out.write(struct.pack(fmt, *value))
        out.write(struct.pack('<i', len(indices)))
        out.write(struct.pack('<%di' % len(indices), *indices))
    size = [max(p[i] for p in vertices) - min(p[i] for p in vertices) for i in range(3)]
    low = min(p[1] for p in vertices)
    print('PMN-2: %d vertices, %d triangles, %d leaning normals; units XYZ %.3f %.3f %.3f '
          '(%.0f x %.0f x %.0f mm), base y %.4f, volume %.5f'
          % (len(vertices), triangles, bad_normals, size[0], size[1], size[2],
             size[0] * UNIT * 1000, size[1] * UNIT * 1000, size[2] * UNIT * 1000, low, volume),
          flush=True)


# ------------------------------------------------------------------- render

def point_at(obj, location):
    obj.rotation_euler = (Vector(location) - obj.location).to_track_quat('-Z', 'Y').to_euler()


def light(name, location, energy, colour, size, aim):
    data = bpy.data.lights.new(name, 'AREA')
    data.energy = energy
    data.color = colour
    data.shape = 'DISK'
    data.size = size
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = location
    point_at(obj, aim)
    return obj


def render_scene():
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.use_denoising = True
    scene.view_settings.view_transform = 'AgX'
    scene.view_settings.look = 'AgX - Medium High Contrast'
    if scene.world is None:
        scene.world = bpy.data.worlds.new('PMN2 review world')
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes['Background']
    background.inputs[0].default_value = (0.22, 0.245, 0.27, 1.0)
    background.inputs[1].default_value = 0.22
    aim = (0.0, 0.0, 0.025)
    light('Soft key', (0.35, -0.30, 0.55), 7.0, (1.0, 0.95, 0.86), 0.35, aim)
    light('Sky fill', (-0.45, -0.10, 0.35), 2.5, (0.78, 0.86, 1.0), 0.45, aim)
    light('Rim', (-0.10, 0.50, 0.30), 4.0, (1.0, 0.97, 0.90), 0.30, aim)

    mesh = bpy.data.meshes.new('PMN2 review ground')
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=2.0)
    bm.to_mesh(mesh)
    bm.free()
    ground = bpy.data.objects.new('REVIEW ground - shadow catcher, not exported', mesh)
    scene.collection.objects.link(ground)
    ground.is_shadow_catcher = True
    camera_data = bpy.data.cameras.new('PMN2 camera')
    camera_data.type = 'ORTHO'
    camera = bpy.data.objects.new('REVIEW camera', camera_data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    return camera, ground


def render_icon(camera, ground):
    """ItemIcon convention (iconlib.py): 300 x 300, transparent, nearly
    filling, soft drop shadow. Rendered 4x and downsampled."""
    scene = bpy.context.scene
    scene.render.film_transparent = True
    scene.render.resolution_x = scene.render.resolution_y = 1200
    scene.render.resolution_percentage = 100
    scene.cycles.samples = 96
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    camera.location = (0.26, -0.30, 0.30)
    point_at(camera, (0.004, 0.0, 0.020))
    camera.data.ortho_scale = 0.170
    staged = os.path.join(SOURCE, 'apmine_icon_render.png')
    scene.render.filepath = staged
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(staged, check_existing=False)
    image.scale(300, 300)
    image.filepath_raw = ICON_PATH
    image.file_format = 'PNG'
    image.save()
    bpy.data.images.remove(image)
    os.remove(staged)


def render_preview(camera, ground):
    scene = bpy.context.scene
    scene.render.film_transparent = False
    ground.is_shadow_catcher = False
    mat = bpy.data.materials.new('REVIEW dry earth')
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Base Color'].default_value = (0.105, 0.085, 0.060, 1.0)
    bsdf.inputs['Roughness'].default_value = 0.92
    ground.data.materials.append(mat)
    scene.render.resolution_x = 1500
    scene.render.resolution_y = 1000
    scene.cycles.samples = 128
    camera.location = (0.30, -0.36, 0.26)
    point_at(camera, (0.008, 0.0, 0.018))
    camera.data.ortho_scale = 0.24
    scene.render.filepath = PREVIEW_PATH
    bpy.ops.render.render(write_still=True)


# --------------------------------------------------------------------- main

def main():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.lights, bpy.data.cameras):
        for item in list(block):
            block.remove(item)
    bpy.context.preferences.filepaths.save_version = 0
    scene = bpy.context.scene
    scene.unit_settings.system = 'METRIC'

    print('Building PMN-2 parts', flush=True)
    mats = authoring_materials()
    obj = join(build_parts(mats))
    unwrap(obj)

    print('Baking %dpx runtime maps' % TEXTURE_SIZE, flush=True)
    bake_maps(obj)

    # Keep an editable copy with the procedural materials for a re-bake. Made
    # AFTER the bake: a coincident copy would occlude the AO rays.
    source = bpy.data.collections.new('AUTHORING - procedural materials (hidden)')
    scene.collection.children.link(source)
    authoring = obj.copy()
    authoring.data = obj.data.copy()
    authoring.name = 'SOURCE PMN2 procedural'
    source.objects.link(authoring)
    source.hide_render = True
    source.hide_viewport = True
    runtime, images = runtime_material()
    obj.data.materials.clear()
    obj.data.materials.append(runtime)
    for poly in obj.data.polygons:
        poly.material_index = 0
    write_ndmesh(obj, MESH_PATH)

    camera, ground = render_scene()
    render_icon(camera, ground)
    render_preview(camera, ground)

    ground.hide_set(True)
    ground.hide_render = True
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.gltf(filepath=GLB_PATH, export_format='GLB', use_selection=True)

    for image in images:
        image.pack()
    staged = os.path.join(SOURCE, 'apmine_build_%d.blend' % os.getpid())
    bpy.ops.wm.save_as_mainfile(filepath=staged, compress=True)
    shutil.copyfile(staged, BLEND_PATH)
    os.remove(staged)
    print('Saved editable source, runtime maps, icon and review render', flush=True)


main()
