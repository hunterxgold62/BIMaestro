from pathlib import Path
import subprocess
P=Path(__file__).resolve().parent
IN='BIMaestro-Tutoriel-Proprietes.mp4'   # sortie de montage.py (zooms + encadrés)
OUT='BIMaestro-Tutoriel-Proprietes-dynamique.mp4'
# Segments de la vidéo d'origine : (début, fin, vitesse). Ce qui manque est coupé.
SEG=[(2.6,10.4,1),(12.4,15.8,1),(15.8,20.8,3),(20.8,31.0,1),(31.0,44.5,2.5),
     (44.5,58.0,1),(63.5,71.6,1)]
OFF=2.5
def m(t):
 o=OFF
 for a,b,s in SEG:
  if t<a: return o
  if t<=b: return o+(t-a)/s
  o+=(b-a)/s
 return o
TOTAL=m(1e9)
STEPS=[(3.4,'Ouvrir l\'onglet BIMaestro'),(7.2,'Cliquer sur Couleurs'),(12.9,'Onglet Propriétés'),
 (21.2,'Créer un groupe personnalisé'),(23.4,'Créer un groupe'),(27.2,'Nommer le bandeau'),
 (44.5,'Glisser un paramètre'),(50.8,'Choisir une ambiance'),(54.0,'Choisir ses couleurs'),
 (55.4,'Couleur du fond'),(63.8,'Enregistrer'),(67.5,'Résultat dans Revit')]
N=len(STEPS)
ev=[]
def ts(t):
 t=max(t,0);h=int(t//3600);mm=int(t//60)%60;s=t%60
 return f'{h}:{mm:02}:{s:05.2f}'
def E(a,b,txt,layer=1): ev.append(f'Dialogue: {layer},{ts(a)},{ts(b)},Default,,0,0,0,,{txt}')
def rr(w,h,r=12): return f'm {r} 0 l {w-r} 0 b {w} 0 {w} 0 {w} {r} l {w} {h-r} b {w} {h} {w} {h} {w-r} {h} l {r} {h} b 0 {h} 0 {h} 0 {h-r} l 0 {r} b 0 0 0 0 {r} 0'
# Titre d'ouverture
E(0,2.5,r'{\an7\pos(0,0)\p1\1c&H1A1A1A&\1a&H40&\fad(0,400)}'+rr(1920,1080,0),5)
E(0,2.5,r'{\an5\pos(960,500)\fs64\b1\1c&HFFFFFF&\fad(200,400)}Personnaliser la palette Propriétés',6)
E(0,2.5,r'{\an5\pos(960,580)\fs34\1c&H8BDDB4&\fad(300,400)}BIMaestro · '+f'{N} étapes · {round(TOTAL)} s',6)
# Indicateur d'étape + barre segmentée en bas
bw=(1920-80-(N-1)*6)/N
for i,(t,lab) in enumerate(STEPS):
 a=m(t); b=m(STEPS[i+1][0]) if i+1<N else TOTAL
 E(a,b,r'{\an7\pos(30,980)\p1\1c&H323E10&\1a&H10&\fad(150,0)}'+rr(len(lab)*17+190,52,12),5)
 E(a,b,r'{\an7\pos(48,988)\fs32\b1\1c&H32ABEF&\fad(150,0)}'+f'{i+1}/{N}'+r'{\1c&HFFFFFF&}   '+lab,6)
 for j in range(N):
  x=40+j*(bw+6); c='H32ABEF' if j<i else ('HFFFFFF' if j==i else 'H6A6A6A')
  E(a,b,r'{\an7\pos(%d,1052)\p1\1c&%s&\1a&H20&}'%(x,c)+rr(int(bw),8,4),5)
 E(a,b,r'{\an7\pos(%d,1052)\p1\1c&H32ABEF&\clip(%d,1050,%d,1062)\t(\clip(%d,1050,%d,1062))}'%(40+i*(bw+6),40+i*(bw+6),40+i*(bw+6),40+i*(bw+6),40+i*(bw+6)+bw)+rr(int(bw),8,4),6)
# Badge d'accélération
for a,b,s in SEG:
 if s>1: E(m(a),m(b),r'{\an9\pos(1890,30)\p1\1c&H1A1A1A&\1a&H30&\fad(100,100)}'+rr(150,52),5); E(m(a),m(b),r'{\an9\pos(1872,38)\fs32\b1\1c&HFFFFFF&\fad(100,100)}▶▶ x'+f'{s:g}'.replace('.',','),6)
# Écran de fin
E(TOTAL-2.2,TOTAL,r'{\an7\pos(0,0)\p1\1c&H1A1A1A&\1a&H40&\fad(400,0)}'+rr(1920,1080,0),7)
E(TOTAL-2.2,TOTAL,r'{\an5\pos(960,520)\fs60\b1\1c&H8BDDB4&\fad(500,0)}✓  Vos propriétés sont personnalisées',8)
hdr='[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nScaledBorderAndShadow: yes\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Segoe UI,30,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n'
(P/'interface.ass').write_text(hdr+'\n'.join(ev),encoding='utf-8-sig')
# Le titre s'affiche sur une image figée du début (2,6 s), puis la vidéo démarre.
parts=[f'[0:v]trim=start={a}:end={b},setpts=(PTS-STARTPTS)/{s}[s{i}]' for i,(a,b,s) in enumerate(SEG)]
parts[0]=parts[0].replace('[s0]','[s0x]')+';[s0x]split[s0][fz0];[fz0]trim=end_frame=1,loop=loop=149:size=1:start=0,setpts=N/60/TB[fz]'
fc=';'.join(parts)+';[fz]'+''.join(f'[s{i}]' for i in range(len(SEG)))+f'concat=n={len(SEG)+1}:v=1:a=0,fps=60,setpts=N/60/TB,ass=interface.ass[v]'
# Chapitres
meta=';FFMETADATA1\ntitle=BIMaestro - Personnaliser la palette Propriétés\n'
for i,(t,lab) in enumerate(STEPS):
 a=0 if i==0 else m(t); b=m(STEPS[i+1][0]) if i+1<N else TOTAL
 meta+=f'[CHAPTER]\nTIMEBASE=1/1000\nSTART={int(a*1000)}\nEND={int(b*1000)}\ntitle={i+1}. {lab}\n'
(P/'chapitres.txt').write_text(meta,encoding='utf-8')
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-i',IN,'-i','chapitres.txt','-filter_complex',fc,'-map','[v]','-map_metadata','1','-map_chapters','1','-c:v','libx264','-preset','medium','-crf','18','-pix_fmt','yuv420p','-movflags','+faststart',OUT],cwd=P,check=True)
print('durée',round(TOTAL,1))
