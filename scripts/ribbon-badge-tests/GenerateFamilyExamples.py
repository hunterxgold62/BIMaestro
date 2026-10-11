from pathlib import Path
import sys,math
sys.path.insert(0,str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image,ImageDraw,ImageFont
ROOT=Path('BIMaestro/Content/ToolTipExamples');BG='#F4F7FB';BLUE='#2378E1';INK='#263548';GREEN='#4F9862'
FONT=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
SMALL=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
BOLD=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',24)
def base(title):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im);d.text((24,18),title,font=BOLD,fill=INK);return im,d
def foot(d,txt):d.text((24,325),txt,font=SMALL,fill='#65758A')
def card(d,r,col='#D7E2EE'):d.rounded_rectangle(r,radius=9,fill='white',outline=col,width=2)
def table(d,x,y,s=1,col=BLUE):
    def p(a,b):return(x+a*s,y+b*s)
    d.polygon([p(-45,-18),p(10,-35),p(53,-13),p(-2,6)],fill='#D6E5F7',outline=col,width=2)
    for a,b in [(-40,-13),(48,-11),(-2,3)]:d.line([p(a,b),p(a,b+34)],fill=col,width=4)
def tick(d,x,y,col=GREEN):d.line([(x,y+7),(x+7,y+14),(x+21,y-3)],fill=col,width=4)
def browser(t):
    im,d=base('Trouver une famille et la charger')
    card(d,(24,80,225,295));d.text((39,97),'DOSSIERS',font=SMALL,fill=INK)
    for i,txt in enumerate(['Mobilier','Équipements','MEP']):
        y=142+i*43;d.rectangle((39,y+5,58,y+20),fill='#D7B371');d.text((68,y),txt,font=SMALL,fill=INK)
    card(d,(248,80,615,295));d.text((265,94),'Mobilier',font=FONT,fill=BLUE)
    for i in range(3):
        x=264+i*112;card(d,(x,143,x+98,240),BLUE if i==1 and t>1 else '#D7E2EE');table(d,x+48,191,.7)
    if t>1:
        d.rounded_rectangle((358,258,581,283),radius=5,fill=BLUE);d.text((387,261),'Charger la famille',font=SMALL,fill='white')
    if t>3:tick(d,282,266)
    foot(d,'Dossiers → aperçu → chargement • exemple illustré');return im
def radial(t):
    im,d=base('Vos familles accessibles dans la rosace')
    cx,cy=212,193;d.ellipse((112,93,312,293),outline='#DAE5F1',width=2)
    for i in range(6):
        a=i*math.pi/3;x=cx+82*math.cos(a);y=cy+82*math.sin(a)
        active=i==0 and t>1;d.ellipse((x-29,y-29,x+29,y+29),fill='#D8E9FC' if active else 'white',outline=BLUE if active else '#B8C9DD',width=3)
        if i%2==0:table(d,x,y,.34)
        else:d.rounded_rectangle((x-12,y-13,x+12,y+13),radius=3,fill='#B4C9E0')
    d.ellipse((cx-24,cy-24,cx+24,cy+24),fill=BLUE)
    d.line((cx-9,cy,cx+9,cy),fill='white',width=3);d.line((cx,cy-9,cx,cy+9),fill='white',width=3)
    if t>1:
        card(d,(363,113,614,281));table(d,486,193,1.3);d.text((389,253),'Famille choisie',font=SMALL,fill=BLUE)
        d.line((322,193,350,193),fill=BLUE,width=3)
    foot(d,'Raccourci → menu radial → famille • exemple');return im
def convert():
    im,d=base('Du paramètre partagé au paramètre de famille')
    for x,title,col in [(24,'PARTAGÉ','#8656BA'),(354,'FAMILLE',BLUE)]:
        card(d,(x,91,x+262,292),col);d.text((x+18,111),title,font=BOLD,fill=col)
        d.text((x+18,158),'Largeur',font=FONT,fill=INK)
        d.text((x+18,204),'Longueur • type',font=SMALL,fill='#65758A')
        d.text((x+18,248),'Dimensions',font=SMALL,fill='#65758A')
    d.text((303,178),'→',font=BOLD,fill=BLUE)
    foot(d,'Même nom, groupe et mode instance / type');return im
