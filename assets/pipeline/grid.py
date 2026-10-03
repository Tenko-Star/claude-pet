from PIL import Image
import numpy as np
U='/mnt/user-data/uploads/'
FILES=dict(base='932D8987-8B8A-4644-981B-2807199C037A.png', nofront='E075384B-F6F4-4A6A-A285-E53EEEAF939B.png',
 back='Pixel-Art_Copper_Hair_Sprite.png', back1='CopperHair.png', noarm='no-arm.png', closed='SerenePixel.png',
 half='SleepyChibi.png', mouth='mouth1.png')
def fit(prof):
    x=np.arange(len(prof))+0.5; best=(0,0,0)
    for p in np.arange(6.0,10.0,0.002):
        z=(prof*np.exp(2j*np.pi*x/p)).sum(); s=abs(z)
        if s>best[0]: best=(s,p,np.angle(z))
    p=best[1]; off=(-best[2]/(2*np.pi)*p)%p
    return p,off
def lowres(arr):
    g=arr.astype(float).sum(2)
    px,ox=fit(np.abs(np.diff(g,axis=1)).sum(0)); py,oy=fit(np.abs(np.diff(g,axis=0)).sum(1))
    nx=int((arr.shape[1]-ox)//px); ny=int((arr.shape[0]-oy)//py)
    out=np.zeros((ny,nx,3),np.uint8)
    for j in range(ny):
        y0=int(round(oy+j*py+py*0.25)); y1=max(y0+1,int(round(oy+(j+1)*py-py*0.25)))
        for i in range(nx):
            x0=int(round(ox+i*px+px*0.25)); x1=max(x0+1,int(round(ox+(i+1)*px-px*0.25)))
            c=arr[y0:y1,x0:x1].reshape(-1,3); out[j,i]=np.median(c,0)
    return out,(px,ox,py,oy)
res={}
for k,f in FILES.items():
    a=np.array(Image.open(U+f).convert('RGB'))
    lr,gr=lowres(a); res[k]=lr; print(k,a.shape,'grid',[round(v,3) for v in gr],'->',lr.shape)
    Image.fromarray(lr).save(f'lr_{k}.png')
np.savez('lr.npz',**res)
