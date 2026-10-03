import numpy as np
from PIL import Image
U='/mnt/user-data/uploads/'
G=(8.144,4.461,7.718,3.684)  # base 网格
def sample(arr,g=G,nx=151,ny=163):
    px,ox,py,oy=g; out=np.zeros((ny,nx,3),np.uint8)
    for j in range(ny):
        y0=int(round(oy+j*py+py*0.25)); y1=max(y0+1,int(round(oy+(j+1)*py-py*0.25)))
        for i in range(nx):
            x0=int(round(ox+i*px+px*0.25)); x1=max(x0+1,int(round(ox+(i+1)*px-px*0.25)))
            out[j,i]=np.median(arr[y0:y1,x0:x1].reshape(-1,3),0)
    return out
R={}
for k,f in dict(hc='CopperHair2.png',hl='CopperHair2-left-2.png',hl1='CopperHair2-left.png',hr='CopperHair2-right.png',nb='NoBackHair.png').items():
    im=Image.open(U+f).convert('RGB')
    if im.size!=(1241,1268): im=im.resize((1241,1268),Image.NEAREST)
    R[k]=sample(np.array(im))
np.savez('lr2.npz',**R)
base=np.load('lr.npz')['base'][:163,:151]
def isbg(a): a=a.astype(int); return (a[...,0]>130)&(a[...,2]>130)&(a[...,1]<140)&(a[...,2]-a[...,1]>60)
# 对齐检查：nb 与 base 比颜色；hc 与 base 比头顶轮廓
for k in ['nb','hc']:
    best=None
    for dy in range(-4,5):
        for dx in range(-4,5):
            s=np.roll(np.roll(R[k],dy,0),dx,1)
            if k=='nb':
                m=~isbg(base)&~isbg(s); e=(np.abs(s.astype(int)-base.astype(int)).sum(2)>60)[m].mean()
            else:
                A=~isbg(base[:45]); B=~isbg(s[:45]); e=1-(A&B).sum()/(A|B).sum()
            if best is None or e<best[0]: best=(round(e,3),dx,dy)
    print(k,best)
# 摆动幅度（格）：各行左右边界
fg=lambda x:~isbg(x)
for y in range(70,150,10):
    row=[]
    for k in ['hc','hl','hl1','hr']:
        xs=np.nonzero(fg(R[k])[y:y+10].any(0))[0]; row.append((xs.min(),xs.max()) if len(xs) else None)
    print(y,row)
c=np.concatenate([R[k] for k in ['hl1','hl','hc','hr','nb']],1)
Image.fromarray(c).resize((c.shape[1]*2,c.shape[0]*2),Image.NEAREST).save('v2_all.png')
