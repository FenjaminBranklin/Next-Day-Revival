"""Original anatomical crocodile, created and exported by Blender.

Run with crocodile_build.py. The preview uses exactly the exported mesh and
texture maps. Blender +Y forward/+Z up becomes Unity +Z forward/+Y up.
"""
import math
import os
import struct
import shutil
import bpy
import bmesh
import numpy as np
from mathutils import Vector, Matrix

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, 'assets')
SOURCE = os.path.join(ASSETS, 'src')
MESH_PATH = os.path.join(ASSETS, 'crocodile.ndmesh')
DIFFUSE_PATH = os.path.join(ASSETS, 'crocodile_diffuse.png')
NORMAL_PATH = os.path.join(ASSETS, 'crocodile_normal.png')
BLEND_PATH = os.path.join(SOURCE, 'crocodile.blend')
PREVIEW_PATH = os.path.join(SOURCE, 'crocodile_preview.png')
RIG_PATH = os.path.join(ASSETS, 'crocodile_rig.bin')
TEXTURE_SIZE = 2048
PARTS = []
REGIONS = {
    'body': (.012, .012, .563, .988),
    'limb': (.581, .012, .796, .554),
    'armor': (.581, .576, .796, .988),
    'jaw': (.813, .012, .988, .510),
    'horn': (.813, .534, .988, .680),
    'eye': (.813, .704, .890, .812),
    'dark': (.915, .704, .988, .812),
    'mouth': (.813, .836, .988, .988),
}


def atlas_uv(region, u, v):
    x0, y0, x1, y1 = REGIONS[region]
    return (x0 + min(.998, max(.002, u)) * (x1-x0),
            y0 + min(.998, max(.002, v)) * (y1-y0))


# Rigid runtime parts. The ndmesh has no armature, so Revival.Crocodile.cs
# turns these vertex sets about their pivots: the jaw opens about its hinge,
# each leg swings about its shoulder or hip. Pivots are Blender coordinates
# (x right, y forward, z up) taken from the anatomy below.
RIG_BODY, RIG_JAW = 0, 1
RIG_PIVOTS = [
    (0.0, 0.0, 0.0),                          # 0 body, head and tail
    (0.0, 1.90, -.025),                       # 1 jaw hinge: first mandible ring
    (-.61, 1.06, .04), (.61, 1.06, .04),      # 2/3 fore limb L/R shoulder
    (-.67, -1.09, .005), (.67, -1.09, .005),  # 4/5 hind limb L/R hip
]


def rig_part(obj):
    # Mandible, mouth floor and lower teeth move with the jaw; every limb
    # piece (tube, palm, digit, claw) goes to the leg whose side and end of
    # the body its centre lies on. Everything else is the body.
    if obj.name.startswith(('02 ', '03 ', '15 ')):
        return RIG_JAW
    if obj.name.startswith(('04 ', '05 ', '06 ', '07 ')):
        world = obj.matrix_world
        centre = Vector()
        for vertex in obj.data.vertices:
            centre += world @ vertex.co
        centre /= max(1, len(obj.data.vertices))
        return (2 if centre.y > 0 else 4) + (1 if centre.x > 0 else 0)
    return RIG_BODY


def mesh_object(name, vertices, faces, uvs, region='body', smooth=True, wrap_uv=False):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh)
    bm.free()
    layer = mesh.uv_layers.new(name='Runtime atlas')
    for poly in mesh.polygons:
        poly.use_smooth = smooth
        values = [uvs[mesh.loops[i].vertex_index] for i in poly.loop_indices]
        wrap = wrap_uv and max(p[0] for p in values)-min(p[0] for p in values) > .7
        for loop_index in poly.loop_indices:
            u, v = uvs[mesh.loops[loop_index].vertex_index]
            if wrap and u < .2:
                u += 1.0
            layer.data[loop_index].uv = atlas_uv(region, u, v)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    PARTS.append(obj)
    return obj


