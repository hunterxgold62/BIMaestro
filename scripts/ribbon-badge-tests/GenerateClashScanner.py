from pathlib import Path
import sys,math
sys.path.insert(0,str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image,ImageDraw,ImageFont
ROOT=Path('BIMaestro/Content/ToolTipExamples')
BG='#F4F7FB'
BOLD=ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf',24)
SMALL=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',18)

def scanner(t):
    im=Image.new('RGB',(640,360),BG);d=ImageDraw.Draw(im)
    d.text((24,18),'Scanner la paroi, révéler le conflit',font=BOLD,fill='#263548')
    d.ellipse((83,285,570,306),fill='#E3EAF2')
    d.polygon([(90,100),(125,82),(550,82),(515,100)],fill='#DFE7F0',outline='#A4B6C8')
    d.polygon([(515,100),(550,82),(550,270),(515,288)],fill='#93A9BF',outline='#7B94AE')
    d.rectangle((90,100,515,288),fill='#C6D3E0',outline='#A4B6C8',width=2)
    for y in (147,194,241):d.line((92,y,513,y),fill='#B8C7D6',width=1)
    for x in (180,270,360,450):d.line((x,102,x,286),fill='#B8C7D6',width=1)
    # X-ray view of two intersecting services hidden behind the same wall.
    inside=Image.new('RGB',(425,188),'#183C58');r=ImageDraw.Draw(inside)
    for x in range(0,425,24):r.line((x,0,x,188),fill='#244D6C',width=1)
    for y in range(0,188,24):r.line((0,y,425,y),fill='#244D6C',width=1)
    r.line((15,107,407,107),fill='#216CB2',width=30)
    r.line((15,100,407,100),fill='#55B6F5',width=20)
    r.line((242,8,242,169),fill='#AD682D',width=30)
    r.line((236,8,236,169),fill='#F0AC54',width=20)
    progress=min(1,max(0,(t-.6)/3.2))
    scan=int(90+425*progress)
    if t>=.6:
        mask=Image.new('L',inside.size,0);m=ImageDraw.Draw(mask)
        m.rectangle((0,0,max(0,scan-90),188),fill=255)
        im.paste(inside,(90,100),mask)
        d=ImageDraw.Draw(im)
        if progress<1:
            for width,col in [(17,'#A7E8ED'),(7,'#6EDADF'),(2,'#E8FFFF')]:d.line((scan,96,scan,292),fill=col,width=width)
            d.polygon([(scan-7,88),(scan+7,88),(scan,96)],fill='#31ADB8')
        if scan>345:
            radius=25+int(3*math.sin(t*5))
            d.ellipse((327-radius,201-radius,327+radius,201+radius),outline='#F06468',width=4)
            d.line((327,185,327,205),fill='#FFD5D6',width=4)
            d.ellipse((325,211,329,215),fill='#FFD5D6')
            d.rounded_rectangle((391,113,500,145),radius=6,fill='#C94F57')
            d.text((403,119),'CONFLIT',font=SMALL,fill='white')
    d.text((24,325),'Rayons X illustratifs • réseaux cachés et intersection',font=SMALL,fill='#65758A')
    return im

writer=imageio_ffmpeg.write_frames(str(ROOT/'ClashCut.mp4'),(640,360),fps=20,codec='libx264',pix_fmt_out='yuv420p',macro_block_size=1,output_params=['-movflags','+faststart'])
writer.send(None)
for n in range(120):
    im=scanner(n/20)
    if n==70:im.save(ROOT/'ClashCutPoster.png')
    writer.send(im.tobytes())
writer.close()
print('ClashCut scanner generated')
