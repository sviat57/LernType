from PIL import Image, ImageDraw, ImageFont
from pathlib import Path
import math

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'src' / 'WortBruecke.App' / 'Assets' / 'Brand'
OUT.mkdir(parents=True, exist_ok=True)

BG_TOP=(211,103,77,255)
BG_BOTTOM=(165,67,48,255)
CREAM=(255,247,237,255)
INK=(78,43,35,255)

def bezier(p0,p1,p2,p3,steps=80):
    pts=[]
    for i in range(steps+1):
        t=i/steps; u=1-t
        pts.append((round(u**3*p0[0]+3*u*u*t*p1[0]+3*u*t*t*p2[0]+t**3*p3[0]),
                    round(u**3*p0[1]+3*u*u*t*p1[1]+3*u*t*t*p2[1]+t**3*p3[1])))
    return pts

def make_icon(size, tile=True):
    scale=4
    S=size*scale
    im=Image.new('RGBA',(S,S),(0,0,0,0))
    d=ImageDraw.Draw(im)
    inset=int(S*.045); radius=int(S*.22)
    # warm directional gradient clipped to a squircle
    mask=Image.new('L',(S,S),0); md=ImageDraw.Draw(mask)
    md.rounded_rectangle((inset,inset,S-inset,S-inset),radius=radius,fill=255)
    grad=Image.new('RGBA',(S,S))
    gp=grad.load()
    for y in range(S):
        t=y/max(1,S-1)
        for x in range(S):
            glow=max(0,1-math.hypot((x/S)-.25,(y/S)-.18)/.85)
            rr=int(BG_TOP[0]*(1-t)+BG_BOTTOM[0]*t + glow*10)
            gg=int(BG_TOP[1]*(1-t)+BG_BOTTOM[1]*t + glow*6)
            bb=int(BG_TOP[2]*(1-t)+BG_BOTTOM[2]*t + glow*4)
            gp[x,y]=(min(rr,255),min(gg,255),min(bb,255),255)
    im.alpha_composite(Image.composite(grad,Image.new('RGBA',(S,S)),mask))
    d=ImageDraw.Draw(im)
    # glass edge: outer and inner refraction
    d.rounded_rectangle((inset,inset,S-inset,S-inset),radius=radius,outline=(255,255,255,82),width=max(1,int(S*.012)))
    inner=inset+int(S*.022)
    d.rounded_rectangle((inner,inner,S-inner,S-inner),radius=max(1,radius-int(S*.025)),outline=(255,228,217,48),width=max(1,int(S*.006)))
    # bridge arch and piers
    w=int(S*.078)
    arch=bezier((S*.245,S*.49),(S*.34,S*.27),(S*.66,S*.27),(S*.755,S*.49))
    d.line(arch,fill=CREAM,width=w,joint='curve')
    for x in (.245,.755):
        d.line((S*x,S*.47,S*x,S*.705),fill=CREAM,width=w)
        r=w//2
        d.ellipse((S*x-r,S*.705-r,S*x+r,S*.705+r),fill=CREAM)
    # route/path underneath the bridge
    pw=int(S*.038)
    route=bezier((S*.50,S*.79),(S*.43,S*.70),(S*.47,S*.61),(S*.605,S*.535),70)
    d.line(route,fill=INK,width=pw,joint='curve')
    r=int(S*.027)
    d.ellipse((S*.605-r,S*.535-r,S*.605+r,S*.535+r),fill=CREAM)
    return im.resize((size,size),Image.Resampling.LANCZOS)

# primary raster and packaging squares
main=make_icon(1024)
main.save(OUT/'LernTypeIcon.png',optimize=True)
for size,name in [(310,'Square310x310Logo.png'),(150,'Square150x150Logo.png'),(44,'Square44x44Logo.png'),(50,'StoreLogo.png')]:
    make_icon(size).save(OUT/name,optimize=True)
# multi-resolution ICO
main.save(OUT/'LernType.ico',format='ICO',sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
# Wide package mark: icon + native wordmark on transparent field.
wide=Image.new('RGBA',(310,150),(0,0,0,0)); wide.alpha_composite(make_icon(116),(12,17))
dw=ImageDraw.Draw(wide)
font_candidates=[Path('C:/Windows/Fonts/seguisb.ttf'),Path('C:/Windows/Fonts/segoeuib.ttf')]
body_candidates=[Path('C:/Windows/Fonts/segoeui.ttf')]
def font(candidates,size):
    for p in candidates:
        if p.exists(): return ImageFont.truetype(str(p),size)
    return ImageFont.load_default()
dw.text((143,43),'LernType',font=font(font_candidates,29),fill=(44,39,35,255))
dw.text((145,84),'DEUTSCH LERNEN',font=font(body_candidates,11),fill=(174,71,51,255),stroke_width=0)
wide.save(OUT/'Wide310x150Logo.png',optimize=True)
print(OUT)
