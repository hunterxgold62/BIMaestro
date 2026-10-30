from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import subprocess, json

ROOT=Path(__file__).resolve().parent
FONT='C:/Windows/Fonts/segoeui.ttf'
BOLD='C:/Windows/Fonts/segoeuib.ttf'
def font(n,b=False): return ImageFont.truetype(BOLD if b else FONT,n)
def run(args):
    subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y',*args],check=True,cwd=ROOT)
def overlay(name,title,subtitle,arrow=None):
    im=Image.new('RGBA',(1920,1080)); d=ImageDraw.Draw(im)
    d.rectangle((0,0,1920,99),fill='#103e32')
    d.text((48,25),'BIMaestro',font=font(36,True),fill='white')
    d.text((340,29),title,font=font(30,True),fill='white')
    d.rectangle((0,980,1920,1080),fill='#103e32')
    d.text((48,1008),subtitle,font=font(29),fill='white')
    if arrow:
        x,y=arrow
        d.line((x+110,y-70,x+10,y-6),fill='#efab32',width=9)
        d.polygon([(x,y),(x+13,y-30),(x+30,y-2)],fill='#efab32')
        d.ellipse((x-14,y-14,x+14,y+14),outline='#efab32',width=4)
    im.save(ROOT/(name+'.png'))

intro=Image.new('RGB',(1920,1080),'#103e32'); d=ImageDraw.Draw(intro)
d.text((130,210),'BIMaestro',font=font(60,True),fill='#9bdec4')
d.text((130,365),'Votre accueil Revit,',font=font(86,True),fill='white')
d.text((130,475),'à votre façon.',font=font(86,True),fill='white')
d.rounded_rectangle((130,670,1120,770),radius=22,fill='#1d5846')
d.text((165,690),'Choisir · Enregistrer · Retrouver ses fichiers',font=font(36),fill='white')
intro.save(ROOT/'intro.png')
run(['-loop','1','-i','intro.png','-t','3','-vf','fade=t=in:d=0.3,fade=t=out:st=2.7:d=0.3','-r','30','-c:v','libx264','-pix_fmt','yuv420p','intro.mp4'])
scenes=[
 ('01-menu',73,8,'980:552:1550:85','1 / 3   Ouvrir Accueil Revit','Dans BIMaestro, ouvrez Couleurs, puis Accueil Revit.',(1180,490)),
 ('02-choix',94,10,'1484:1042:525:179','2 / 3   Choisir sa présentation','Cinq présentations disponibles. Ici, choisissez Bibliothèque.',(390,617)),
 ('03-valider',56,7,'1484:1042:525:179','3 / 3   Appliquer le changement','Cliquez sur « Enregistrer et afficher l’accueil ». ',(1400,650)),
 ('04-resultat',61,8,'2534:1250:0:80','Votre nouvel accueil est prêt','Bibliothèque : des cartes et des aperçus pour retrouver vos fichiers.',None),
]
for name,start,duration,crop,title,subtitle,arrow in scenes:
    overlay(name,title,subtitle,arrow)
    vf=f'[0:v]crop={crop},scale=1920:880:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:100:color=0xf3f6f5,setsar=1[v];[v][1:v]overlay=0:0,fade=t=in:d=0.18,fade=t=out:st={duration-.18}:d=0.18[out]'
    run(['-ss',str(start),'-i','capture.mkv','-i',name+'.png','-filter_complex',vf,'-map','[out]','-t',str(duration),'-r','30','-an','-c:v','libx264','-preset','fast','-crf','20','-pix_fmt','yuv420p',name+'.mp4'])
(ROOT/'liste.txt').write_text(''.join("file '"+n+".mp4'\n" for n in ['intro']+[s[0] for s in scenes]),encoding='utf-8')
run(['-f','concat','-safe','0','-i','liste.txt','-c','copy','-movflags','+faststart','BIMaestro-Accueil-Revit.mp4'])
run(['-ss','16','-i','BIMaestro-Accueil-Revit.mp4','-frames:v','1','apercu.jpg'])
print(ROOT/'BIMaestro-Accueil-Revit.mp4')