def interpolate(table, position):
    # Cubic Hermite sections avoid a stack of visibly separate primitives.
    i = max(0, min(len(table)-2, next(
        (j for j in range(len(table)-1) if position <= table[j+1][0]), len(table)-2)))
    a, b = table[i], table[i+1]
    before, after = table[max(0, i-1)], table[min(len(table)-1, i+2)]
    t = max(0.0, min(1.0, (position-a[0])/(b[0]-a[0])))
    out = []
    for k in range(1, len(a)):
        ma = (b[k]-before[k])/(b[0]-before[0])
        mb = (after[k]-a[k])/(after[0]-a[0])
        out.append((2*t**3-3*t*t+1)*a[k] + (t**3-2*t*t+t)*ma*(b[0]-a[0])
                   + (-2*t**3+3*t*t)*b[k] + (t**3-t*t)*mb*(b[0]-a[0]))
    return out


# Y, half width, dorsal height, ventral height. A continuous muscular tail and
# torso lead into the neck and tapered skull, with a widened nasal rosette.
BODY = [
    (-5.18,.008,-.09,-.11), (-4.90,.075,.035,-.19),
    (-4.45,.145,.18,-.23), (-3.85,.255,.30,-.29),
    (-3.15,.390,.40,-.34), (-2.45,.570,.49,-.38),
    (-1.80,.760,.59,-.42), (-1.05,.900,.67,-.43),
    (-.20,.940,.70,-.41), (.65,.840,.66,-.37),
    (1.20,.700,.60,-.28), (1.58,.610,.55,-.20),
    (1.91,.690,.57,-.09), (2.18,.640,.51,.004),
    (2.52,.510,.355,.037), (2.93,.410,.262,.030),
    (3.30,.376,.236,.021), (3.59,.407,.260,.015),
    (3.79,.352,.252,.030), (3.91,.025,.148,.082),
]


def body_section(y):
    width, top, bottom = interpolate(BODY, y)
    return max(.004, width), top, bottom


def dorsal(y, x):
    w, top, bottom = body_section(y)
    return (top+bottom)*.5 + (top-bottom)*.5 * math.sqrt(max(.01, 1-(x/w)**2))


def make_body():
    verts, faces, uv = [], [], []
    rings, sides = 99, 36
    for row in range(rings):
        y = -5.18 + 9.09*row/(rings-1)
        width, top, bottom = body_section(y)
        centre, height = (top+bottom)*.5, (top-bottom)*.5
        skull = max(0.0, min(1.0, (y-1.60)/.65))
        for col in range(sides):
            angle = 2*math.pi*col/sides
            sn, cs = math.sin(angle), -math.cos(angle)
            x = width*math.copysign(abs(sn)**(1-.32*skull), sn)
            z = centre+height*math.copysign(abs(cs)**(1-.30*skull), cs)
            if y > 1.5 and cs > 0:
                # Orbital ridges grow from the skull surface and shelter the eye.
                z += .070*math.exp(-((abs(x)-.50)/.19)**2-((y-2.09)/.25)**2)
            if y < 1.4:
                z += (.008*math.sin(y*19+col*.3)*abs(sn)**6
                      * max(0,min(1,(y+4.3)/2)))
            x += .065*math.sin((y+1.8)*1.05)*max(0, min(1, (-y-1.7)/2.5))
            verts.append((x, y, z))
            uv.append((col/sides, row/(rings-1)))
    for row in range(rings-1):
        for col in range(sides):
            a = row*sides+col
            b = row*sides+(col+1)%sides
            faces.append((a,b,b+sides,a+sides))
    faces.extend([tuple(reversed(range(sides))), tuple((rings-1)*sides+i for i in range(sides))])
    return mesh_object('01 Continuous torso skull and swimming tail', verts, faces, uv, wrap_uv=True)


def loft(name, table, region='limb', rings=15, sides=12):
    verts, uv, faces = [], [], []
    for row in range(rings):
        t = row/(rings-1)
        x,y,z,rx,rz = interpolate(table, t)
        for col in range(sides):
            a = col*2*math.pi/sides
            verts.append((x+rx*math.sin(a),y,z-rz*math.cos(a)))
            uv.append((col/sides,t))
    for row in range(rings-1):
        for col in range(sides):
            a = row*sides+col
            b = row*sides+(col+1)%sides
            faces.append((a,b,b+sides,a+sides))
    faces.extend([tuple(reversed(range(sides))),tuple((rings-1)*sides+i for i in range(sides))])
    return mesh_object(name,verts,faces,uv,region,wrap_uv=True)


