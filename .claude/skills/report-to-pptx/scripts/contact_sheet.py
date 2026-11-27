import sys,glob,os
from PIL import Image
d=sys.argv[1]; fs=sorted(glob.glob(os.path.join(d,"slide-*.png")))
for k in range(0,len(fs),2):
    ims=[Image.open(f).resize((1200,675)) for f in fs[k:k+2]]
    out=Image.new("RGB",(1200,675*len(ims)+10*(len(ims)-1)),"white")
    for i,im in enumerate(ims): out.paste(im,(0,i*685))
    out.save(os.path.join(d,f"pair-{k//2+1}.jpg"),quality=85)
print("ok")
