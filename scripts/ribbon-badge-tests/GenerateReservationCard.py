from pathlib import Path
import sys,runpy
from PIL import Image,ImageDraw
sys.argv=['GenerateModificationExamples.py','preview-only']
module=runpy.run_path('scripts/ribbon-badge-tests/GenerateModificationExamples.py')
root=Path('BIMaestro/Content/ToolTipExamples')
module['reservation'](3.5,True).crop((48,82,620,308)).resize((640,252),Image.Resampling.LANCZOS).save(root/'ReservationCardHero.png')
for i in range(3):
    im=Image.new('RGB',(88,88),'#EEF4FA');d=ImageDraw.Draw(im)
    if i==0:
        d.line((17,64,17,29,70,29),fill='#2378E1',width=13)
        d.line((17,24,70,24),fill='#80BAF1',width=3)
    elif i==1:
        d.polygon([(22,18),(59,25),(59,71),(22,64)],fill='#B8CBDD',outline='#7696B5',width=2)
        d.polygon([(59,25),(69,18),(69,64),(59,71)],fill='#7F9DB8')
    else:
        d.rectangle((17,17,71,71),fill='#BDCFDF')
        d.rectangle((28,25,61,64),fill='#F4F7FB',outline='#E7A33A',width=4)
        d.ellipse((35,35,54,54),fill='#2378E1')
    im.save(root/('ReservationCardStep'+str(i)+'.png'))
print('Reservation card assets generated')
