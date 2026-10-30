from pathlib import Path
import subprocess
P=Path(__file__).resolve().parent
SRC=r'C:/Users/plemert/Videos/2026-10-30 09-33-17.mp4'
OUT='BIMaestro-Tutoriel-Proprietes.mp4'
K=2  # repères relevés en 1280x720, source en 2560x1440
events=[]
def ts(t):
 h=int(t//3600);m=int(t//60)%60;s=t%60
 return f'{h}:{m:02}:{s:05.2f}'
def ev(a,b,text,layer=1):events.append(f'Dialogue: {layer},{ts(a)},{ts(b)},Default,,0,0,0,,{text}')
def rounded(w,h,r=14):
 return f'm {r} 0 l {w-r} 0 b {w} 0 {w} 0 {w} {r} l {w} {h-r} b {w} {h} {w} {h} {w-r} {h} l {r} {h} b 0 {h} 0 {h} 0 {h-r} l 0 {r} b 0 0 0 0 {r} 0'
def frame(a,b,box,label,num):
 x,y,w,h=[v*K for v in box]; x-=6;y-=6;w+=12;h+=12
 tags=rf'\an7\pos({x},{y})\p1\bord6\shad0\1a&HFF&\3c&H32ABEF&\3a&H00&\fad(100,200)\clip({x-10},{y-10},{x-9},{y+h+10})\t(0,400,\clip({x-10},{y-10},{x+w+10},{y+h+10}))'
 ev(a,b,'{'+tags+'}'+rounded(w,h))
 txt=f'{num}  {label}'; lw=len(txt)*17+40; lh=46
 ly=y+h+10 if y<240 else y-lh-10
 lx=min(x,2560-lw-20)
 ev(a,b,'{'+rf'\an7\pos({lx},{ly})\p1\bord0\shad0\1c&H323E10&\fad(140,200)'+'}'+rounded(lw,lh,10),2)
 ev(a,b,'{'+rf'\an7\pos({lx+18},{ly+7})\fs30\b1\1c&HFFFFFF&\bord0\shad0\fad(140,200)'+'}'+txt,3)
def ripple(t,x,y):
 x*=K;y*=K
 for d in [0,.12]:
  tags=rf'\an5\pos({x},{y})\p1\bord5\shad0\1a&HFF&\3c&H32ABEF&\fscx45\fscy45\t(0,600,\fscx200\fscy200)\fad(50,520)'
  ev(t+d,t+d+.65,'{'+tags+'}'+'m 0 -24 b 13 -24 24 -13 24 0 b 24 13 13 24 0 24 b -13 24 -24 13 -24 0 b -24 -13 -13 -24 0 -24',4)
frame(3.4,4.9,(592,15,44,17),'Onglet BIMaestro',1); ripple(4.3,614,23)
frame(7.2,9.8,(1008,34,36,36),'Couleurs',2); ripple(9.6,1025,50)
frame(12.9,14.6,(403,148,44,18),'Propriétés',3); ripple(13.6,424,157)
frame(21.2,22.6,(278,339,128,17),'Créer un groupe personnalisé',4); ripple(22.1,284,347)
frame(23.4,25.6,(396,358,88,20),'Créer un groupe',5); ripple(25.0,440,368)
frame(27.2,28.8,(790,398,171,22),'Nommer le bandeau',6)
frame(46.0,50.6,(513,445,228,18),'Glisser le paramètre ici',7); ripple(50.3,560,454)
frame(50.8,53.2,(790,511,87,23),'Ambiance en un clic',8); ripple(52.2,835,522)
frame(54.0,55.2,(790,486,130,16),'Choisir mes propres couleurs',9); ripple(54.2,797,494)
frame(55.4,57.6,(790,464,171,20),'Couleur du fond',10)
frame(63.8,65.8,(880,614,107,23),'Enregistrer mes réglages',11); ripple(65.4,933,625)
frame(67.6,71.6,(1055,181,215,24),'Résultat dans Propriétés',12)
header='''[Script Info]
ScriptType: v4.00+
PlayResX: 2560
PlayResY: 1440
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Segoe UI,30,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
'''
(P/'animations.ass').write_text(header+'\n'.join(events),encoding='utf-8-sig')
# Zooms : (début, fin, centre x, centre y en 1280x720, facteur)
Z=[(3.0,5.6,614,40,2.4),(6.8,10.0,980,110,2.0),(12.6,15.0,430,170,2.2),(20.8,25.8,400,360,2.0),
   (26.5,30.8,860,400,2.0),(44.5,53.5,680,470,1.6),(54.0,58.0,860,520,1.6),(63.5,66.0,900,600,1.8),(67.5,72.2,1160,215,2.4)]
R=0.7
def ease(a,b):
 u=f'clip(min((it-{a})/{R},({b}-it)/{R}),0,1)'
 return f'({u}*{u}*(3-2*{u}))'
zexpr='1'+''.join(f'+{f-1}*{ease(a,b)}' for a,b,_,_,f in Z)
def pick(i):
 e='0'
 for a,b,cx,cy,_ in reversed(Z): e=f'if(between(it,{a},{b}),{(cx,cy)[i]*K},{e})'
 return e
xexpr=f'clip({pick(0)}-iw/zoom/2,0,iw-iw/zoom)'
yexpr=f'clip({pick(1)}-ih/zoom/2,0,ih-ih/zoom)'
vf=f"ass=animations.ass,zoompan=z='{zexpr}':x='{xexpr}':y='{yexpr}':d=1:s=1920x1080:fps=60"
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-i',SRC,'-vf',vf,'-an','-c:v','libx264','-preset','medium','-crf','18','-pix_fmt','yuv420p','-movflags','+faststart',OUT],cwd=P,check=True)
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-i',OUT,'-vf','select=eq(n\,250)+eq(n\,560)+eq(n\,820)+eq(n\,1460)+eq(n\,1700)+eq(n\,2900)+eq(n\,3420)+eq(n\,3920)+eq(n\,4300),scale=640:-1,tile=3x3','-frames:v','1','verification.jpg'],cwd=P,check=True)
print('ok')
