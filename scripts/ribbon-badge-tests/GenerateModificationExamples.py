from pathlib import Path
import sys, math
sys.path.insert(0, str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image, ImageDraw, ImageFont

ROOT = Path('BIMaestro/Content/ToolTipExamples')
BG='#F4F7FB'; BLUE='#2378E1'; INK='#263548'; GREEN='#4F9862'
FONT=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
SMALL=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
BOLD=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',24)
def base(title):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im)
    d.text((24,18),title,font=BOLD,fill=INK)
    return im,d
def box(d,r,text,color=BLUE):
    d.rounded_rectangle(r,radius=9,fill='white',outline=color,width=3)
    d.text((r[0]+14,r[1]+14),text,font=FONT,fill=color)
def foot(d,text):d.text((24,325),text,font=SMALL,fill='#65758A')
def reservation(t):
    im,d=base('Une réservation autour du réseau')
    d.polygon([(250,90),(370,113),(370,290),(250,267)],fill='#BCC9D9')
    d.polygon([(370,113),(413,93),(413,270),(370,290)],fill='#8C9DB3')
    if t>1.4:
        # Opening follows the wall plane and is centred on the same pipe axis.
        d.polygon([(302,148),(362,160),(362,212),(302,200)],fill='#344860',outline='#F1AF49',width=4)
        d.text((420,255),'Ouverture',font=FONT,fill='#BA751C')
        d.line((410,266,362,212),fill='#BA751C',width=2)
    d.line((95,238,548,143),fill='#145CA5',width=29)
    d.line((95,230,548,135),fill=BLUE,width=21)
    foot(d,'Réseau + paroi → réservation illustrée');return im
def flange(t,remove=False):
    im,d=base('Retirer les brides et reconnecter' if remove else 'Ajouter les brides aux extrémités')
    d.rounded_rectangle((75,172,280,210),radius=8,fill='#A8C9E9')
    d.rounded_rectangle((360,172,565,210),radius=8,fill='#A8C9E9')
    # A valve remains between the flange connections, including after removal.
    d.rounded_rectangle((266,173,374,209),radius=6,fill='#617D99')
    d.polygon([(280,161),(320,180),(360,161),(360,221),(320,202),(280,221)],fill='#EDAB46',outline='#B77724',width=3)
    d.line((320,177,320,127),fill='#617D99',width=7)
    d.ellipse((292,105,348,135),fill=BG,outline='#617D99',width=5)
    d.line((294,120,346,120),fill='#617D99',width=3)
    d.line((320,107,320,133),fill='#617D99',width=3)
    visible=t<2 if remove else t>1.3
    if visible:
        for x in (260,380):
            d.rounded_rectangle((x-10,143,x+10,239),radius=3,fill=BLUE)
            for y in (155,227):d.ellipse((x-4,y-4,x+4,y+4),fill='white')
    if remove and t>=2:
        for x in (260,380):d.line((x,175,x,207),fill=GREEN,width=4)
    if not remove and t>3:d.text((225,273),'Brides ajoutées',font=FONT,fill=BLUE)
    foot(d,'Exemple : vanne raccordée entre deux canalisations');return im
def overrides(t):
    im,d=base('Un réseau, plusieurs affichages')
    labels=['Original','Demi-teinte','Transparent']
    for i,label in enumerate(labels):
        x=14+i*211;d.rounded_rectangle((x,83,x+194,290),radius=10,fill='white',outline='#DBE4EF',width=2)
        col=BLUE if i==0 else '#A7B4C4' if i==1 else '#C8DEF4'
        d.line([(x+28,139),(x+155,139),(x+155,238),(x+49,238)],fill=col,width=15)
        if i==2:d.line((x+28,191,x+177,191),fill='#97A5B8',width=2)
        d.text((x+16,96),label,font=SMALL,fill=INK)
        if int(t)%3==i:d.rounded_rectangle((x,83,x+194,290),radius=10,outline=BLUE,width=4)
    foot(d,'Surcharges appliquées aux vues choisies');return im
def choose():
    im,d=base('Choisir la bride à utiliser')
    for i in range(3):
        x=30+i*205;col=BLUE if i==1 else '#9BAAC0'
        d.rounded_rectangle((x,88,x+170,283),radius=9,fill='white',outline=col,width=4 if i==1 else 2)
        d.ellipse((x+37,118,x+133,214),fill=col);d.ellipse((x+61,142,x+109,190),fill='white')
        d.text((x+24,241),'Type '+chr(65+i),font=FONT,fill=col)
    foot(d,'La famille sélectionnée devient la référence');return im
def dynamo(n):
    im,d=base('Lancer votre script Dynamo '+str(n))
    for r,txt,col in [((24,116,180,239),'Entrées',BLUE),((230,116,408,239),'Script '+str(n),'#8656BA'),((458,116,616,239),'Résultat',GREEN)]:box(d,r,txt,col)
    for x in (185,414):d.polygon([(x,169),(x+29,184),(x,199)],fill='#92A2B8')
    for j in range(3):d.line((43,181+j*14,145,181+j*14),fill='#CFDBE9',width=4)
    for j in range(2):d.ellipse((267+j*62,178,286+j*62,197),fill='#B89DD3')
    d.line([(487,188),(497,198),(518,172)],fill=GREEN,width=5)
    foot(d,'Illustration générique • résultat selon le script');return im
