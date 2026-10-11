from pathlib import Path
import sys,math
sys.path.insert(0,str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image,ImageDraw,ImageFont
ROOT=Path('BIMaestro/Content/ToolTipExamples');BG='#F4F7FB';BLUE='#2378E1';INK='#263548';PURPLE='#8656BA'
FONT=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
SMALL=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
BOLD=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',24)
def base(title):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im);d.text((24,18),title,font=BOLD,fill=INK);return im,d
def foot(d,txt):d.text((24,325),txt,font=SMALL,fill='#65758A')
def card(d,r):d.rounded_rectangle(r,radius=9,fill='white',outline='#D7E2EE',width=2)
def table(d,x,y,s=1,col=BLUE):
    def p(a,b):return(x+a*s,y+b*s)
    d.polygon([p(-55,-25),p(12,-44),p(65,-18),p(-4,3)],fill='#D6E5F7',outline=col,width=3)
    for a,b in [(-49,-20),(58,-16),(-4,0)]:d.line([p(a,b),p(a,b+46)],fill=col,width=5)
def family(t):
    im,d=base('D’une demande à une famille Revit')
    card(d,(24,88,257,282));d.text((40,105),'VOTRE DEMANDE',font=SMALL,fill=PURPLE)
    d.text((40,151),'Créer une table',font=FONT,fill=INK)
    d.text((40,191),'Largeur : 1,20 m',font=SMALL,fill='#65758A')
    d.text((280,167),'→',font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',40),fill=PURPLE)
    card(d,(339,88,615,282))
    if t>1:
        d.text((357,103),'FAMILLE',font=SMALL,fill=BLUE);table(d,477,194,1.45)
    if t>2.4:
        d.line((388,251,565,251),fill=BLUE,width=2)
        for x in (388,565):d.line((x,246,x,256),fill=BLUE,width=2)
        d.text((431,258),'1,20 m',font=SMALL,fill=BLUE)
    foot(d,'Création / modification • exemple de famille paramétrique');return im
def community():
    im,d=base('Parcourir les familles partagées')
    for i,(title,col) in enumerate([('Mobilier',BLUE),('Équipement',PURPLE),('MEP','#4F9862')]):
        x=12+i*211;card(d,(x,88,x+193,291));d.text((x+14,103),title,font=FONT,fill=INK)
        if i==0:table(d,x+96,182,1,col)
        elif i==1:
            d.rounded_rectangle((x+59,150,x+135,224),radius=7,fill='#E9E0F5',outline=col,width=3)
            for y in (166,183,200):d.line((x+72,y,x+121,y),fill=col,width=3)
        else:
            d.line([(x+40,166),(x+107,166),(x+107,224)],fill=col,width=17,joint='curve')
        d.rounded_rectangle((x+17,254,x+175,280),radius=5,fill='#EAF1FA')
        d.line((x+96,258,x+96,270),fill=col,width=2);d.polygon([(x+89,266),(x+103,266),(x+96,273)],fill=col)
    foot(d,'Galerie illustrative • catégories et téléchargement');return im
def chat():
    im,d=base('Un assistant relié à l’objet sélectionné')
    card(d,(24,85,260,290));table(d,143,181,1.35)
    d.rounded_rectangle((47,117,235,243),radius=8,outline=BLUE,width=3)
    d.text((61,260),'Objet sélectionné',font=SMALL,fill=BLUE)
    d.line((272,186,316,186),fill=PURPLE,width=3);d.ellipse((290,180,302,192),fill=PURPLE)
    card(d,(327,85,616,290));d.text((345,102),'CHAT + CONTEXTE',font=SMALL,fill=PURPLE)
    d.rounded_rectangle((373,141,599,187),radius=10,fill='#EEE5F8');d.text((387,153),'Analyser cet objet',font=SMALL,fill=INK)
    d.rounded_rectangle((344,205,576,269),radius=10,fill='#E8F1FB')
    for y,w in [(221,184),(237,162),(253,113)]:d.line((359,y,359+w,y),fill='#8DA9CC',width=4)
    foot(d,'Élément Revit + conversation • exemple illustré');return im
def correction():
    im,d=base('Comparer avant de valider la correction')
    for x,label,col in [(24,'AVANT','#CD665C'),(351,'PROPOSITION','#4F9862')]:
        card(d,(x,90,x+265,287));d.text((x+17,109),label,font=BOLD,fill=col)
        d.text((x+17,170),'Les vanne sont' if x==24 else 'Les vannes sont',font=FONT,fill=INK)
        d.text((x+17,205),'ouverte.' if x==24 else 'ouvertes.',font=FONT,fill=INK)
        d.line((x+61,198,x+134,198),fill=col,width=3);d.line((x+16,235,x+109,235),fill=col,width=3)
    d.text((302,176),'→',font=BOLD,fill=PURPLE)
    foot(d,'Suggestion IA • validation ou modification par l’utilisateur');return im
def audit():
    im,d=base('Repérer les anomalies sur les feuilles')
    d.rectangle((29,82,348,292),fill='white',outline='#9FB1C7',width=3)
    d.rectangle((49,103,326,270),outline='#D9E2ED',width=2)
    d.line((69,136,194,136),fill='#9FB1C7',width=3);d.rectangle((69,158,205,241),outline='#9FB1C7',width=2)
    d.text((222,146),'Texte',font=SMALL,fill=INK);d.text((222,211),'Note',font=SMALL,fill=INK)
    for y,col,n in [(159,'#CD665C','1'),(225,'#D59B36','2')]:
        d.line((221,y+11,272,y+11),fill=col,width=3);d.ellipse((285,y-1,310,y+24),fill=col);d.text((293,y),n,font=SMALL,fill='white')
    card(d,(383,105,614,270))
    for y,col,label in [(126,'#CD665C','Erreur'),(205,'#D59B36','Mineur')]:
        d.ellipse((398,y+2,416,y+20),fill=col);d.text((433,y-2),label,font=FONT,fill=INK)
        d.line((433,y+34,589,y+34),fill='#D3DEEA',width=4)
    foot(d,'Vues / feuilles choisies • anomalies illustratives');return im
def room(render):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im)
    # Identical layout and furniture geometry on both sides of the wipe.
    d.rectangle((36,88,604,290),fill='#E9D8C2' if render else 'white',outline=INK,width=6)
    if render:
        for x in range(48,602,26):d.line((x,93,x,285),fill='#D6BEA1',width=1)
    d.line((377,90,377,290),fill=INK,width=5)
    for r in [(69,124,176,229),(218,134,307,195),(419,125,556,193),(449,224,568,263)]:
        if render:d.rounded_rectangle((r[0]+6,r[1]+6,r[2]+6,r[3]+6),radius=6,fill='#C6B29A')
        d.rounded_rectangle(r,radius=6,fill=('#9DB7A3' if r[0]<200 else '#CFB090') if render else 'white',outline='#65788B',width=3)
    return im
