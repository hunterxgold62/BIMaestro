from pathlib import Path
import sys, math
sys.path.insert(0, str(Path('tmp/ribbon-video-tools').resolve()))
import imageio_ffmpeg
from PIL import Image, ImageDraw, ImageFont
root = Path('BIMaestro/Content/ToolTipExamples')
font = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 13)
small = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 11)
writer = imageio_ffmpeg.write_frames(str(root/'Selection.mp4'), (320,180), fps=20, codec='libx264', pix_fmt_in='rgb24', pix_fmt_out='yuv420p', macro_block_size=1, output_params=['-movflags','+faststart'], ffmpeg_log_level='error')
writer.send(None)
for n in range(120):
    t = n/20
    img = Image.new('RGB',(320,180),'#F4F7FB')
    d = ImageDraw.Draw(img)
    title = '1  Choisir un élément' if t < 2 else '2  Rechercher les similaires' if t < 4 else '3  Les éléments sont sélectionnés'
    d.text((12,10),title,font=font,fill='#263548')
    d.rectangle((16,39,303,146),outline='#ADBACA',width=2)
    d.line((159,39,159,146),fill='#ADBACA',width=2)
    for i, (x,y) in enumerate([(48,64),(108,106),(194,64),(254,106)]):
        selected = (t >= 4 or (i==0 and t>=1))
        color = '#2378E1' if selected else '#A9B6C4'
        d.rounded_rectangle((x-15,y-10,x+15,y+10),radius=3,fill=color)
        if selected: d.rounded_rectangle((x-19,y-14,x+19,y+14),radius=4,outline='#2378E1',width=1)
    if t < 2:
        x = int(95-40*min(t,1)); y=int(95-20*min(t,1))
        d.polygon([(x,y),(x+3,y+15),(x+7,y+10),(x+12,y+10)],fill='#263548',outline='white')
    d.text((12,156),'Exemple illustré • sélection par similitude',font=small,fill='#65758A')
    if n==0: img.save(root/'SelectionPoster.png')
    writer.send(img.tobytes())
writer.close()
print(root/'Selection.mp4')
