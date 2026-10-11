from pathlib import Path
import sys,math
sys.path.insert(0,str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image,ImageDraw,ImageFont
ROOT=Path('BIMaestro/Content/ToolTipExamples');BG='#F4F7FB';BLUE='#2378E1';INK='#263548'
FONT=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
SMALL=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)
BOLD=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',24)
def base(title):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im);d.text((24,18),title,font=BOLD,fill=INK);return im,d
def foot(d,txt):d.text((24,325),txt,font=SMALL,fill='#65758A')
def valve(d,x,y,color,scale=1):
    def pt(a,b):return (x+a*scale,y+b*scale)
    d.line([pt(-30,0),pt(30,0)],fill='#B9C9DB',width=max(2,int(9*scale)))
    d.polygon([pt(-18,-11),pt(0,0),pt(18,-11),pt(18,11),pt(0,0),pt(-18,11)],fill=color)
    d.line([pt(0,-3),pt(0,-23)],fill=color,width=max(2,int(3*scale)))
    d.line([pt(-12,-23),pt(12,-23)],fill=color,width=max(2,int(4*scale)))
def selection(t):
    im,d=base('Un objet → filtrer → sélectionner')
    for i,txt in enumerate(['1  RÉFÉRENCE','2  FAMILLE / TYPE','3  RÉSULTAT']):
        x=8+i*214;d.rounded_rectangle((x,79,x+196,295),radius=9,fill='white',outline='#D8E2EE',width=2)
        d.text((x+11,93),txt,font=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',17),fill=INK)
    valve(d,105,192,BLUE,1.65);d.rounded_rectangle((57,143,153,222),radius=7,outline=BLUE,width=3)
    d.text((23,257),'Vanne choisie',font=SMALL,fill=BLUE)
    for j,txt in enumerate(['Vanne A','Vanne B']):
        y=157+j*42;d.rectangle((235,y,255,y+20),fill=BLUE if j==0 and t>1.2 else '#E5EBF3')
        if j==0 and t>1.2:d.line([(239,y+9),(244,y+15),(252,y+4)],fill='white',width=3)
        d.text((266,y-2),txt,font=SMALL,fill=INK)
    d.text((236,252),'Vue / maquette',font=SMALL,fill='#65758A')
    for x,y,is_a in [(475,170,True),(566,170,False),(475,236,True),(566,236,True)]:
        col=BLUE if is_a and t>2.4 else '#9AABBF';valve(d,x,y,col)
        if is_a and t>2.4:d.rounded_rectangle((x-33,y-30,x+33,y+17),radius=5,outline=BLUE,width=2)
    foot(d,'Exemple : sélectionner les vannes du type choisi');return im
def booster(t):
    im,d=base('Prévisualiser une rotation MEP')
    d.rounded_rectangle((32,80,608,294),radius=10,fill='white',outline='#D8E2EE',width=2)
    cx,cy=238,196
    d.ellipse((cx-85,cy-85,cx+85,cy+85),outline='#D3E1F2',width=3)
    for a in (0,45,90,135,180,225,270,315):
        rad=math.radians(a);x=cx+85*math.cos(rad);y=cy-85*math.sin(rad)
        d.ellipse((x-8,y-8,x+8,y+8),fill=BLUE if a==90 and t>1 else '#C0D1E5')
    # A free elbow rotates around its origin, with a ghost of its initial position.
    d.line([(cx-48,cy),(cx,cy),(cx,cy-48)],fill='#D8E3EF',width=15)
    angle=min(max((t-1.5)/2,0),1)*math.pi/2
    points=[]
    for x,y in [(-48,0),(0,0),(0,-48)]:points.append((cx+x*math.cos(angle)-y*math.sin(angle),cy+x*math.sin(angle)+y*math.cos(angle)))
    d.line(points,fill=BLUE,width=13,joint='curve')
    d.ellipse((cx-5,cy-5,cx+5,cy+5),fill='white',outline=BLUE,width=2)
    d.text((376,119),'Survol : aperçu',font=FONT,fill=INK)
    d.text((376,169),'90°',font=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',42),fill=BLUE)
    d.text((376,231),'Clic : appliquer',font=FONT,fill=BLUE)
    foot(d,'Raccord libre illustré • rotation selon les actions autorisées');return im
def colors(t):
    im,d=base('Reconnaître les réseaux par leur couleur')
    d.rounded_rectangle((26,80,386,296),radius=10,fill='white',outline='#D8E2EE',width=2)
    for i,(txt,col) in enumerate([('Réseau A',BLUE),('Réseau B','#E67D43'),('Réseau C','#55A373')]):
        y=123+i*61;d.line([(47,y),(321-i*38,y),(321-i*38,y+32)],fill=col if t>1.2 else '#97A8BC',width=10)
        d.ellipse((417,y-7,437,y+13),fill=col);d.text((449,y-12),txt,font=FONT,fill=INK)
    foot(d,'Couleurs définies par les types de systèmes');return im
def walkthrough(t):
    im,d=base('Explorer la maquette à la première personne')
    d.rounded_rectangle((25,78,615,296),radius=8,fill='#DFE8F2',outline='#BCCBDC',width=2)
    shift=int(15*math.sin(t*.7));left=238-shift;right=400+shift
    d.polygon([(26,79),(left,124),(left,243),(26,294)],fill='#A9BCD2')
    d.polygon([(614,79),(right,124),(right,243),(614,294)],fill='#C3D1E2')
    d.rectangle((left,124,right,243),fill='#EDF2F7')
    d.polygon([(26,294),(left,243),(right,243),(614,294)],fill='#C7D4E3')
    d.rectangle((300,153,340,242),fill='#8CABC6');d.line((304,159,336,159),fill='#647E9B',width=3)
    for y,col in [(101,BLUE),(115,'#E67D43')]:d.line([(35,y),(left,y+32),(right,y+32),(605,y)],fill=col,width=7)
    d.line((307,195,329,195),fill='white',width=2);d.line((318,184,318,206),fill='white',width=2)
    # Movement key lights up to make the viewpoint animation readable.
    for x,y,txt in [(64,215,'Z'),(34,251,'Q'),(64,251,'S'),(94,251,'D')]:
        d.rounded_rectangle((x,y,x+27,y+29),radius=4,fill=BLUE if txt=='Z' else 'white');d.text((x+7,y+3),txt,font=SMALL,fill='white' if txt=='Z' else INK)
    foot(d,'Visite illustrée • déplacement et inspection en 3D');return im
for name,draw in [('Highlight',selection),('BetaBooster',booster),('BetaColors',colors),('BetaWalkthrough',walkthrough)]:
    writer=imageio_ffmpeg.write_frames(str(ROOT/(name+'.mp4')),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart']);writer.send(None)
    for n in range(120):
        im=draw(n/20)
        if n==70:im.save(ROOT/(name+'Poster.png'))
        writer.send(im.tobytes())
    writer.close();print(name)