def rendering(t):
    before=room(False);after=room(True);d=ImageDraw.Draw(before)
    progress=min(max((t-.7)/3.7,0),1);cut=int(36+568*progress)
    before.paste(after.crop((36,88,cut,291)),(36,88));d=ImageDraw.Draw(before)
    d.text((24,18),'Du plan au rendu de présentation',font=BOLD,fill=INK)
    d.line((cut,76,cut,301),fill=PURPLE,width=3);d.ellipse((cut-8,186,cut+8,202),fill=PURPLE,outline='white',width=2)
    d.text((36,303),'RENDU ILLUSTRÉ',font=SMALL,fill=PURPLE);d.text((468,303),'PLAN SOURCE',font=SMALL,fill='#65758A')
    return before
for name,draw in [('AiFamily',family),('AiRender',rendering)]:
    writer=imageio_ffmpeg.write_frames(str(ROOT/(name+'.mp4')),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart']);writer.send(None)
    for n in range(120):
        im=draw(n/20)
        if n==70:im.save(ROOT/(name+'Poster.png'))
        writer.send(im.tobytes())
    writer.close();print(name)
for name,draw in [('AiCommunity',community),('AiChat',chat),('AiCorrection',correction),('AiAudit',audit)]:draw().save(ROOT/(name+'.png'));print(name)
