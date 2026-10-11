from pathlib import Path
import sys,math
sys.path.insert(0,str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image,ImageDraw,ImageFont
ROOT=Path('BIMaestro/Content/ToolTipExamples');BG='#F4F7FB';BLUE='#2378E1';INK='#263548';GREEN='#4F9862';PURPLE='#8656BA'
FONT=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22);SMALL=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18);BOLD=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',24)
COLORS=[BLUE,PURPLE,'#E5A349',GREEN,'#D46D84']
def base(title):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im);d.text((24,18),title,font=BOLD,fill=INK);return im,d
def foot(d,txt):d.text((24,325),txt,font=SMALL,fill='#65758A')
def card(d,r):d.rounded_rectangle(r,radius=9,fill='white',outline='#D7E2EE',width=2)
def ribbon(d,y=99,color=True,shift=0):
    for i in range(5):
        x=26+i*119;col=COLORS[(i+shift)%5] if color else '#C0CDDC';card(d,(x,y,x+109,y+111))
        for j in range(3):d.rounded_rectangle((x+12+j*28,y+18,x+31+j*28,y+53),radius=3,fill='#D6E3F3')
        d.rectangle((x+2,y+82,x+107,y+108),fill=col)
def colors(t):
    im,d=base('Personnaliser les couleurs du ruban');ribbon(d,color=t>1)
    for i,col in enumerate(COLORS):
        x=165+i*65;d.ellipse((x,257,x+34,291),fill=col)
        if i==int(t)%5:d.ellipse((x-5,252,x+39,296),outline=INK,width=2)
    foot(d,'Palette → panneaux colorés • exemple illustré');return im
def christmas(t):
    im,d=base('Animer le ruban comme une guirlande');ribbon(d,shift=int(t*2)%5)
    for i in range(12):
        x=41+i*50;y=254+int(12*math.sin(i*.55));d.line((x-25,y-5,x+25,y-5),fill='#8F9EAF',width=2);d.ellipse((x-6,y,x+6,y+16),fill=COLORS[(i+int(t*3))%5])
    foot(d,'Animation festive • palette illustrative');return im
def plan(d,r):
    x,y,w,h=r;d.rectangle((x,y,x+w,y+h),fill='#EAF1F8',outline='#92A8C0',width=2);d.line((x+w*.5,y,x+w*.5,y+h),fill='#92A8C0',width=2);d.line((x,y+h*.5,x+w*.5,y+h*.5),fill='#92A8C0',width=2)
def deck(t):
    im,d=base('Des miniatures dans les onglets')
    for i,label in enumerate(['Niveau 0','Niveau 1','Coupe A']):
        x=24+i*203;card(d,(x,99,x+185,267));d.text((x+16,113),label,font=FONT,fill=BLUE if i==1 else INK)
        if t>1:
            if i<2:
                plan(d,(x+14,155,157,91))
                d.line((x+30,230,x+30,181,x+132,181,x+132,230),fill=INK,width=4)
                d.line((x+85,181,x+85,216) if i==0 else (x+30,212,x+103,212),fill=INK,width=3)
                d.arc((x+85,208,x+111,234),180,270,fill=BLUE,width=2)
                d.line((x+122,202,x+152,202),fill=GREEN,width=5)
            else:
                d.line((x+18,239,x+168,239),fill=INK,width=4)
                d.line((x+36,239,x+36,173,x+146,173,x+146,239),fill=INK,width=4)
                d.line((x+36,207,x+146,207),fill=INK,width=4)
                d.line((x+53,188,x+128,188,x+128,223),fill=BLUE,width=5)
                d.line((x+63,173,x+63,207),fill='#A7BDD3',width=2)
    foot(d,'Onglets compacts → onglets avec miniatures');return im
def creature(d,x,y):
    # Original code-drawn pixel creature with a leafy back, illustrative only.
    d.rectangle((x-20,y-14,x+18,y+10),fill='#68AF94');d.rectangle((x-15,y+10,x-7,y+18),fill='#4A8D76');d.rectangle((x+8,y+10,x+16,y+18),fill='#4A8D76')
    d.rectangle((x+11,y-19,x+30,y+5),fill='#85C7AB');d.rectangle((x+22,y-13,x+27,y-7),fill='#D65B67');d.rectangle((x+27,y-13,x+29,y-10),fill='white')
    d.polygon([(x-21,y-13),(x-17,y-34),(x-4,y-26),(x+2,y-37),(x+10,y-13)],fill='#4E8F61')
