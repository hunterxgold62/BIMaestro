from pathlib import Path
import sys,math
sys.path.insert(0,str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image,ImageDraw,ImageFont
ROOT=Path('BIMaestro/Content/ToolTipExamples');BG='#F4F7FB';BLUE='#2378E1';INK='#263548';PURPLE='#8656BA';GREEN='#4F9862'
FONT=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
SMALL=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
BOLD=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',24)
def base(title):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im);d.text((24,18),title,font=BOLD,fill=INK);return im,d
def foot(d,txt):d.text((24,325),txt,font=SMALL,fill='#65758A')
def card(d,r):d.rounded_rectangle(r,radius=9,fill='white',outline='#D7E2EE',width=2)
def weight(t):
    im,d=base('Repérer ce qui alourdit la maquette')
    card(d,(24,78,615,298));progress=min(max(t/1.6,0),1)
    for i,(label,size,col) in enumerate([('Famille A',24,'#DF8352'),('Import CAO',16,BLUE),('Famille B',6,'#7DAE9B')]):
        y=110+i*59;d.text((41,y),label,font=FONT,fill=INK)
        d.rounded_rectangle((212,y+4,518,y+27),radius=5,fill='#EAF0F7')
        end=212+int(306*size/24*progress)
        if end>213:d.rounded_rectangle((212,y+4,end,y+27),radius=5,fill=col)
        d.text((538,y+2),str(size)+' Mo',font=SMALL,fill=col)
    if t>2:d.rounded_rectangle((32,97,605,152),radius=6,outline='#DF8352',width=3)
    foot(d,'Classement par taille • valeurs illustratives');return im
def time(t):
    im,d=base('Comparer le temps passé par projet')
    card(d,(24,82,223,296));d.ellipse((56,122,192,258),outline='#E3EAF4',width=20)
    p=min(max(t/2,0),1)
    d.arc((56,122,192,258),-90,-90+210*p,fill=PURPLE,width=20)
    d.arc((56,122,192,258),120,120+105*p,fill=BLUE,width=20)
    d.arc((56,122,192,258),225,225+45*p,fill='#78A895',width=20)
    d.text((89,174),'12 h',font=BOLD,fill=INK)
    card(d,(244,82,615,296))
    for i,(label,hrs,col) in enumerate([('Projet A',7,PURPLE),('Projet B',3.5,BLUE),('Projet C',1.5,'#78A895')]):
        y=108+i*58;d.ellipse((262,y+4,277,y+19),fill=col);d.text((288,y),label,font=FONT,fill=INK)
        d.rounded_rectangle((288,y+30,506,y+43),radius=4,fill='#EAF0F7')
        end=288+int(218*hrs/7*p)
        if end>289:d.rounded_rectangle((288,y+30,end,y+43),radius=4,fill=col)
        d.text((529,y+18),str(hrs).replace('.',',')+' h',font=SMALL,fill=col)
    foot(d,'Répartition et durées • données de démonstration');return im
def history(t):
    im,d=base('Voir le déplacement enregistré')
    card(d,(24,79,615,255));d.rectangle((47,96,591,237),outline='#A7B7CA',width=3)
    d.line((224,97,224,237),fill='#D1DBE8',width=2);d.line((419,97,419,237),fill='#D1DBE8',width=2)
    d.rectangle((80,124,545,141),outline='#95A6BA',width=2)
    p=min(max((t-1)/2,0),1);y=int(124+64*p)
    d.rounded_rectangle((80,y,545,y+17),radius=4,fill=BLUE)
    if p>0:
        d.line((573,141,573,197),fill=BLUE,width=2);d.polygon([(567,188),(579,188),(573,198)],fill=BLUE)
    d.line((77,284,568,284),fill='#C6D6E8',width=3)
    for i,(label,col) in enumerate([('Création',GREEN),('Déplacement',BLUE),('Modification',PURPLE)]):
        x=112+i*197;d.ellipse((x-7,277,x+7,291),fill=col);d.text((x-46,299),label,font=SMALL,fill=col)
    return im
def objectinfo():
    im,d=base('Le dernier changement au bon endroit')
    card(d,(24,83,615,296));d.rectangle((51,112,263,267),outline='#B6C5D6',width=3)
    d.line([(72,151),(230,151),(230,237)],fill='#B6C5D6',width=11)
    d.rounded_rectangle((131,136,195,163),radius=4,fill=BLUE);d.rounded_rectangle((125,129,201,170),radius=5,outline=BLUE,width=2)
    d.line([(202,151),(288,151),(316,183)],fill=BLUE,width=2)
    d.rounded_rectangle((312,126,592,232),radius=9,fill='#EAF2FC',outline=BLUE,width=2)
    d.text((329,144),'Dernier changement',font=FONT,fill=BLUE)
    d.text((329,185),'Déplacé • utilisateur A',font=SMALL,fill=INK)
    foot(d,'Objet sélectionné → information connue • exemple');return im
def tracker():
    im,d=base('Suivre les ouvertures de la maquette')
    card(d,(24,84,235,295));d.polygon([(73,146),(128,118),(184,146),(128,174)],fill='#D6E7FA',outline=BLUE,width=2)
    d.polygon([(73,146),(128,174),(128,239),(73,211)],fill='#A6C5E8',outline=BLUE,width=2)
    d.polygon([(128,174),(184,146),(184,211),(128,239)],fill='#82ACDD',outline=BLUE,width=2)
    d.text((61,259),'Maquette A',font=FONT,fill=BLUE)
    card(d,(257,84,615,295));d.line((284,121,284,257),fill='#C9D9EB',width=3)
    for i,(label,user,col) in enumerate([('Ouverture','Utilisateur A',GREEN),('Fermeture','Utilisateur A','#899BAF'),('Ouverture','Utilisateur B',BLUE)]):
        y=118+i*59;d.ellipse((277,y-1,291,y+13),fill=col)
        d.text((307,y-7),label,font=FONT,fill=col);d.text((307,y+22),user,font=SMALL,fill='#65758A')
    foot(d,'Registre collaboratif • événements illustratifs');return im
for name,draw in [('AnalysisWeight',weight),('AnalysisTime',time),('AnalysisHistory',history)]:
    writer=imageio_ffmpeg.write_frames(str(ROOT/(name+'.mp4')),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart']);writer.send(None)
    for n in range(120):
        im=draw(n/20)
        if n==70:im.save(ROOT/(name+'Poster.png'))
        writer.send(im.tobytes())
    writer.close();print(name)
for name,draw in [('AnalysisObjectInfo',objectinfo),('AnalysisTracker',tracker)]:draw().save(ROOT/(name+'.png'));print(name)
