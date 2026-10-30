from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import subprocess
P=Path(__file__).resolve().parent
def run(a): subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y',*a],cwd=P,check=True)
def ft(n,b=False): return ImageFont.truetype('C:/Windows/Fonts/segoeui'+('b' if b else '')+'.ttf',n)
def banner(name,title,sub):
 im=Image.new('RGBA',(1920,1080)); d=ImageDraw.Draw(im)
 d.rectangle((0,0,1920,89),fill='#103e32');d.rectangle((0,990,1920,1080),fill='#103e32')
 d.text((42,23),'BIMaestro',font=ft(32,True),fill='#9bdec4')
 d.text((320,24),title,font=ft(30,True),fill='white')
 d.text((42,1014),sub,font=ft(28),fill='white');im.save(P/(name+'.png'))
# One fixed canvas, square pixels, no indicator crossing a scene boundary.
scenes=[
 ('exemple',58,4,'2534:1260:0:45','Personnalisez votre accueil Revit','Voici le résultat : vos fichiers présentés en cartes avec Bibliothèque.'),
 ('menu',13,7,'1120:630:1410:45','01   Ouvrez les réglages','BIMaestro → Couleurs → Accueil Revit'),
 ('choix',26,7,'1600:900:465:140','02   Choisissez votre présentation','Sélectionnez Bibliothèque parmi les cinq présentations.'),
 ('appliquer',36,4.3,'1600:900:465:140','03   Enregistrez votre choix','Cliquez sur « Enregistrer et afficher l’accueil ».'),
 ('resultat',58,6,'2534:1260:0:45','Votre nouvel accueil est prêt','Retrouvez vos projets et familles dans la présentation Bibliothèque.'),
]
for name,start,dur,crop,title,sub in scenes:
 banner(name,title,sub)
 # Fill a 16:9 viewport with preserved proportions, identical for selection/save.
 vf=f'[0:v]crop={crop},scale=1920:900:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:90:color=0xf3f6f5,setsar=1,fps=30,format=yuv420p[v];[v][1:v]overlay=0:0[out]'
 run(['-ss',str(start),'-i','capture.mkv','-i',name+'.png','-filter_complex',vf,'-map','[out]','-t',str(dur),'-an','-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p',name+'.mp4'])
args=[]
for s in scenes: args+=['-i',s[0]+'.mp4']
graph=[]; prev='0:v'; total=scenes[0][2]
for i in range(1,len(scenes)):
 label=f'x{i}';graph.append(f'[{prev}][{i}:v]xfade=transition=fade:duration=0.35:offset={total-.35}[{label}]');prev=label;total+=scenes[i][2]-.35
run([*args,'-filter_complex',';'.join(graph),'-map',f'[{prev}]','-an','-c:v','libx264','-preset','fast','-crf','19','-pix_fmt','yuv420p','-movflags','+faststart','BIMaestro-Accueil-Revit-v2.mp4'])
run(['-i','BIMaestro-Accueil-Revit-v2.mp4','-vf','fps=1/3,scale=640:-1,tile=3x3','-frames:v','1','verification.jpg'])
print(f'Durée prévue : {total:.2f} s')
