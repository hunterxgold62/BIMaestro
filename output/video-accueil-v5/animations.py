from pathlib import Path
import subprocess
P=Path(__file__).resolve().parent
events=[]
def ts(t):
 h=int(t//3600);m=int(t//60)%60;s=t%60
 return f'{h}:{m:02}:{s:05.2f}'
def event(a,b,text,layer=1):events.append(f'Dialogue: {layer},{ts(a)},{ts(b)},Default,,0,0,0,,{text}')
def rounded(w,h,r=12):
 return f'm {r} 0 l {w-r} 0 b {w} 0 {w} 0 {w} {r} l {w} {h-r} b {w} {h} {w} {h} {w-r} {h} l {r} {h} b 0 {h} 0 {h} 0 {h-r} l 0 {r} b 0 0 0 0 {r} 0'
def frame(a,b,box,label,num):
 x,y,w,h=box
 # Animated reveal follows the control bounds; no opaque fill over the label.
 tags=rf'\an7\pos({x},{y})\p1\bord4\shad0\1a&HFF&\3c&H32ABEF&\3a&H00&\fad(100,180)\clip({x-8},{y-8},{x-7},{y+h+8})\t(0,400,\clip({x-8},{y-8},{x+w+8},{y+h+8}))'
 event(a,b,'{'+tags+'}'+rounded(w,h))
 lx=x;ly=max(110,y-47);lw=max(190,len(label)*15+60)
 event(a,b,'{'+rf'\an7\pos({lx},{ly})\p1\bord0\shad0\1c&H323E10&\fad(140,180)'+'}'+rounded(lw,36,8),2)
 event(a,b,'{'+rf'\an7\pos({lx+13},{ly+4})\fs23\b1\1c&HFFFFFF&\bord0\shad0\fad(140,180)'+'}'+str(num)+'  '+label,3)
def ripple(t,x,y):
 # Two short rings expand and disappear at the demonstrated click.
 for delay in [0,.12]:
  tags=rf'\an5\pos({x},{y})\p1\bord3\shad0\1a&HFF&\3c&H32ABEF&\fscx45\fscy45\t(0,600,\fscx190\fscy190)\fad(50,520)'
  circle='m 0 -20 b 11 -20 20 -11 20 0 b 20 11 11 20 0 20 b -11 20 -20 11 -20 0 b -20 -11 -11 -20 0 -20'
  event(t+delay,t+delay+.65,'{'+tags+'}'+circle,4)
frame(5.7,6.5,(1204,139,132,140),'Couleurs',1)
frame(6.5,7.03,(1110,284,227,75),'Ouvrir',1)
ripple(6.65,1220,323)
frame(7.85,10.35,(780,250,137,44),'Accueil Revit',2)
ripple(9.75,848,272)
frame(13.5,17.5,(254,692,992,87),'Choisir Bibliothèque',3)
ripple(16.25,282,736)
frame(17.55,18.82,(1304,752,350,54),'Appliquer',4)
ripple(18.35,1478,777)
# Discreet three-step progress indicator in the existing header.
for a,b,step in [(2.8,7.03,0),(7.45,10.6,1),(11.1,18.82,2)]:
 for i in range(3):
  c='H32ABEF' if i==step else 'H8BA698'
  event(a,b,'{'+rf'\an7\pos({1690+i*62},43)\p1\bord0\1c&{c}&\fad(120,120)'+'}'+rounded(46,5,2),2)
header='''[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Segoe UI,26,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
'''
(P/'animations.ass').write_text(header+'\n'.join(events),encoding='utf-8-sig')
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-i','../video-accueil-v4/BIMaestro-Accueil-Revit-prise-utilisateur.mp4','-vf','ass=animations.ass','-an','-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p','-movflags','+faststart','BIMaestro-Accueil-Revit-cadres-animes.mp4'],cwd=P,check=True)
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-i','BIMaestro-Accueil-Revit-cadres-animes.mp4','-vf','select=eq(n\\,360)+eq(n\\,535)+eq(n\\,900)+eq(n\\,1080),scale=960:-1,tile=2x2','-frames:v','1','verification.jpg'],cwd=P,check=True)
print('Variante animée exportée.')