def tube(name, points, radii, region='limb', sides=10):
    verts, uv, faces = [], [], []
    distance=sum((Vector(points[i])-Vector(points[i-1])).length for i in range(1,len(points)))
    detail_scale=min(1.0,max(r[0] for r in radii)/.22) if region=='limb' else 1.0
    for row, point in enumerate(points):
        point = Vector(point)
        direction = Vector(points[min(len(points)-1,row+1)])-Vector(points[max(0,row-1)])
        direction.normalize()
        sideways = direction.cross(Vector((0,0,1))).normalized()
        up = sideways.cross(direction).normalized()
        rx, rz = radii[row]
        for col in range(sides):
            a = col*2*math.pi/sides
            verts.append(tuple(point + sideways*(rx*math.cos(a)) + up*(rz*math.sin(a))))
            uv.append((col/sides*detail_scale,
                       .15+row/(len(points)-1)*min(.75,distance*.40) if region=='limb'
                       else row/(len(points)-1)))
    for row in range(len(points)-1):
        for col in range(sides):
            a=row*sides+col
            b=row*sides+(col+1)%sides
            faces.append((a,b,b+sides,a+sides))
    faces.extend([tuple(reversed(range(sides))),tuple((len(points)-1)*sides+i for i in range(sides))])
    return mesh_object(name,verts,faces,uv,region,wrap_uv=True)


def ellipsoid(name, centre, scale, region, segments=16, rings=8):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=rings, location=centre)
    obj=bpy.context.object
    obj.name=name
    obj.scale=scale
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    for poly in obj.data.polygons:
        poly.use_smooth=True
    for loop in obj.data.uv_layers.active.data:
        loop.uv=atlas_uv(region,loop.uv.x,loop.uv.y)
    obj.data.uv_layers.active.name='Runtime atlas'
    PARTS.append(obj)
    return obj


def plate(name, x,y,width,length,height):
    # Low beveled osteoderm with a long keel buried into the skin.
    outline=[(-.72,-1),(.72,-1),(1,-.60),(1,.62),(.67,1),(-.67,1),(-1,.60),(-1,-.62)]
    verts,uv=[],[]
    for ring in range(2):
        for px,py in outline:
            xx=x+px*width*(1 if ring==0 else .82)
            yy=y+py*length*(1 if ring==0 else .86)
            zz=dorsal(yy,xx) - .009 + (height*.28 if ring else 0)
            verts.append((xx,yy,zz))
            uv.append((.08+.84*(px*.5+.5),.08+.84*(py*.5+.5)))
    for py in (-.56,.59):
        verts.append((x,y+length*py,dorsal(y+length*py,x)+height))
        uv.append((.5,py*.36+.5))
    faces=[]
    for i in range(8):
        j=(i+1)%8
        faces.append((i,j,8+j,8+i))
    faces.extend([(8,9,16),(9,10,16),(10,11,17,16),(11,12,17),
                  (12,13,17),(13,14,17),(14,15,16,17),(15,8,16),
                  tuple(reversed(range(8)))])
    obj=mesh_object(name,verts,faces,uv,'armor',smooth=False)
    for poly in obj.data.polygons:
        if 8 <= poly.index < 16:
            poly.use_smooth=True
    return obj