def companion(t,whole=False):
    im,d=base('Un compagnon dans la fenêtre' if whole else 'Un compagnon sur les onglets')
    card(d,(24,82,615,293))
    for i in range(3):d.rounded_rectangle((37+i*150,96,177+i*150,126),radius=4,fill='#DDE8F4')
    plan(d,(49,146,220,123))
    x=int(339+97*math.sin(t));y=int(212+35*math.cos(t)) if whole else 127
    creature(d,x,y);d.polygon([(x+48,y-22),(x+48,y),(x+54,y-8),(x+62,y-7)],fill=INK)
    foot(d,'Suivi de la souris • compagnon pixel illustratif');return im
def snake(t):
    im,d=base('Une pause avec Snake')
    d.rounded_rectangle((55,76,585,303),radius=12,fill='#E9F1E7',outline='#BDCEBA',width=2)
    for x in range(68,580,32):d.line((x,86,x,293),fill='#D6E3D2',width=1)
    for y in range(86,295,32):d.line((68,y,573,y),fill='#D6E3D2',width=1)
    # Continuous body, rounded head and eyes pointing towards the apple.
    shift=int(t*1.5)%6
    points=[(132,262),(132,230),(132,198),(164,198),(196,198),(228,198)]
    points += [(260+i*32,198) for i in range(shift+1)]
    d.line(points,fill='#287443',width=26,joint='curve')
    for x,y in points:d.ellipse((x-13,y-13,x+13,y+13),fill='#368A50')
    hx,hy=points[-1]
    d.rounded_rectangle((hx-17,hy-17,hx+19,hy+17),radius=12,fill='#216E3C')
    for ey in (hy-9,hy+9):
        d.ellipse((hx+3,ey-5,hx+13,ey+5),fill='white')
        d.ellipse((hx+9,ey-3,hx+13,ey+3),fill='#183624')
    ax,ay=516,198
    d.ellipse((ax-17,ay-14,ax+3,ay+17),fill='#D94F53')
    d.ellipse((ax-3,ay-14,ax+17,ay+17),fill='#D94F53')
    d.line((ax,ay-12,ax+2,ay-23),fill='#79543B',width=3)
    d.ellipse((ax+2,ay-26,ax+15,ay-19),fill='#39844D')
    d.ellipse((ax-10,ay-9,ax-5,ay-3),fill='#F59691')
    foot(d,'Guidez le serpent vers la pomme • exemple animé');return im
def flappy(t):
    im,d=base('Une pause avec Flappy Bird');d.rounded_rectangle((24,80,615,294),radius=8,fill='#DCEFF8')
    for cx,cy in [(108,112),(328,100),(535,116)]:
        d.ellipse((cx-26,cy,cx+26,cy+17),fill='white');d.ellipse((cx-12,cy-10,cx+14,cy+16),fill='white')
    for j in range(3):
        x=int(50+((350+j*215-t*65)%645))
        if x>560:continue
        d.rectangle((x,81,x+47,142),fill='#388B51',outline='#256B3C',width=2)
        d.rectangle((x,240,x+47,280),fill='#388B51',outline='#256B3C',width=2)
        d.rectangle((x-5,131,x+52,144),fill='#61AF66',outline='#256B3C',width=2)
        d.rectangle((x-5,238,x+52,251),fill='#61AF66',outline='#256B3C',width=2)
        d.line((x+9,84,x+9,128),fill='#7BC780',width=4)
        d.line((x+9,254,x+9,278),fill='#7BC780',width=4)
    d.rectangle((26,281,613,291),fill='#D4C28B');d.line((26,280,613,280),fill='#70A956',width=4)
    y=int(192+12*math.sin(t*math.pi*2))
    d.polygon([(179,y),(162,y-10),(165,y+9)],fill='#DDA338')
    d.ellipse((172,y-22,223,y+22),fill='#F6C64F',outline='#BA8728',width=2)
    wing=int(5*math.sin(t*math.pi*4))
    d.ellipse((176,y-1+wing,201,y+15+wing),fill='#EAA438',outline='#BA8728',width=2)
    d.ellipse((202,y-17,222,y+3),fill='white',outline='#BA8728',width=1)
    d.ellipse((214,y-11,220,y-3),fill=INK)
    d.rounded_rectangle((218,y+2,238,y+12),radius=4,fill='#E77A3D',outline='#AF5428',width=2)
    foot(d,'Mini-jeu • passage entre les obstacles');return im
