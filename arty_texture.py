"""Deterministic 4K Bohdana surface atlas and matched PBR data.

Mapping stays compatible with arty_import.surface_uv: paint/rubber on the
upper row, machinery/glass below. Linear metal R and smoothness A; normals
are packed AG for the desktop Unity Standard shader. No baked highlights.
"""
from pathlib import Path
import numpy as np
from PIL import Image
import texlib

SIZE = 4096
HALF = SIZE // 2
ROOT = Path(__file__).resolve().parent


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
        a,m,n=surface(kind,0x4127+kind)
        diffuse.paste(Image.fromarray(a),pos)
        metal.paste(Image.fromarray(m),pos)
        normal.paste(Image.fromarray(n),pos)
    for name,img in (('diffuse',diffuse),('metal',metal),('normal',normal)):
        path=ROOT/'assets'/('arty_'+name+'.png')
        img.save(path)
        print(path.name,img.size,img.mode)


if __name__=='__main__':
    main()
