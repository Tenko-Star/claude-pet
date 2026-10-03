import numpy as np
from PIL import Image
D=dict(np.load('lr.npz'))
H,W=D['base'].shape[:2]
def isbg(a): a=a.astype(int); return (a[...,0]>130)&(a[...,2]>130)&(a[...,1]<140)&(a[...,2]-a[...,1]>60)
# 1) 统一画布并按整格对齐到 base
def fitcanvas(a):
    out=np.zeros((H,W,3),np.uint8); out[...]= [255,0,255]
    h,w=min(H,a.shape[0]),min(W,a.shape[1]); out[:h,:w]=a[:h,:w]; return out
def align(a,ref,rng=5):
    best=None
    fr=~isbg(ref)
    for dy in range(-rng,rng+1):
        for dx in range(-rng,rng+1):
            s=np.roll(np.roll(a,dy,0),dx,1)
            m=fr&~isbg(s)
            err=(np.abs(s.astype(int)-ref.astype(int)).sum(2)>60)[m].mean() if m.sum() else 1
            if best is None or err<best[0]: best=(err,dx,dy)
    _,dx,dy=best; return np.roll(np.roll(a,dy,0),dx,1),best
L={'base':D['base'][:H,:W]}
for k in ['nofront','back','back1','noarm','closed','half','mouth']:
    a=fitcanvas(D[k]); L[k],b=align(a,L['base']); print(k,'align',b)
# 2) 统一调色板（k-means）
pix=np.concatenate([v[~isbg(v)] for v in L.values()]).astype(float)
rng=np.random.default_rng(0); C=pix[rng.choice(len(pix),32,replace=False)]
for _ in range(25):
    lab=((pix[:,None,:]-C[None])**2).sum(2).argmin(1)
    for i in range(len(C)):
        if (lab==i).any(): C[i]=pix[lab==i].mean(0)
C=np.round(C).astype(np.uint8)
def quant(a):
    a=a.copy(); ai=a.astype(int); purple=(ai[...,2]-ai[...,1]>40)&(ai[...,0]-ai[...,1]>40); a[purple]=[255,0,255]
    q=C[((a.astype(float)[...,None,:]-C[None,None].astype(float))**2).sum(3).argmin(2)]
    out=np.zeros((H,W,4),np.uint8); out[...,:3]=q; out[...,3]=np.where(isbg(a),0,255); return out
Q={k:quant(v) for k,v in L.items()}
np.save('palette.npy',C)
eq=lambda a,b:(a[...,:3]==b[...,:3]).all(2)&(a[...,3]>0)&(b[...,3]>0)
base=Q['base']
# 3) 表情差分：只取眼睛/嘴巴附近、与 base 不同的格子
def patch(k,box):
    y0,y1,x0,x1=box; m=np.zeros((H,W),bool); m[y0:y1,x0:x1]=True
    d=m&~eq(Q[k],base)&(Q[k][...,3]>0)
    out=np.zeros_like(base); out[d]=Q[k][d]; return out,d.sum()
# 定位眼睛和嘴：closed 与 base 差异最集中的区域
d=~eq(Q['closed'],base)&(base[...,3]>0); ys,xs=np.nonzero(d[30:90,40:110]); print('eye diff rows',ys.min()+30,ys.max()+30,'cols',xs.min()+40,xs.max()+40)

def ishair(a):
    r,g,b=[a[...,i].astype(int) for i in range(3)]
    return (r>150)&(r-b>90)&(g>60)&(g<190)&(a[...,3]>0)
def expr(k,box):
    y0,y1,x0,x1=box; m=np.zeros((H,W),bool); m[y0:y1,x0:x1]=True
    d=m&~eq(Q[k],base)&~(ishair(Q[k])&ishair(base))
    out=np.zeros_like(base); out[d]=Q[k][d]; return out,int(d.sum())
EYE=(50,60,54,83); MOUTH=(59,65,63,73)
ex={}
for k,box in [('half',EYE),('closed',EYE),('mouth',MOUTH)]:
    ex[k],n=expr(k,box); print(k,'patch cells',n)
# 4) 后发：补头部镂空（仅在 base 轮廓内、胸口以上），用最近的头发像素向内扩散
back=Q['back'].copy()
fill=(back[...,3]==0)&(base[...,3]>0); fill[95:]=False
# 只填被头发包围的镂空：按行检查左右两侧都有头发
for y in range(H):
    xs=np.nonzero(back[y,:,3]>0)[0]
    if len(xs)==0: fill[y]=False; continue
    row=fill[y].copy(); row[:xs.min()]=False; row[xs.max()+1:]=False; fill[y]=row
todo=fill.copy()
while todo.any():
    nb=np.zeros_like(todo)
    for dy,dx in [(0,1),(0,-1),(1,0),(-1,0)]:
        s=np.roll(np.roll(back,dy,0),dx,1); ok=todo&(s[...,3]>0)&(back[...,3]==0)
        back[ok]=s[ok]; nb|=ok
    if not nb.any(): break
    todo&=~nb
print('back filled',fill.sum())
# 5) 主体 = base 去掉"后发可见部分"（与后发同色的头发格子）
backvis=eq(base,Q['back'])&ishair(base)
main=base.copy(); main[backvis,3]=0
print('back-visible cells removed from main',backvis.sum())
# 手臂层（为后续工作/托腮差分预留）
arms=base.copy(); armmask=~eq(Q['noarm'],base)&(base[...,3]>0)&~ishair(base); arms[~armmask,3]=0
for name,arr in dict(back=back,main=main,eye_half=ex['half'],eye_closed=ex['closed'],mouth_open=ex['mouth'],arms=arms,noarm=Q['noarm'],full=base).items():
    Image.fromarray(arr).save(f'layer_{name}.png')
# 预览帧检查
def comp(bdx=0,eyes=None,mouth=None):
    c=np.zeros((H,W,4),np.uint8)
    for lay in [np.roll(back,bdx,1),main]+([ex[eyes]] if eyes else [])+([ex['mouth']] if mouth else []):
        a=lay[...,3:]>0; c=np.where(a,lay,c)
    return c
fr=[comp(0),comp(1),comp(-1),comp(0,'half'),comp(0,'closed'),comp(0,None,True)]
strip=np.concatenate(fr,1); bg=np.zeros_like(strip); bg[...,:3]=[60,60,70]; bg[...,3]=255
out=np.where(strip[...,3:]>0,strip,bg)
Image.fromarray(out).resize((out.shape[1]*3,out.shape[0]*3),Image.NEAREST).save('frames.png')