def radial(t):
    im,d=base('Vos boutons favoris dans une rosace')
    cx,cy=192,188;opening=min(1,t/.8);radius=100*opening
    d.ellipse((92,88,292,288),outline='#D7E2EE',width=2)
    selected=2 if t>1.3 else -1
    for i in range(16):
        a=i*math.pi/8;x=cx+radius*math.cos(a);y=cy+radius*math.sin(a)
        d.rounded_rectangle((x-15,y-15,x+15,y+15),radius=6,fill=BLUE if i==selected else 'white',outline=BLUE if i==selected else '#BCCDE0',width=2)
        icon(d,x,y,i,'white' if i==selected else COLORS[i%5],8)
    d.ellipse((166,162,218,214),fill=BLUE);d.line((181,188,203,188),fill='white',width=3);d.line((192,177,192,199),fill='white',width=3)
    card(d,(345,87,615,293));d.text((364,101),'FAVORIS',font=BOLD,fill=BLUE)
    for i,label in enumerate(['Vue 3D','Couleurs','Nomenclature']):
        y=153+i*48;d.rounded_rectangle((359,y-6,600,y+32),radius=6,fill='#E1EDFD' if i==2 and selected>=0 else BG)
        icon(d,382,y+12,i,BLUE,11);d.text((405,y),label,font=SMALL,fill=INK)
    if selected>=0:
        x=cx+radius/math.sqrt(2);y=cy+radius/math.sqrt(2)
        d.polygon([(x+9,y+9),(x+9,y+34),(x+16,y+25),(x+25,y+25)],fill=INK)
    foot(d,'16 favoris + boutons récents • exemple illustré');return im

def icon(d,x,y,index,col,s):
    if index%4==0:
        d.polygon([(x-s,y-s/2),(x,y-s),(x+s,y-s/2),(x+s,y+s/2),(x,y+s),(x-s,y+s/2)],outline=col,width=2)
        d.line((x-s,y-s/2,x,y,x+s,y-s/2),fill=col,width=2);d.line((x,y,x,y+s),fill=col,width=2)
    elif index%4==1:
        for dx,dy in [(-4,-4),(4,-4),(0,4)]:d.ellipse((x+dx-3,y+dy-3,x+dx+3,y+dy+3),fill=col)
    elif index%4==2:
        d.rectangle((x-s,y-s,x+s,y+s),outline=col,width=2)
        d.line((x-s,y,x+s,y),fill=col,width=2);d.line((x,y-s,x,y+s),fill=col,width=2)
    else:
        d.ellipse((x-s,y-s,x+s-3,y+s-3),outline=col,width=2);d.line((x+s-4,y+s-4,x+s+2,y+s+2),fill=col,width=3)
def reset():
    im,d=base('Réinitialiser les couleurs des onglets')
    for row,y in enumerate([111,227]):
        for i in range(3):
            x=42+i*196;d.rounded_rectangle((x,y,x+173,y+53),radius=7,fill=COLORS[i] if row==0 else '#DEE7F1');d.text((x+17,y+15),'Vue '+str(i+1),font=FONT,fill='white' if row==0 else INK)
    d.text((301,178),'↓',font=BOLD,fill=BLUE);foot(d,'Couleurs appliquées → onglets réinitialisés');return im
def guide(appearance=False):
    if not appearance:
        im,d=base('Essayer les outils étape par étape')
        for i,label in enumerate(['Choisir','Essayer','Terminer']):
            x=24+i*204;card(d,(x,92,x+185,287))
            d.text((x+14,104),str(i+1),font=BOLD,fill=BLUE)
            if i==0:
                for j in range(3):d.rounded_rectangle((x+32,155+j*23,x+150,171+j*23),radius=4,fill='#DCE9F7' if j!=1 else BLUE)
                d.polygon([(x+120,180),(x+120,206),(x+127,197),(x+136,197)],fill=INK)
            elif i==1:
                icon(d,x+90,181,0,BLUE,35)
                d.ellipse((x+115,193,x+149,227),fill='#E9B453');d.line((x+125,207,x+139,207),fill='white',width=3)
            else:
                d.ellipse((x+56,147,x+129,220),fill='#DCEEDD');d.line((x+72,183,x+88,199,x+113,166),fill=GREEN,width=7)
            d.text((x+39,248),label,font=FONT,fill=INK)
        foot(d,'Parcours guidé • choisir un exercice puis le réaliser');return im
    im,d=base('Découvrir les réglages des couleurs' if appearance else 'Essayer les outils étape par étape')
    labels=['Ruban','Palette','Résultat'] if appearance else ['Choisir','Essayer','Terminer']
    d.line((103,184,542,184),fill='#C6D7EA',width=4)
    for i,label in enumerate(labels):
        x=103+i*219;d.ellipse((x-36,148,x+36,220),fill=COLORS[i]);d.text((x-8,163),str(i+1),font=BOLD,fill='white');d.text((x-44,252),label,font=FONT,fill=INK)
    foot(d,'Parcours guidé • étapes illustratives');return im