def build_anatomy():
    make_body()
    loft('02 Lower mandible',[
        (0,0,1.90,-.025,.45,.09),(.18,0,2.24,-.065,.59,.095),
        (.47,0,2.81,-.048,.415,.079),(.77,0,3.38,-.038,.367,.067),
        (.94,0,3.75,.007,.325,.058),(1,0,3.89,.044,.020,.020)
    ],'jaw',30,24)
    loft('03 Mouth shadow',[(0,0,2.12,.026,.50,.015),(.5,0,2.99,.019,.39,.012),
                            (.94,0,3.75,.052,.30,.014),(1,0,3.86,.075,.018,.008)],
         'mouth',22,16)
    for side in (-1,1):
        label='L' if side<0 else 'R'
        for fore in (True,False):
            if fore:
                points=[(.61,1.06,.04),(.83,.91,-.035),(1.04,.77,-.13),
                        (1.21,.81,-.24),(1.32,.99,-.33),(1.46,1.13,-.375)]
                radii=[(.26,.24),(.24,.21),(.16,.15),(.13,.12),(.13,.09),(.18,.065)]
                palm=(1.46,1.13,-.375)
                count=5
            else:
                points=[(.67,-1.09,.005),(.98,-1.28,-.045),(1.28,-1.46,-.16),
                        (1.26,-1.71,-.26),(1.13,-1.94,-.34),(1.43,-2.10,-.375)]
                radii=[(.36,.30),(.37,.28),(.27,.20),(.18,.145),(.125,.105),(.18,.065)]
                palm=(1.43,-2.10,-.375)
                count=4
            tube(('04 Forelimb ' if fore else '05 Hindlimb ')+label,
                 [(side*x,y,z) for x,y,z in points],radii,sides=16)
            ellipsoid('06 Scaled palm '+label,(side*palm[0],palm[1],palm[2]),
                      (.19,.145,.07),'limb',12,6)
            for digit in range(count):
                spread=(digit-(count-1)*.5)
                dx=.10*spread
                dy=(.21 if fore else -.23) + (.045 if fore else -.045)*(2-abs(spread))
                x,y,z=palm
                toe=[(side*(x+dx*.56),y+(.015 if fore else -.01),z),
                     (side*(x+dx),y+dy*.48,z-.012),
                     (side*(x+dx*1.45+.065),y+dy,z-.02)]
                radius=.048 if fore else .057
                tube('06 Articulated digit %s %s %d'%(label,fore,digit),toe,
                     [(radius,radius*.7),(radius*.83,radius*.62),(radius*.45,radius*.40)],sides=8)
                if digit<3:
                    end=Vector(toe[-1]); direction=(end-Vector(toe[-2])).normalized()
                    tube('07 Worn claw',[end,end+direction*.058+Vector((0,0,.003)),
                                         end+direction*.102+Vector((0,0,-.012))],
                         [(.028,.023),(.019,.019),(.002,.003)],'horn',sides=7)
        eye=Vector((side*.520,2.12,.458))
        outward=Vector((side*.75,.24,.53)).normalized()
        ellipsoid('08 Recessed eye globe '+label,eye,(.082,.112,.048),'dark',16,8)
        horizontal=Vector((-outward.y,outward.x,0)).normalized()
        vertical=outward.cross(horizontal).normalized()
        rotation=Matrix((horizontal,vertical,outward)).transposed().to_quaternion()
        iris=ellipsoid('09 Amber iris '+label,eye+outward*.064,(.055,.037,.010),'eye',16,8)
        pupil=ellipsoid('10 Vertical pupil '+label,eye+outward*.075,(.006,.029,.003),'dark',12,8)
        for eye_part in (iris,pupil):
            eye_part.rotation_mode='QUATERNION'
            eye_part.rotation_quaternion=rotation
        ellipsoid('12 Nasal mound '+label,(side*.226,3.64,.233),(.096,.144,.033),'limb',16,8)
        ellipsoid('13 Nostril '+label,(side*.23,3.68,.257),(.032,.060,.010),'dark',12,6)
        for i in range(11):
            y=2.38+i*.125
            w=body_section(y)[0]*.960
            length=.035+.023*(.5+.5*math.sin(i*2.1))
            if i==3: length=.095
            z=.040
            tube('14 Upper tooth %s %02d'%(label,i),
                 [(side*w,y,z+.035),(side*(w+.012),y+.007,z-length*.25),
                  (side*(w+.005),y+.020,z-length)],
                 [(.029,.026),(.020,.017),(.0018,.002)],'horn',7)
            if i%2==0:
                tube('15 Lower tooth',[(side*(w+.015),y+.057,-.055),
                                       (side*(w+.022),y+.068,-.018),(side*w,y+.073,.027)],
                     [(.023,.022),(.015,.013),(.002,.002)],'horn',7)
    for row in range(17):
        y=-1.88+row*.190
        w=body_section(y)[0]
        for col,ratio in enumerate((-.70,-.43,-.145,.145,.43,.70)):
            yy=y+.014*math.sin(row*7+col*3)
            plate('16 Dorsal osteoderm %02d %d'%(row,col),w*ratio,yy,
                  w*.133,.090,.024+(.023 if col in (2,3) else .004))
    for row in range(15):
        y=-2.05-row*.157
        w=body_section(y)[0]
        for side in (-1,1):
            fraction=.40*max(0,min(1,(y+4.9)/1.3))
            plate('17 Paired caudal keel',side*w*fraction,y,w*.28,.071,
                  max(.035,.115*(y+5.3)/3.3))
    for row in range(7):
        plate('18 Distal rudder crest',0,-4.96+row*.112,.035,.057,.052)
    for y in (1.45,1.68):
        for x in (-.33,-.11,.11,.33):
            plate('19 Nuchal shield',x,y,.087,.088,.025)


