"""Package Illustrator PNG exports; create ICO and previews; verify real bytes."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import struct, io, json, hashlib, xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]
RENDERS=ROOT/'source'/'renders'
PNG=ROOT/'png'; PNG.mkdir(exist_ok=True)
SIZES=[16,24,32,48,64,256]
for variant,sizes in [('standard',[1024,256]),('small',[16,24,32,48,64,256]),('mono',[16,24,32,48,64,256]),('mono-light',[16,24,32,48,64,256])]:
    for n in sizes:
        im=Image.open(RENDERS/f'{variant}-{n}.png').convert('RGBA')
        expected=n*4 if n<=64 else n
        assert im.size==(expected,expected), (variant,n,im.size)
        if n<=64: im=im.resize((n,n),Image.Resampling.LANCZOS)
        # Clear the <= 4/255 alpha ringing created by Lanczos at very small sizes.
        pix=list(im.get_flattened_data()); im.putdata([(0,0,0,0) if p[3]<=4 else p for p in pix])
        name=f'npedutools-{n}.png' if variant=='standard' or (variant=='small' and n<=64) else f'npedutools-{variant}-{n}.png'
        im.save(PNG/name)

def dib(im):
    n=im.width
    xor=im.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes('raw','BGRA')
    stride=((n+31)//32)*4
    mask=bytearray(stride*n)
    alpha=im.getchannel('A')
    for y in range(n):
        for x in range(n):
            if alpha.getpixel((x,n-1-y))==0: mask[y*stride+x//8]|=1<<(7-x%8)
    header=struct.pack('<IiiHHIIiiII',40,n,2*n,1,32,0,len(xor)+len(mask),0,0,0,0)
    return header+xor+bytes(mask)

chunks=[]
for n in SIZES:
    im=Image.open(PNG/f'npedutools-{n}.png')
    if n==256:
        out=io.BytesIO(); im.save(out,format='PNG'); payload=out.getvalue()
    else: payload=dib(im)
    chunks.append((n,payload))
offset=6+16*len(chunks)
entries=[]
for n,payload in chunks:
    entries.append(struct.pack('<BBBBHHII',n%256,n%256,0,0,1,32,len(payload),offset))
    offset+=len(payload)
(ROOT/'npedutools.ico').write_bytes(struct.pack('<HHH',0,1,len(chunks))+b''.join(entries)+b''.join(p for n,p in chunks))

ico=Image.open(ROOT/'npedutools.ico')
assert ico.ico.sizes()=={(n,n) for n in SIZES}
checks={}
for n in SIZES:
    im=ico.ico.getimage((n,n)).convert('RGBA')
    expected=Image.open(PNG/f'npedutools-{n}.png').convert('RGBA')
    assert im.tobytes()==expected.tobytes(),f'ICO frame mismatch {n}'
    a=im.getchannel('A')
    assert a.getextrema()==(0,255)
    assert a.getpixel((int(n*.60),int(n*.80)))==0, f'central gap {n}'
    checks[str(n)]={'rgba_exact_match':True,'alpha_range':list(a.getextrema()),'bbox':list(a.getbbox()),'variant':'small' if n<=64 else 'standard','encoding':'PNG' if n==256 else '32-bit DIB + AND mask'}
svgs={}
for file in ROOT.glob('*.svg'):
    tree=ET.parse(file); elements=list(tree.iter())
    assert not any(e.tag.rsplit('}',1)[-1] in ['image','rect','mask','filter','foreignObject'] for e in elements)
    paths=[e for e in elements if e.tag.endswith('}path')]
    assert paths and all('C' in p.attrib['d'] for p in paths)
    svgs[file.name]={'paths':len(paths),'embedded_images':0,'background_shapes':0}
report={'ico_frames':checks,'svg':svgs,'original_sha256':hashlib.sha256((ROOT.parent/'logoorigin.png').read_bytes()).hexdigest()}
(ROOT/'source'/'validation.json').write_text(json.dumps(report,indent=2),encoding='utf-8')

# Preview uses only the final exported PNGs, composed at their specified sizes.
fontpath='C:/Windows/Fonts/msyh.ttc'
def font(n):return ImageFont.truetype(fontpath,n)
canvas=Image.new('RGB',(1360,1060),'#F6F8F7'); d=ImageDraw.Draw(canvas)
def txt(x,y,s,size=20,fill='#202E35'):d.text((x,y),s,font=font(size),fill=fill)
def paste(name,x,y):
    im=Image.open(PNG/name).convert('RGBA'); canvas.paste(im,(x,y),im)
def card(box,color):d.rounded_rectangle(box,radius=16,fill=color)
txt(48,32,'NPEduTools',38);txt(50,87,'矢量重绘 · 图标资产预览',19,'#536B61')
txt(974,45,'原生 AI / SVG / PNG / ICO',17,'#536B61')
for x,title,sub in [(48,'标准版','4 条路径 · 保留书页与折面'),(480,'小尺寸简化版','3 条路径 · 扩大负空间'),(912,'单色版','2 条路径 · 深浅背景适配')]:
    card((x,142,x+400,492),'#EDF2EF')
    txt(x+24,163,title,23);txt(x+24,204,sub,15,'#536B61')
paste('npedutools-256.png',120,230)
paste('npedutools-small-256.png',552,230)
card((932,253,1102,461),'#FFFFFF'); card((1120,253,1290,461),'#202E35')
# Monochrome thumbnails are rendered from actual exported 256 PNGs for overview.
for name,x in [('npedutools-mono-256.png',937),('npedutools-mono-light-256.png',1125)]:
    im=Image.open(PNG/name).convert('RGBA').resize((160,160),Image.Resampling.LANCZOS);canvas.paste(im,(x,274),im)
txt(48,518,'小尺寸实像素检视',25);txt(334,526,'以下图标以 1:1 像素合成；请在图片查看器以 100% 查看',16,'#536B61')
for y,bg,fg,mono in [(568,'#FFFFFF','#202E35','mono'),(729,'#202E35','#F6F8F7','mono-light')]:
    card((48,y,1312,y+140),bg)
    txt(70,y+16,'彩色',15,fg);txt(726,y+16,'单色',15,fg)
    for j,n in enumerate([16,24,32,48,64]):
        x=184+j*101;paste(f'npedutools-{n}.png',x+(64-n)//2,y+41+(64-n)//2)
        txt(x+15,y+109,str(n),13,fg)
        x=820+j*91;paste(f'npedutools-{mono}-{n}.png',x+(64-n)//2,y+41+(64-n)//2)
        txt(x+15,y+109,str(n),13,fg)
txt(48,900,'最终色板',21)
for j,(color,label) in enumerate([('#147D68','品牌青绿'),('#1C604D','深绿折面'),('#73C99B','浅绿飘带'),('#202E35','深色单色'),('#FFFFFF','浅色单色')]):
    x=204+j*220;d.rounded_rectangle((x,897,x+38,935),radius=8,fill=color,outline='#CBD8D1')
    txt(x+48,897,color,15);txt(x+48,920,label,12,'#536B61')
txt(48,972,'透明背景与内部留白 · 16–64 px 使用简化版 · 256 / 1024 px 使用标准版',17,'#536B61')
txt(48,1008,'预览由交付 PNG 合成。母版 4 个画板，纯矢量路径，无位图、无白色遮挡。',15,'#536B61')
canvas.save(ROOT/'npedutools-preview.png')

# Exact-size HTML remains 1 CSS px per pixel regardless of preview sheet fitting.
rows=[]
for dark in [False,True]:
    mono='mono-light' if dark else 'mono'
    cells=''.join(f'<figure><img src="png/npedutools-{n}.png" width="{n}" height="{n}"><figcaption>{n}px</figcaption></figure>' for n in [16,24,32,48,64])
    cells+=''.join(f'<figure><img src="png/npedutools-{mono}-{n}.png" width="{n}" height="{n}"><figcaption>mono {n}px</figcaption></figure>' for n in [16,24,32,48,64])
    rows.append(f'<section class="{"dark" if dark else "light"}">{cells}</section>')
html='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>NPEduTools 图标检视</title>
<style>body{margin:32px;background:#F6F8F7;color:#202E35;font:16px "Microsoft YaHei",sans-serif}section{display:flex;gap:20px;align-items:center;padding:24px;margin:20px 0;border-radius:16px}.light{background:white}.dark{background:#202E35;color:#fff}figure{width:84px;text-align:center;margin:0}img{object-fit:contain}figcaption{font-size:12px;margin-top:16px}.large img{width:240px;height:240px}.large figure{width:260px}</style>
<h1>NPEduTools · 实际导出检视</h1><p>浏览器缩放设为 100%；小图按其标称 CSS 像素显示。SVG 可以任意放大检查曲线。系统显示缩放仍会影响物理屏幕像素。</p>
<section class="large light"><figure><img src="npedutools-logo.svg"><figcaption>标准 SVG</figcaption></figure><figure><img src="npedutools-logo-small.svg"><figcaption>简化 SVG</figcaption></figure><figure><img src="npedutools-logo-mono.svg"><figcaption>单色 SVG</figcaption></figure></section>'''+''.join(rows)+'</html>'
(ROOT/'preview.html').write_text(html,encoding='utf-8')
print(json.dumps(report,indent=2))
