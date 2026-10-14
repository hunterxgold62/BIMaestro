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
def reservation(t,illustration_only=False):
    im,d=base('Une ouverture centrée sur le réseau')
    ready=t>=1.4
    amber='#E7A33A'
    # Pipe normal to the wall: the same projected axis is used on both faces.
    def pipe(a,b):
        d.line((a,b),fill='#175B9B',width=30)
        d.line(((a[0],a[1]-5),(b[0],b[1]-5)),fill=BLUE,width=21)
        d.line(((a[0],a[1]-12),(b[0],b[1]-12)),fill='#72B6FA',width=3)
    d.ellipse((135,284,418,303),fill='#E4EBF2')
    pipe((327,182),(455,150))
    d.polygon([(222,106),(350,130),(394,108),(266,84)],fill='#E2E9F0',outline='#ACBCCD')
    d.polygon([(350,130),(394,108),(394,272),(350,294)],fill='#8FA6BB',outline='#7D96AE')
    d.polygon([(222,106),(350,130),(350,294),(222,270)],fill='#C5D3DF',outline='#A4B7C9')
    if ready:
        # Opening centred at (286,194), in the front wall plane; no offset.
        outer=[(250,151),(322,165),(322,237),(250,223)]
        d.polygon(outer,fill='#536E87',outline=amber,width=4)
        d.polygon([(254,156),(315,168),(315,230),(254,218)],fill='#EAF2F9')
        d.polygon([(254,218),(315,230),(322,237),(250,223)],fill='#C7882D')
        d.line((254,156,254,218),fill='#9B6D30',width=3)
    pipe((78,246),(286,194) if not ready else (315,187))
    d.ellipse((64,231,91,261),fill='#13558F',outline='#8DC6FA',width=2)
    d.ellipse((69,235,86,257),fill='#317ECE')
    if ready:
        # Front elevation makes the clearance around the pipe unambiguous.
        d.line((325,233,423,249),fill=amber,width=2)
    d.rounded_rectangle((433,104,613,288),radius=12,fill='white',outline='#D6E1EC',width=2)
    d.text((457,118),'Vue de face',font=SMALL,fill='#65758A')
    d.rectangle((459,153,587,264),fill='#C5D3DF')
    if ready:d.rounded_rectangle((479,164,567,252),radius=3,fill='#F4F7FB',outline=amber,width=4)
    d.ellipse((498,183,548,233),fill=BLUE,outline='#175B9B',width=3)
    d.ellipse((505,190,541,226),fill='#175B9B')
    if ready:
        d.line((478,278,568,278),fill=amber,width=2)
        for x in (478,568):d.line((x,273,x,283),fill=amber,width=2)
        if not illustration_only:
            d.ellipse((389,64,415,90),fill='#DDEEDD')
            d.line((395,77,400,82,409,70),fill=GREEN,width=3)
    if not illustration_only:d.text((24,69),'APRÈS' if ready else 'AVANT',font=SMALL,fill=GREEN if ready else '#65758A')
    foot(d,'Paroi traversée → ouverture avec jeu autour du réseau');return im
def flange(t,remove=False):
    if not remove:return exploded_flange(t)
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

