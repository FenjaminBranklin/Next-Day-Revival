"""Bohdana atlas using the installed BTR exterior and wheel textures.

Mapping stays compatible with arty_import.surface_uv: paint/rubber on the
upper row, machinery/glass below. Linear metal R and smoothness A; normals
are packed AG for the desktop Unity Standard shader. No baked highlights.
"""
from pathlib import Path
import os
import numpy as np
from PIL import Image
import texlib

SIZE = 4096
HALF = SIZE // 2
ROOT = Path(__file__).resolve().parent
PAINT_CROP = (550, 20, 806, 276)  # Plain exterior plate; no hatches or lamps.
GUTTER = 64
INNER = HALF - 2 * GUTTER


def native_environment():
    import UnityPy
    data = Path(os.environ.get('NDR_GAME_DATA',
        r'C:\Program Files (x86)\Steam\steamapps\common\Next Day Survival\nextday_game_Data'))
    return UnityPy.load(str(data / 'resources.assets'))


def native_maps():
    names = ('btr-80a_alb', 'btr-80a_met', 'btr-80a_norm',
             'btr-80a_wheel_alb', 'btr-80a_wheel_norm')
    found = {}
    for obj in native_environment().objects:
        if obj.type.name != 'Texture2D':
            continue
        data = obj.read()
        if data.m_Name in names:
            found[data.m_Name] = data.image.convert('RGBA')
    if set(found) != set(names):
        raise ValueError('Native BTR exterior maps missing: '+str(set(names)-set(found)))
    return found


def paint_patch(image, normal=False):
    """Repeat actual paint pixels with mirrored, normal-corrected seams."""
    patch = np.asarray(image.crop(PAINT_CROP)).copy()
    if normal:
        patch[..., 0] = patch[..., 2] = 255  # Unity desktop AG encoding.
    across = patch[:, ::-1].copy()
    if normal:
        across[..., 3] = 255-across[..., 3]
    row = np.concatenate((patch, across), axis=1)
    below = row[::-1].copy()
    if normal:
        below[..., 1] = 255-below[..., 1]
    tile = np.concatenate((row, below), axis=0)
    return np.tile(tile, (4, 4, 1))[:INNER, :INNER]


def padded(pixels):
    return Image.fromarray(np.pad(pixels, ((GUTTER,GUTTER),
                                          (GUTTER,GUTTER),(0,0)), mode='edge'))


def surface(kind, seed):
    r = np.random.default_rng(seed)
    n = HALF
    bases = ((70, 82, 49), (32, 33, 34), (64, 66, 68), (32, 41, 47))
    # Painted steel is a dielectric. Only chips and exposed machinery are metal.
    metals = (0.035, 0.0, 0.88, 0.0)
    smooths = (0.30, 0.12, 0.53, 0.83)
    rgb = np.empty((n,n,3), np.float32)
    rgb[:] = bases[kind]
    grain = r.normal(0, 1.2 if kind != 3 else 0.18, (n,n)).astype(np.float32)
    # Small-amplitude directional rolling/grinding marks, no camouflage blobs.
    brushed = r.normal(0, 0.8, (n,1)).astype(np.float32)
    rgb += grain[...,None] + brushed[...,None]
    height = grain * 0.0005
    metal = np.full((n,n), metals[kind], np.float32)
    smooth = np.clip(smooths[kind] + grain * 0.009,0,1)
    if kind in (0,2):
        # Sparse sharp scratches and chips expose metal beneath paint.
        for _ in range(1100 if kind==0 else 1700):
            x,y = r.integers(8,n-64,2)
            length=int(r.integers(3,42)); width=int(r.integers(1,3))
            patch=(slice(y,y+width),slice(x,x+length))
            rgb[patch] = (100,104,101) if kind==0 else (111,115,117)
            metal[patch]=0.88
            smooth[patch]=0.57
            height[patch]-=0.045
        # Light road dirt toward the bottom; only a subtle tonal variation.
        dirt=np.linspace(0.98,0.88,n,dtype=np.float32)[:,None,None]
        rgb *= dirt
    if kind==1:
        # Fine tread cuts; geometry still supplies the actual wheel silhouette.
        y,x=np.mgrid[:n,:n]
        cuts=((x+(y//96%2*2-1)*y*0.45)%80)<9
        rgb[cuts]*=0.6
        height[cuts]-=0.12
    if kind==3:
        height[:]=0
    normal=np.asarray(texlib.height_to_normal(height, 3.0))
    packed=np.full((n,n,4),255,np.uint8)
    packed[...,1]=normal[...,1]
    packed[...,3]=normal[...,0]
    material=np.zeros((n,n,4),np.uint8)
    material[...,0]=(metal*255).astype(np.uint8)
    material[...,3]=(smooth*255).astype(np.uint8)
    return np.clip(rgb,0,255).astype(np.uint8), material, packed


def main():
    diffuse=Image.new('RGB',(SIZE,SIZE))
    metal=Image.new('RGBA',(SIZE,SIZE))
    normal=Image.new('RGBA',(SIZE,SIZE))
    for kind,pos in enumerate(((0,0),(HALF,0),(0,HALF),(HALF,HALF))):
        if kind < 2:
            continue
        a,m,n=surface(kind,0x4127+kind)
        diffuse.paste(Image.fromarray(a),pos)
        metal.paste(Image.fromarray(m),pos)
        normal.paste(Image.fromarray(n),pos)
    native = native_maps()
    paint = paint_patch(native['btr-80a_alb'])
    material = paint_patch(native['btr-80a_met'])
    # The BTR's map alpha is 1, multiplied by _GlossMapScale = 0.4.
    material[..., 3] = np.rint(material[..., 3].astype(float)*0.4).astype(np.uint8)
    diffuse.paste(padded(paint).convert('RGB'), (0,0))
    metal.paste(padded(material), (0,0))
    normal.paste(padded(paint_patch(native['btr-80a_norm'], True)), (0,0))
    wheel = np.array(native['btr-80a_wheel_alb'].resize((INNER,INNER), Image.Resampling.BILINEAR))
    wheel_normal = np.array(native['btr-80a_wheel_norm'].resize((INNER,INNER), Image.Resampling.BILINEAR))
    wheel_normal[...,0] = wheel_normal[...,2] = 255
    wheel_material = np.zeros((INNER,INNER,4), np.uint8)
    wheel_material[...,3] = 30
    diffuse.paste(padded(wheel).convert('RGB'), (HALF,0))
    metal.paste(padded(wheel_material), (HALF,0))
    normal.paste(padded(wheel_normal), (HALF,0))
    for name,img in (('diffuse',diffuse),('metal',metal),('normal',normal)):
        path=ROOT/'assets'/('arty_'+name+'.png')
        img.save(path)
        print(path.name,img.size,img.mode)


if __name__=='__main__':
    main()
