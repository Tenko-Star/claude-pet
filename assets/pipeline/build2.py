import numpy as np, json, base64, io
from PIL import Image
C=np.load('palette.npy').astype(float)
R=dict(np.load('lr2.npz'))
def isbg(a): a=a.astype(int); return (a[...,0]>130)&(a[...,2]>130)&(a[...,1]<140)&(a[...,2]-a[...,1]>60)
def quant(a):
    a=a.copy(); ai=a.astype(int); a[(ai[...,2]-ai[...,1]>40)&(ai[...,0]-ai[...,1]>40)]=[255,0,255]
    q=C[((a.astype(float)[...,None,:]-C[None,None])**2).sum(3).argmin(2)].astype(np.uint8)
    out=np.zeros((*a.shape[:2],4),np.uint8); out[...,:3]=q; out[...,3]=np.where(isbg(a),0,255); return out
def clean(x):  # 去掉孤立的单个像素
    a=x[...,3]>0; n=sum(np.roll(np.roll(a,dy,0),dx,1) for dy in (-1,0,1) for dx in (-1,0,1))-a
    x=x.copy(); x[a&(n<=1),3]=0; return x
L={k:clean(quant(R[k])) for k in ['nb','hc','hl','hr','hl1']}
box=(13,19,132,148)
crop=lambda x:x[box[1]:box[3],box[0]:box[2]]
out={}
for k,name in dict(nb='main',hc='hair_c',hl='hair_l',hr='hair_r',hl1='hair_l_big').items():
    out[name]=crop(L[k])
for name in ['eye_half','eye_closed','mouth_open']:
    out[name]=crop(np.array(Image.open(f'layer_{name}.png').convert('RGBA')))
# 底部超出裁剪框检查
for k in ['hc','hl','hr','hl1','nb']:
    a=L[k][...,3]>0; ys,xs=np.nonzero(a); print(k,'bbox',xs.min(),xs.max(),ys.min(),ys.max())
def comp(h,eye=None):
    c=np.zeros_like(out['main'])
    for lay in [out[h],out['main']]+([out[eye]] if eye else []):
        m=lay[...,3:]>0; c=np.where(m,lay,c)
    return c
fr=[comp('hair_c'),comp('hair_l'),comp('hair_r'),comp('hair_l_big')]
s=np.concatenate(fr,1); bg=np.zeros_like(s); bg[...,:3]=[46,42,51]; bg[...,3]=255
s=np.where(s[...,3:]>0,s,bg); Image.fromarray(s).resize((s.shape[1]*4,s.shape[0]*4),Image.NEAREST).save('frames2.png')
d={}
for k,v in out.items():
    Image.fromarray(v).save(f'/mnt/user-data/outputs/sprites/{k}.png')
    b=io.BytesIO(); Image.fromarray(v).save(b,'PNG'); d[k]='data:image/png;base64,'+base64.b64encode(b.getvalue()).decode()
json.dump(d,open('layers2.json','w'))