def demo():
    im,d=base('Une maquette dédiée aux essais');card(d,(24,84,615,295))
    plan(d,(48,122,202,139));d.line((60,246,60,139,231,139,231,246),fill=INK,width=4)
    d.line((145,139,145,211),fill=INK,width=3);d.line((73,171,207,171,207,230),fill=BLUE,width=6)
    d.arc((145,208,179,242),180,270,fill=INK,width=2)
    d.text((276,176),'→',font=BOLD,fill=BLUE)
    d.polygon([(345,163),(423,122),(561,164),(483,206)],fill='#E5EDF4',outline='#8DA6BD',width=2)
    d.polygon([(345,163),(483,206),(483,276),(345,232)],fill='#C4D6E5',outline='#8DA6BD')
    d.polygon([(483,206),(561,164),(561,235),(483,276)],fill='#94B1CC',outline='#7595B3')
    for x,y in [(364,185),(410,199)]:d.polygon([(x,y),(x+25,y+8),(x+25,y+34),(x,y+26)],fill='#F3F9FC',outline=BLUE,width=2)
    d.polygon([(507,207),(539,190),(539,217),(507,234)],fill='#EAF5FC',outline=BLUE,width=2)
    d.polygon([(451,230),(471,236),(471,272),(451,266)],fill='#677F96')
    d.line((372,155,432,173,504,135),fill=BLUE,width=7);d.line((432,173,432,208),fill=BLUE,width=7)
    d.ellipse((426,201,438,213),fill='#E9B453')
    foot(d,'Plan + bâtiment + réseau • maquette illustrative');return im
def info(updates=False):
    im,d=base('Voir les nouveautés de BIMaestro' if updates else 'Découvrir BIMaestro')
    card(d,(107,79,533,296));d.rounded_rectangle((130,101,510,146),radius=6,fill=BLUE);d.text((150,111),'NOUVEAUTÉS' if updates else 'BIMaestro',font=BOLD,fill='white')
    for i in range(3):
        y=176+i*33;d.ellipse((137,y,149,y+12),fill=COLORS[i]);d.line((166,y+5,472-i*26,y+5),fill='#B7CAE1',width=6)
    foot(d,'Page d’information • présentation illustrative');return im
def options():
    im,d=base('Composer votre ruban et vos préférences');ribbon(d,y=78)
    for i in range(3):
        y=222+i*25;d.line((131,y,490,y),fill='#C4D4E6',width=4);x=202+i*97;d.ellipse((x-8,y-8,x+8,y+8),fill=COLORS[i])
    foot(d,'Organisation du ruban + réglages utilisateur');return im
def contact():
    im,d=base('Partager un retour ou une idée');card(d,(31,91,281,276));d.rounded_rectangle((57,119,251,220),radius=12,fill='#E7F0FC');d.polygon([(93,219),(93,244),(120,219)],fill='#E7F0FC');d.text((79,145),'Votre retour',font=FONT,fill=BLUE)
    d.text((299,170),'→',font=BOLD,fill=BLUE);card(d,(356,91,609,276));d.rounded_rectangle((417,116,546,245),radius=9,fill='#216AA5');d.text((447,139),'in',font=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',65),fill='white')
    foot(d,'Ouvre le profil LinkedIn • envoi par l’utilisateur');return im
def support():
    im,d=base('Soutenir le développement de BIMaestro')
    d.rounded_rectangle((163,144,397,263),radius=17,fill='white',outline='#A1B5CE',width=4);d.arc((350,153,456,245),-90,90,fill='#A1B5CE',width=9)
    d.line((143,276,426,276),fill='#A1B5CE',width=5)
    d.ellipse((224,164,268,208),fill='#D8748F');d.ellipse((266,164,310,208),fill='#D8748F');d.polygon([(225,194),(309,194),(267,237)],fill='#D8748F')
    for x in (231,271,311):d.arc((x-10,88,x+10,133),70,270,fill='#BACADD',width=3)
    foot(d,'Page Ko-fi • soutien volontaire autour d’un café');return im
for name,draw in [('InfoColors',colors),('InfoChristmas',christmas),('InfoDeck',deck),('InfoTabCompanion',lambda t:companion(t)),('InfoWindowCompanion',lambda t:companion(t,True)),('InfoSnake',snake),('InfoFlappy',flappy),('InfoRadial',radial)]:
    if len(sys.argv)>1 and name not in sys.argv[1:]:continue
    writer=imageio_ffmpeg.write_frames(str(ROOT/(name+'.mp4')),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart']);writer.send(None)
    for n in range(120):
        im=draw(n/20)
        if n==70:im.save(ROOT/(name+'Poster.png'))
        writer.send(im.tobytes())
    writer.close();print(name)
for name,draw in [('InfoReset',reset),('InfoAppearanceGuide',lambda:guide(True)),('InfoTours',guide),('InfoDemo',demo),('InfoAbout',info),('InfoUpdates',lambda:info(True)),('InfoOptions',options),('InfoContact',contact),('InfoSupport',support)]:
    if len(sys.argv)>1 and name not in sys.argv[1:]:continue
    draw().save(ROOT/(name+'.png'));print(name)
