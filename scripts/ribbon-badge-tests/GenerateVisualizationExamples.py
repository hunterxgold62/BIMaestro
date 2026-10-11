from pathlib import Path
import sys, math
sys.path.insert(0,str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image,ImageDraw,ImageFont
ROOT=Path('BIMaestro/Content/ToolTipExamples')
BG='#F4F7FB';BLUE='#2378E1';INK='#263548';GREEN='#4F9862'
FONT=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
SMALL=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
BOLD=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',24)
def base(title):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im)
    d.text((24,18),title,font=BOLD,fill=INK);return im,d
def foot(d,text):d.text((24,325),text,font=SMALL,fill='#65758A')
def plan(d,r,color=BLUE):
    x,y,w,h=r;d.rectangle((x,y,x+w,y+h),fill='white',outline='#92A5BC',width=3)
    d.line((x+w*.55,y,x+w*.55,y+h),fill='#92A5BC',width=3)
    d.line((x,y+h*.6,x+w*.55,y+h*.6),fill='#92A5BC',width=3)
    d.line([(x+15,y+h*.3),(x+w*.77,y+h*.3),(x+w*.77,y+h*.8)],fill=color,width=6)
def navigation(t):
    im,d=base('De la vue à sa feuille, et inversement')
    plan(d,(32,124,225,151));d.text((32,79),'VUE ACTIVE',font=FONT,fill=BLUE)
    d.rectangle((376,82,606,293),fill='white',outline='#92A5BC',width=3)
    plan(d,(395,122,187,113));d.rectangle((479,264,603,290),outline='#92A5BC',width=2)
    d.text((397,91),'FEUILLE',font=SMALL,fill=INK)
    forward=t<3
    d.text((290,164),'→' if forward else '←',font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',46),fill=BLUE)
    d.rounded_rectangle((374,80,608,295) if forward else (30,122,259,277),radius=5,outline=BLUE,width=4)
    foot(d,'Vue associée à une feuille • exemple illustré');return im
def thumbnails(t):
    im,d=base('Voir la vue avant de l’ouvrir')
    d.rounded_rectangle((24,78,240,293),radius=9,fill='white',outline='#D6E1ED',width=2)
    d.text((39,93),'Vues du projet',font=FONT,fill=INK)
    for i,txt in enumerate(['Niveau 0','Niveau 1','Coupe A']):
        y=143+i*42
        if i==1 and t>1:d.rounded_rectangle((35,y-4,226,y+30),radius=5,fill='#DCEBFB')
        d.text((49,y),txt,font=SMALL,fill=BLUE if i==1 else INK)
    if t>1:
        d.polygon([(204,185),(204,215),(212,205),(220,216),(225,212),(216,201),(230,199)],fill=INK)
        d.rounded_rectangle((279,87,615,290),radius=8,fill='white',outline=BLUE,width=3)
        d.text((293,98),'Niveau 1',font=FONT,fill=INK);plan(d,(297,143,299,128))
    foot(d,'Aperçu au survol • arborescence ou onglets');return im
def face(t):
    im,d=base('Aligner la vue sur une face choisie')
    p=min(max((t-1)/2,0),1)
    # Interpolate a perspective front face toward its face-on view.
    start=[(176,139),(378,93),(378,253),(176,279)]
    end=[(182,105),(454,105),(454,279),(182,279)]
    pts=[(int(a[0]+(b[0]-a[0])*p),int(a[1]+(b[1]-a[1])*p)) for a,b in zip(start,end)]
    depth=int(61*(1-p))
    if depth:d.polygon([pts[1],(pts[1][0]+depth,pts[1][1]+32),(pts[2][0]+depth,pts[2][1]+32),pts[2]],fill='#9EAFC3')
    d.polygon(pts,fill='#C5DDF8',outline=BLUE,width=4)
    cx=sum(x for x,y in pts)//4;cy=sum(y for x,y in pts)//4
    d.ellipse((cx-14,cy-14,cx+14,cy+14),outline=BLUE,width=3)
    d.line((cx-22,cy,cx+22,cy),fill=BLUE,width=2);d.line((cx,cy-22,cx,cy+22),fill=BLUE,width=2)
    d.text((24,72),'Face sélectionnée' if p<1 else 'Vue alignée',font=FONT,fill=BLUE)
    foot(d,'Orientation 3D • animation de principe');return im
def file(d,x,y,label,col):
    d.polygon([(x,y),(x+100,y),(x+128,y+28),(x+128,y+122),(x,y+122)],fill='white',outline=col,width=3)
    d.line([(x+100,y),(x+100,y+28),(x+128,y+28)],fill=col,width=3)
    d.rounded_rectangle((x+13,y+48,x+114,y+86),radius=6,fill=col)
    d.text((x+25,y+54),label,font=FONT,fill='white')
def schedules():
    im,d=base('Une nomenclature, deux formats')
    d.rounded_rectangle((24,111,277,269),radius=8,fill='white',outline='#94A8C0',width=3)
    d.rectangle((26,113,275,148),fill=BLUE);d.text((40,117),'NOMENCLATURE',font=SMALL,fill='white')
    for i in range(3):
        y=163+i*31;d.text((40,y),'DN '+str(50+i*25),font=SMALL,fill=INK);d.text((173,y),'12,0 m',font=SMALL,fill=INK)
    d.line((294,192,328,192),fill='#94A8C0',width=3)
    for y in (114,268):d.line([(328,192),(345,y),(365,y)],fill='#94A8C0',width=3)
    file(d,381,65,'EXCEL',GREEN);file(d,466,192,'PDF','#CE6060')
    foot(d,'Export des nomenclatures sélectionnées');return im
def dwg():
    im,d=base('Exporter plusieurs vues en DWG')
    for i in range(3):
        x=26+i*42;y=108-i*14;d.rectangle((x,y,x+167,y+170),fill='white',outline='#95A8C0',width=3)
    plan(d,(125,96,136,118))
    d.text((41,288),'Vues / feuilles',font=SMALL,fill=INK)
    d.text((285,162),'→',font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',44),fill=BLUE)
    file(d,367,98,'DWG',BLUE)
    d.text((348,249),'Projet_Niveau_1.dwg',font=SMALL,fill=INK)
    foot(d,'Export par lot • nom de fichier illustratif');return im
def paint():
    im,d=base('Identifier la zone et les couches du mur')
    for i,(col,label) in enumerate([('#B4C4D5','Support'),('#E6BD7A','Isolant'),('#ECE8E0','Finition')]):
        x=42+i*47;y=97-i*9
        d.polygon([(x,y),(x+100,y+24),(x+100,y+177),(x,y+153)],fill=col,outline='#8999AB',width=2)
    d.polygon([(183,71),(243,86),(243,239),(183,224)],fill='#6B9FCE',outline=BLUE,width=3)
    d.ellipse((199,144,225,170),outline='white',width=3)
    d.line([(225,156),(283,156),(309,107)],fill=BLUE,width=2)
    d.text((320,86),'Peinture de la zone',font=FONT,fill=BLUE)
    for i,(txt,col) in enumerate([('Peinture','#6B9FCE'),('Finition','#ECE8E0'),('Isolant','#E6BD7A'),('Support','#B4C4D5')]):
        y=132+i*36;d.rounded_rectangle((320,y,344,y+24),radius=3,fill=col,outline='#A7B5C4');d.text((356,y-1),txt,font=FONT,fill=INK)
    foot(d,'Zone cliquée + composition • matériaux illustratifs');return im

for name,draw in [('VisNavigation',navigation),('VisThumbnails',thumbnails),('VisFace',face)]:
    writer=imageio_ffmpeg.write_frames(str(ROOT/(name+'.mp4')),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart']);writer.send(None)
    for n in range(120):
        im=draw(n/20)
        if n==70:im.save(ROOT/(name+'Poster.png'))
        writer.send(im.tobytes())
    writer.close();print(name)
for name,draw in [('VisSchedule',schedules),('VisDwg',dwg),('VisPaint',paint)]:draw().save(ROOT/(name+'.png'));print(name)