def purge():
    im,d=base('Nettoyer les paramètres inutilisés')
    card(d,(24,85,397,294));d.text((42,102),'PARAMÈTRES',font=SMALL,fill=INK)
    for i,(txt,used) in enumerate([('Largeur',True),('Hauteur',True),('Ancien code',False)]):
        y=150+i*47;d.text((44,y),txt,font=FONT,fill=INK if used else '#98A5B6')
        if used:tick(d,342,y+2)
        else:d.line((41,y+14,179,y+14),fill='#CD665C',width=3);d.line((343,y+3,359,y+19),fill='#CD665C',width=3);d.line((359,y+3,343,y+19),fill='#CD665C',width=3)
    card(d,(427,111,613,269));d.text((447,129),'SAUVEGARDE',font=SMALL,fill=BLUE)
    d.polygon([(468,169),(542,169),(563,190),(563,243),(468,243)],fill='#E6F0FB',outline=BLUE,width=3)
    d.line([(542,169),(542,190),(563,190)],fill=BLUE,width=2)
    foot(d,'Dépendances vérifiées • sauvegarde avant nettoyage');return im
def translation(views=False):
    im,d=base('Traduire les noms des vues' if views else 'Traduire les noms des paramètres')
    pairs=[('Floor Plan','Plan d’étage'),('Front','Avant'),('Section','Coupe')] if views else [('Width','Largeur'),('Height','Hauteur'),('Material','Matériau')]
    d.text((42,79),'AVANT',font=BOLD,fill='#8656BA');d.text((372,79),'FRANÇAIS',font=BOLD,fill=BLUE)
    for i,(a,b) in enumerate(pairs):
        y=123+i*57;card(d,(24,y,278,y+43));card(d,(355,y,614,y+43),BLUE)
        d.text((41,y+7),a,font=FONT,fill=INK);d.text((372,y+7),b,font=FONT,fill=BLUE);d.text((303,y+5),'→',font=BOLD,fill='#8656BA')
    foot(d,'Exemples illustrés • noms déjà français conservés');return im
def units(importing=False):
    im,d=base('Recharger les unités du projet' if importing else 'Sauvegarder les unités du projet')
    card(d,(24,92,291,287));d.text((42,106),'UNITÉS + PRÉCISION',font=SMALL,fill=BLUE)
    for i,(label,value) in enumerate([('Longueur','mm'),('Surface','m²'),('Précision','0,1')]):
        y=153+i*40;d.text((42,y),label,font=FONT,fill=INK);d.text((219,y),value,font=FONT,fill=BLUE)
    d.text((310,175),'←' if importing else '→',font=BOLD,fill=BLUE)
    d.polygon([(395,112),(535,112),(570,147),(570,272),(395,272)],fill='white',outline=BLUE,width=3)
    d.line([(535,112),(535,147),(570,147)],fill=BLUE,width=3)
    d.rounded_rectangle((415,167,551,213),radius=6,fill=BLUE);d.text((446,176),'JSON',font=BOLD,fill='white')
    d.text((418,237),'RevitUnits',font=SMALL,fill=INK)
    foot(d,'Fichier de préférences • valeurs illustratives');return im
for name,draw in [('FamilyBrowser',browser),('FamilyRadial',radial)]:
    writer=imageio_ffmpeg.write_frames(str(ROOT/(name+'.mp4')),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart']);writer.send(None)
    for n in range(120):
        im=draw(n/20)
        if n==70:im.save(ROOT/(name+'Poster.png'))
        writer.send(im.tobytes())
    writer.close();print(name)
for name,draw in [('FamilyConvert',convert),('FamilyPurge',purge),('FamilyTranslate',translation),('FamilyTranslateViews',lambda:translation(True)),('FamilyUnitsExport',units),('FamilyUnitsImport',lambda:units(True))]:draw().save(ROOT/(name+'.png'));print(name)