def hash2(x,y):
    return np.mod(np.sin(x*127.1+y*311.7)*43758.5453,1)


def noise(u,v,frequency):
    x,y=u*frequency,v*frequency
    ix,iy=np.floor(x),np.floor(y)
    tx,ty=x-ix,y-iy
    tx=tx*tx*(3-2*tx); ty=ty*ty*(3-2*ty)
    return ((1-tx)*hash2(ix,iy)+tx*hash2(ix+1,iy))*(1-ty) + \
           ((1-tx)*hash2(ix,iy+1)+tx*hash2(ix+1,iy+1))*ty


def cells(u,v,nx,ny):
    x=u*nx+.22*np.sin(v*ny*2.7)
    y=v*ny+.14*np.sin(u*nx*3.3)
    ix,iy=np.floor(x),np.floor(y)
    first=np.full(u.shape,100.0); second=first.copy(); identity=np.zeros(u.shape)
    for dy in (-1,0,1):
        for dx in (-1,0,1):
            gx,gy=ix+dx,iy+dy
            jitter=hash2(gx,gy)
            px=gx+.5+(jitter-.5)*.84
            py=gy+.5+(hash2(gx+33,gy+11)-.5)*.72
            d=(x-px)**2+(y-py)**2
            closer=d<first
            second=np.where(closer,first,np.minimum(second,d))
            first=np.minimum(first,d)
            identity=np.where(closer,jitter,identity)
    edge=np.sqrt(second)-np.sqrt(first)
    relief=np.clip(edge/.15,0,1)
    relief=relief*relief*(3-2*relief)
    return relief,identity