def settings():
    im,d=base('Associer vos scripts aux cinq boutons')
    for i in range(5):
        y=78+i*44;d.rounded_rectangle((26,y,79,y+34),radius=6,fill='#8656BA')
        d.text((45,y+1),str(i+1),font=FONT,fill='white')
        d.line((88,y+17,133,y+17),fill='#B89DD3',width=3)
        box(d,(144,y,602,y+34),'', '#D5DEEA')
        d.text((160,y+4),'Votre script '+str(i+1)+'.dyn',font=SMALL,fill=INK)
    foot(d,'Chemin du script + libellé du bouton');return im
def excel():
    im,d=base('Nomenclature ↔ fichier Excel')
    for x,col,title in [(24,BLUE,'REVIT'),(365,GREEN,'EXCEL')]:
        box(d,(x,85,x+248,277),'',col);d.rectangle((x+2,87,x+246,126),fill=col)
        d.text((x+17,94),title,font=FONT,fill='white')
        for j in range(3):
            y=140+j*37;d.text((x+16,y),'DN '+str(50+j*25),font=SMALL,fill=INK)
            d.text((x+133,y),'12,0 m' if j==0 else '8,5 m',font=SMALL,fill=INK)
            d.line((x+8,y+29,x+240,y+29),fill='#DBE4EF',width=1)
    d.text((288,153),'→',font=BOLD,fill=BLUE);d.text((288,203),'←',font=BOLD,fill=GREEN)
    foot(d,'Export / import • valeurs illustratives');return im
def rename():
    im,d=base('Une nomenclature de noms cohérente')
    for x,title in [(24,'AVANT'),(354,'APRÈS')]:d.text((x+12,80),title,font=BOLD,fill=INK)
    for i in range(3):
        y=125+i*54;box(d,(24,y,274,y+42),'', '#D5DEEA');box(d,(354,y,614,y+42),'',BLUE)
        d.text((39,y+7),'Élément '+str(i+7),font=FONT,fill='#7B899C')
        d.text((369,y+7),'CTA_N1_00'+str(i+1),font=FONT,fill=BLUE)
        d.text((296,y+5),'→',font=BOLD,fill='#94A5BA')
    foot(d,'Préfixe + niveau + numérotation • exemple');return im
def phases():
    im,d=base('Lire les phases en un regard')
    for i,(txt,col) in enumerate([('Existant','#7D8A9B'),('Démoli','#DA744E'),('Nouveau',BLUE)]):
        x=20+i*210;d.line((x+14,205,x+178,205),fill=col,width=25)
        if i==1:
            for a in range(x+14,x+178,24):d.line((a,187,a+16,221),fill='white',width=3)
        d.text((x+15,116),txt,font=BOLD,fill=col)
        d.ellipse((x+72,253,x+90,271),fill=col)
    foot(d,'Phases de création et de démolition illustrées');return im
def purge():
    im,d=base('Choisir ce qui peut être supprimé')
    for i,(label,selected) in enumerate([('Vue non placée',True),('Famille inutilisée',True),('Vue utilisée',False)]):
        y=94+i*65;box(d,(24,y,418,y+48),'', '#D5DEEA')
        d.rectangle((38,y+13,59,y+34),fill=BLUE if selected else '#ECF0F5')
        if selected:d.line([(42,y+24),(47,y+29),(56,y+17)],fill='white',width=3)
        d.text((75,y+10),label,font=FONT,fill=INK)
    d.rounded_rectangle((463,106,605,273),radius=12,fill='#E5EEF8')
    # Recognisable bin silhouette: handle, lid, tapered body and three ribs.
    d.rounded_rectangle((520,156,549,166),radius=4,outline='#617D99',width=4)
    d.rounded_rectangle((494,168,577,177),radius=3,fill='#617D99')
    d.polygon([(501,184),(570,184),(563,250),(508,250)],fill='#9BB4CF',outline='#617D99',width=3)
    for x in (520,535,550):d.line((x,195,x,237),fill='white',width=4)
    d.text((482,118),'Purger',font=FONT,fill=BLUE)
    foot(d,'Sélection préalable • les éléments utilisés sont conservés');return im

for name,draw in [('ModReservation',reservation),('ModFlanges',flange),('ModRemoveFlanges',lambda t:flange(t,True)),('ModOverrides',overrides)]:
    writer=imageio_ffmpeg.write_frames(str(ROOT/(name+'.mp4')),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart']);writer.send(None)
    for n in range(120):
        im=draw(n/20)
        if n==70:im.save(ROOT/(name+'Poster.png'))
        writer.send(im.tobytes())
    writer.close();print(name)
for name,draw in [('ModChooseFlange',choose),('ModDynamoSettings',settings),('ModExcel',excel),('ModRename',rename),('ModPhases',phases),('ModPurge',purge)]+[('ModDynamo'+str(n),lambda n=n:dynamo(n)) for n in range(1,6)]:
    draw().save(ROOT/(name+'.png'));print(name)