def exploded_flange(t):
    im,d=base('Brides et joints autour de la vanne')
    def ease(u):
        u=max(0,min(1,u));return u*u*(3-2*u)
    spread=ease((t-.6)/1.4) if t<3.5 else 1-ease((t-3.5)/1.7)
    left,right=248-70*spread,392+70*spread
    gl,gr=265-35*spread,375+35*spread
    cy=197
    d.ellipse((60,244,580,260),fill='#E5EBF2')
    # Exploded assembly axis through all components.
    if spread>.08:
        for x in range(76,570,13):d.line((x,cy,x+6,cy),fill='#A6B6C7',width=2)
    def tube(a,b):
        d.rounded_rectangle((a,179,b,216),radius=5,fill='#A0BFDC',outline='#6B91B4',width=2)
        d.line((a+3,184,b-3,184),fill='#D2E4F4',width=4)
        d.line((a+3,211,b-3,211),fill='#779EBD',width=3)
    tube(55,left-12);tube(right+12,585)
    # Valve stays on the connection axis; spindle and handwheel identify it.
    d.rounded_rectangle((268,173,372,223),radius=13,fill='#E7AA48',outline='#AD772C',width=3)
    d.ellipse((289,168,351,229),fill='#F2BF65',outline='#AD772C',width=2)
    d.line((320,173,320,129),fill='#56738E',width=9)
    d.rounded_rectangle((308,153,332,170),radius=3,fill='#8CA5BA',outline='#56738E',width=2)
    d.ellipse((287,104,353,134),fill=BG,outline='#56738E',width=5)
    d.line((290,119,350,119),fill='#56738E',width=3)
    d.line((320,106,320,132),fill='#56738E',width=3)
    def seal(x):
        d.ellipse((x-5,163,x+5,232),fill='#DAA051',outline='#92622A',width=2)
        d.ellipse((x-2,181,x+2,215),fill=BG,outline='#92622A',width=1)
    seal(gl);seal(gr)
    def plate(x):
        d.rounded_rectangle((x-12,151,x+7,244),radius=5,fill='#175B9B')
        d.ellipse((x-5,151,x+15,244),fill=BLUE,outline='#175B9B',width=2)
        d.ellipse((x+1,181,x+9,215),fill='#163F65')
        for yy in (163,231):d.ellipse((x+2,yy-3,x+8,yy+3),fill='#DDEEFF',outline='#175B9B')
    plate(left);plate(right)
    label_font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
    if spread>.65:
        for px,tx,label,col in [(left,142,'Bride',BLUE),(gl,223,'Joint','#A77632'),(320,303,'Vanne','#A77632')]:
            d.line((px,246,px,266,tx+19,276),fill=col,width=2)
            d.text((tx,280),label,font=label_font,fill=col)
    else:
        d.ellipse((446,95,472,121),fill='#DDEEDD');d.line((452,108,457,113,466,101),fill=GREEN,width=3)
        d.text((24,69),'Raccord assemblé',font=SMALL,fill=GREEN)
    # Small site worker follows the flange and tightens the lower fixing.
    wx=right+47
    d.ellipse((wx-25,306,wx+23,316),fill='#DBE4ED')
    d.line((wx-7,291,wx-10,308),fill='#315D86',width=9)
    d.line((wx+7,291,wx+10,308),fill='#315D86',width=9)
    d.rounded_rectangle((wx-18,307,wx-5,314),radius=3,fill='#263548')
    d.rounded_rectangle((wx+5,307,wx+19,314),radius=3,fill='#263548')
    d.rounded_rectangle((wx-14,265,wx+14,292),radius=6,fill='#EAA13D',outline='#BD7B26',width=2)
    d.line((wx-7,268,wx-7,288),fill='#FFF0BB',width=3)
    d.line((wx+7,268,wx+7,288),fill='#FFF0BB',width=3)
    d.line((wx-12,282,wx+12,282),fill='#FFF0BB',width=3)
    d.ellipse((wx-10,245,wx+10,266),fill='#E9BE94',outline='#AE845F',width=1)
    d.ellipse((wx-12,236,wx+12,254),fill='#F2C548',outline='#BE9227',width=2)
    d.rectangle((wx-15,249,wx+15,253),fill='#F2C548')
    d.ellipse((wx-7,256,wx-4,259),fill=INK)
    d.line((wx-12,270,wx-24,255,right+13,241),fill='#315D86',width=7)
    d.ellipse((right+9,236,right+17,244),fill='#E9BE94')
    turn=3*math.sin(t*8) if spread<.3 else 0
    d.line((right+13,242,right+6+turn,228),fill='#637F98',width=4)
    d.arc((right+1+turn,220,right+12+turn,232),20,300,fill='#637F98',width=3)
    d.line((wx+13,272,wx+19,286),fill='#315D86',width=7)
    foot(d,'Vue éclatée → réassemblage • exemple illustratif');return im
def overrides(t):
    im,d=base('Le même réseau, trois affichages')
    labels=['Normal','Demi-teinte','Transparence']
    captions=['Opaque','Atténué, mais opaque','Arrière-plan visible']
    detail_font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',16)
    for i,label in enumerate(labels):
        x=14+i*211
        active=min(2,int(t/2))==i
        d.rounded_rectangle((x,83,x+194,301),radius=10,fill='white',outline=BLUE if active else '#DBE4EF',width=4 if active else 2)
        d.text((x+15,99),label,font=SMALL,fill=BLUE if active else INK)
        d.rounded_rectangle((x+13,137,x+181,267),radius=6,fill='#F0F4F8')
        for y in (157,190,223,256):d.line((x+19,y,x+174,y),fill='#DFE7EF',width=1)
        # Identical structural support behind the duct in every view.
        d.rectangle((x+84,145,x+113,263),fill='#EBC477',outline='#BA914C',width=2)
        for y in range(151,260,12):d.line((x+85,y,x+112,y+8),fill='#CDA75D',width=2)
        d.rectangle((x+76,250,x+121,263),fill='#EBC477',outline='#BA914C',width=2)
        # A real alpha layer reveals the support only in transparency mode.
        layer=Image.new('RGBA',im.size,(0,0,0,0));ld=ImageDraw.Draw(layer)
        fill=(35,120,225,255) if i==0 else (177,190,203,255) if i==1 else (35,120,225,65)
        edge=(22,81,144,255) if i==0 else (149,166,184,255) if i==1 else (35,120,225,140)
        shape=[(x+27,174),(x+162,174),(x+162,235),(x+133,235),(x+133,205),(x+27,205)]
        ld.polygon(shape,fill=fill,outline=edge,width=2)
        ld.line((x+30,179,x+156,179),fill=(255,255,255,125 if i!=2 else 35),width=3)
        ld.line((x+155,182,x+155,231),fill=edge,width=3)
        im.paste(layer,(0,0),layer)
        d=ImageDraw.Draw(im)
        width=d.textlength(captions[i],font=detail_font)
        d.text((x+(194-width)/2,278),captions[i],font=detail_font,fill='#65758A')
    foot(d,'Normal → demi-teinte → transparence • même réseau');return im
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
    if len(sys.argv)>1 and name not in sys.argv[1:]:continue
    writer=imageio_ffmpeg.write_frames(str(ROOT/(name+'.mp4')),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart']);writer.send(None)
    for n in range(120):
        im=draw(n/20)
        if n==70:im.save(ROOT/(name+'Poster.png'))
        writer.send(im.tobytes())
    writer.close();print(name)
for name,draw in [('ModChooseFlange',choose),('ModDynamoSettings',settings),('ModExcel',excel),('ModRename',rename),('ModPhases',phases),('ModPurge',purge)]+[('ModDynamo'+str(n),lambda n=n:dynamo(n)) for n in range(1,6)]:
    if len(sys.argv)>1 and name not in sys.argv[1:]:continue
    draw().save(ROOT/(name+'.png'));print(name)