def make_textures():
    size=TEXTURE_SIZE
    colors=np.zeros((size,size,4),dtype=np.float32)
    colors[:,:,:3]=(.16,.16,.125); colors[:,:,3]=1
    normals=np.zeros_like(colors); normals[:]=(.5,.5,1,1)
    for region,(x0,y0,x1,y1) in REGIONS.items():
        left,right=int(x0*size)-8,int(x1*size)+9
        bottom,top=int(y0*size)-8,int(y1*size)+9
        xx,yy=np.meshgrid(np.arange(left,right),np.arange(bottom,top))
        u=np.clip((xx/size-x0)/(x1-x0),0,1)
        v=np.clip((yy/size-y0)/(y1-y0),0,1)
        coarse=noise(u,v,7); mottles=noise(u,v,26); grain=noise(u,v,380)
        if region=='body':
            relief,identity=cells(u,v,43,114)
            dorsalness=(1-np.cos(u*2*math.pi))*.5
            belly=np.clip((.39-dorsalness)*4,0,1)
            light=np.array((.46,.430,.340)); dark=np.array((.290,.305,.236))
            base=dark[None,None,:]*(1-belly[:,:,None])+light[None,None,:]*belly[:,:,None]
            bands=(.5+.5*np.sin(v*71+noise(u,v,11)*4))**6
            tail=np.clip((.43-v)*8,0,1)
            tone=.51+.74*coarse+.40*(mottles-.5)+.18*(identity-.5)-.22*bands*tail
        elif region=='limb':
            relief,identity=cells(u,v,25,47)
            base=np.array((.310,.320,.249)); tone=.71+.42*coarse+.16*identity
        elif region=='armor':
            relief,identity=cells(u,v,11,18)
            base=np.array((.295,.309,.243)); tone=.69+.40*coarse+.16*identity
        elif region=='jaw':
            relief,identity=cells(u,v,20,48)
            base=np.array((.385,.368,.288)); tone=.78+.23*coarse+.17*identity
        elif region=='horn':
            relief=.75+.16*grain
            base=np.array((.55,.50,.373)); tone=.78+.23*v+.10*coarse
        elif region=='eye':
            relief=np.ones_like(u)
            base=np.array((.37,.285,.105)); tone=.70+.45*grain
        elif region=='dark':
            relief=np.ones_like(u)
            base=np.array((.035,.039,.030)); tone=.85+.2*grain
        else:
            relief=np.ones_like(u)
            base=np.array((.085,.076,.058)); tone=.80+.20*grain
        cavity=.71+.29*relief
        srgb=np.clip(base*(tone*cavity+.032*(grain-.5))[:,:,None],0,1)
        colors[bottom:top,left:right,:3]=srgb
        height=relief*.75+grain*.09+coarse*.1
        gy,gx=np.gradient(height)
        strength=.90 if region not in ('eye','dark','mouth','horn') else .18
        nx,ny=-gx*strength,-gy*strength
        length=np.sqrt(nx*nx+ny*ny+1)
        normals[bottom:top,left:right,0]=nx/length*.5+.5
        normals[bottom:top,left:right,1]=ny/length*.5+.5
        normals[bottom:top,left:right,2]=1/length*.5+.5
    result=[]
    for name,path,pixels,normal in (('Crocodile albedo 2048',DIFFUSE_PATH,colors,False),
                                    ('Crocodile tangent normal 2048',NORMAL_PATH,normals,True)):
        image=bpy.data.images.new(name,width=size,height=size,alpha=True)
        image.colorspace_settings.name='Non-Color' if normal else 'sRGB'
        image.pixels.foreach_set(pixels.ravel())
        image.filepath_raw=path; image.file_format='PNG'; image.save()
        # Reload the PNG so Blender reviews the same encoded bytes as Unity.
        loaded=bpy.data.images.load(path,check_existing=False)
        loaded.colorspace_settings.name='Non-Color' if normal else 'sRGB'
        result.append(loaded)
    return result


def skin_material(diffuse,normal):
    mat=bpy.data.materials.new('Natural weathered crocodile skin - runtime maps')
    mat.use_nodes=True
    nodes=mat.node_tree.nodes; links=mat.node_tree.links
    shader=nodes.get('Principled BSDF')
    shader.inputs['Roughness'].default_value=.69
    shader.inputs['Metallic'].default_value=0
    tex=nodes.new('ShaderNodeTexImage'); tex.image=diffuse
    ntex=nodes.new('ShaderNodeTexImage'); ntex.image=normal
    nmap=nodes.new('ShaderNodeNormalMap'); nmap.inputs['Strength'].default_value=1
    links.new(tex.outputs['Color'],shader.inputs['Base Color'])
    links.new(ntex.outputs['Color'],nmap.inputs['Color'])
    links.new(nmap.outputs['Normal'],shader.inputs['Normal'])
    return mat


def join_model(mat):
    # Keep named editable anatomical parts in a hidden source collection.
    source=bpy.data.collections.new('AUTHORING - anatomical parts (hidden)')
    bpy.context.scene.collection.children.link(source)
    source.hide_render=True
    source.hide_viewport=True
    for part in PARTS:
        copy=part.copy(); copy.data=part.data.copy()
        copy.name='SOURCE '+part.name
        copy.data.materials.append(mat)
        source.objects.link(copy)
    bpy.context.view_layer.update()
    for part in PARTS:
        code = rig_part(part)
        rig = part.data.attributes.new('ndr_rig', 'INT', 'POINT')
        rig.data.foreach_set('value', [code]*len(part.data.vertices))
    bpy.ops.object.select_all(action='DESELECT')
    for obj in PARTS:
        obj.data.materials.append(mat); obj.select_set(True)
    bpy.context.view_layer.objects.active=PARTS[0]
    bpy.ops.object.join()
    obj=bpy.context.object; obj.name='NDR_Crocodile_GAME_MESH'
    modifier=obj.modifiers.new('Runtime triangles','TRIANGULATE')
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    obj['Authoring']='Original anatomical lofts, osteoderms and articulated digits'
    obj['Runtime']='One material; packed 2048px albedo and tangent normal; +Y forward'
    obj['Animation']='Runtime tail deformation begins at Blender Y=-1.1m'
    return obj


def write_ndmesh(obj,path):
    mesh=obj.data; mesh.calc_loop_triangles()
    uv_layer=mesh.uv_layers.active
    rig=mesh.attributes['ndr_rig']
    vertices,normals,uvs,indices,parts=[],[],[],[],[]
    shared={}; matrix=obj.matrix_world
    for triangle in mesh.loop_triangles:
        ids=[]
        for loop_index in triangle.loops:
            loop=mesh.loops[loop_index]
            normal=mesh.corner_normals[loop_index].vector
            uv=uv_layer.data[loop_index].uv
            # Smooth island vertices MUST be shared: swimming recalculates normals.
            key=(loop.vertex_index,round(uv.x,6),round(uv.y,6),
                 round(normal.x,5),round(normal.y,5),round(normal.z,5))
            if key not in shared:
                shared[key]=len(vertices)
                p=matrix@mesh.vertices[loop.vertex_index].co
                n=(matrix.to_3x3()@normal).normalized()
                vertices.append((p.x,p.z,p.y)); normals.append((n.x,n.z,n.y)); uvs.append(tuple(uv))
                parts.append(rig.data[loop.vertex_index].value)
            ids.append(shared[key])
        indices.extend((ids[0],ids[2],ids[1]))
    errors=0; volume=0
    for offset in range(0,len(indices),3):
        a,b,c=[Vector(vertices[indices[offset+i]]) for i in range(3)]
        face=(b-a).cross(c-a)
        if face.length_squared<1e-13:
            raise RuntimeError('Degenerate exported triangle')
        if any(face.dot(Vector(normals[indices[offset+i]]))<0 for i in range(3)):
            errors+=1
            print('Invalid smooth normal at',tuple(a),tuple(b),tuple(c),flush=True)
        volume+=a.dot(b.cross(c))/6
    if errors or volume<=0:
        raise RuntimeError('Invalid winding: %d faces, volume %.3f'%(errors,volume))
    if any(not .99<sum(c*c for c in n)<1.01 for n in normals):
        raise RuntimeError('Invalid normal')
    if len(indices)//3>20000:
        raise RuntimeError('Crocodile exceeds existing 20k triangle budget: %d'%(len(indices)//3))
    with open(path,'wb') as out:
        out.write(b'NDMS'+struct.pack('<ii',1,len(vertices)))
        for values,fmt in ((vertices,'<3f'),(normals,'<3f'),(uvs,'<2f')):
            for value in values: out.write(struct.pack(fmt,*value))
        out.write(struct.pack('<i',len(indices)))
        out.write(struct.pack('<%di'%len(indices),*indices))
    counts=[parts.count(code) for code in range(len(RIG_PIVOTS))]
    if min(counts[1:])<50:
        raise RuntimeError('Rig part without vertices: %s'%counts)
    # NDRG: magic, version, vertex count, part count, one Unity-axis pivot per
    # part, then one part byte per exported vertex in ndmesh order.
    with open(RIG_PATH,'wb') as out:
        out.write(b'NDRG'+struct.pack('<iii',1,len(vertices),len(RIG_PIVOTS)))
        for x,y,z in RIG_PIVOTS: out.write(struct.pack('<3f',x,z,y))
        out.write(bytes(parts))
    print('CROCODILE RIG: body %d, jaw %d, fore L/R %d/%d, hind L/R %d/%d vertices'%
          tuple(counts),flush=True)
    dimensions=[max(p[i] for p in vertices)-min(p[i] for p in vertices) for i in range(3)]
    print('CROCODILE: %d shared vertices, %d triangles; XYZ %.3f %.3f %.3f; volume %.3f'%
          (len(vertices),len(indices)//3,*dimensions,volume),flush=True)


def point_at(obj,location):
    obj.rotation_euler=(Vector(location)-obj.location).to_track_quat('-Z','Y').to_euler()


def preview_scene(croc):
    scene=bpy.context.scene
    try: scene.render.engine='BLENDER_EEVEE_NEXT'
    except TypeError: scene.render.engine='BLENDER_EEVEE'
    scene.render.resolution_x=1500; scene.render.resolution_y=1000
    scene.render.resolution_percentage=100
    scene.render.image_settings.file_format='PNG'
    scene.world.use_nodes=True
    scene.world.node_tree.nodes['Background'].inputs[0].default_value=(.22,.245,.27,1)
    scene.world.node_tree.nodes['Background'].inputs[1].default_value=.30
    scene.view_settings.view_transform='AgX'
    scene.view_settings.look='AgX - Medium High Contrast'
    scene.view_settings.exposure=-.3
    bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,-.455))
    ground=bpy.context.object; ground.name='PREVIEW neutral ground - not exported'
    mat=bpy.data.materials.new('Neutral charcoal earth'); mat.use_nodes=True
    shader=mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value=(.069,.079,.073,1)
    shader.inputs['Roughness'].default_value=.86
    ground.data.materials.append(mat)
    for name,location,energy,color,size in (
        ('Overcast soft key',(3,2,8),1000,(1,.94,.83),7),
        ('Sky fill',(-4,0,4),550,(.77,.86,1),6),
        ('Soft rear light',(1,-5,4),500,(1,.96,.87),5)):
        bpy.ops.object.light_add(type='AREA',location=location)
        light=bpy.context.object; light.name=name
        light.data.energy=energy; light.data.color=color; light.data.shape='DISK'; light.data.size=size
        point_at(light,(0,-.2,0))
    bpy.ops.object.camera_add(location=(9.2,10.2,8.1))
    camera=bpy.context.object; camera.name='PREVIEW main camera'
    camera.data.type='ORTHO'; camera.data.ortho_scale=11.0
    point_at(camera,(0,-.55,.02)); scene.camera=camera
    scene.render.filepath=PREVIEW_PATH
    bpy.ops.render.render(write_still=True)
    camera.location=(5.4,7.4,3.5); camera.data.ortho_scale=4.5
    point_at(camera,(0,2.35,.19))
    scene.render.filepath=os.path.join(SOURCE,'crocodile_detail.png')
    bpy.ops.render.render(write_still=True)
    camera.location=(9.2,10.2,8.1); camera.data.ortho_scale=11.0
    point_at(camera,(0,-.55,.02)); scene.render.filepath=PREVIEW_PATH
    bpy.ops.object.select_all(action='DESELECT'); croc.select_set(True)
    bpy.context.view_layer.objects.active=croc
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type=='VIEW_3D':
                area.spaces.active.shading.type='MATERIAL'
                area.spaces.active.region_3d.view_distance=11
                area.spaces.active.region_3d.view_location=(0,-.4,0)


def main():
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    # Preserve the pre-existing .blend1 backup.
    bpy.context.preferences.filepaths.save_version=0
    print('Building continuous crocodile anatomy',flush=True)
    build_anatomy()
    print('Painting 2048px natural skin atlases',flush=True)
    diffuse,normal=make_textures()
    croc=join_model(skin_material(diffuse,normal))
    write_ndmesh(croc,MESH_PATH)
    preview_scene(croc)
    for image in (diffuse,normal):
        image.pack()
        image.filepath='//../'+os.path.basename(image.filepath_raw)
    # Blender's replacement rename can fail on a Windows sandbox-owned file.
    # Save a new sibling and copy bytes into the owned target; backups stay intact.
    staged=os.path.join(SOURCE,'crocodile_build_%d.blend'%os.getpid())
    bpy.ops.wm.save_as_mainfile(filepath=staged,compress=False)
    shutil.copyfile(staged,BLEND_PATH)
    os.remove(staged)
    print('Saved editable source and runtime-matched review renders',flush=True)


main()
