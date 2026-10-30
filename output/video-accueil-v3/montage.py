from pathlib import Path
import subprocess
P=Path(__file__).resolve().parent
exec((P.parent/'video-accueil-v2'/'montage.py').read_text(encoding='utf-8').split('# One fixed canvas')[0].replace('P=Path(__file__).resolve().parent','P=Path(__file__).resolve().parent'))
v2='../video-accueil-v2/capture.mkv'
clips=[]
def scene(name,src,start,dur,title,sub,zoom=False):
 banner(name,title,sub)
 if zoom:
  # Two seconds of context, then a 2.2 s smoothstep zoom toward the ribbon menu.
  q='min(max((on-60)/66,0),1)'
  z=f'1+1.26*({q})*({q})*(3-2*({q}))'
  base=f"crop=2534:1188:0:45,zoompan=z='{z}':x='iw-iw/zoom':y=0:d=1:s=1920x900:fps=30"
 else:
  crop='1600:900:465:140' if name in ['choix','appliquer'] else '2534:1188:0:45'
  base=f'crop={crop},scale=1920:900:force_original_aspect_ratio=decrease,pad=1920:900:(ow-iw)/2:(oh-ih)/2:color=0xf3f6f5,fps=30'
 vf=f'[0:v]{base},pad=1920:1080:0:90:color=0xf3f6f5,setsar=1,format=yuv420p[v];[v][1:v]overlay=0:0[out]'
 run(['-ss',str(start),'-i',src,'-i',name+'.png','-filter_complex',vf,'-map','[out]','-t',str(dur),'-an','-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p',name+'.mp4'])
 clips.append((name,dur))
scene('exemple',v2,58,3,'Personnalisez votre accueil Revit','Un accueil adapté à votre façon de travailler.')
scene('interface',v2,8,12,'01   Ouvrez les réglages','Dans le ruban BIMaestro : Couleurs → Accueil Revit',True)
scene('choix',v2,26,7,'02   Choisissez votre présentation','Sélectionnez Bibliothèque parmi les cinq présentations.')
scene('appliquer',v2,36,4.3,'03   Enregistrez votre choix','Cliquez sur « Enregistrer et afficher l’accueil ».')
scene('atelier','variantes.mkv',45,2.6,'À chacun son accueil','01 / 04   Atelier essentiel — des cartes pour aller à l’essentiel.')
scene('bureau','variantes.mkv',101,2.6,'À chacun son accueil','02 / 04   Bureau de projets — un registre organisé.')
scene('bibliotheque',v2,58,2.6,'À chacun son accueil','03 / 04   Bibliothèque — des aperçus pour reconnaître vos fichiers.')
scene('index','variantes.mkv',147,2.6,'À chacun son accueil','04 / 04   Index — une liste compacte et des colonnes alignées.')
args=[]
for name,dur in clips: args+=['-i',name+'.mp4']
graph=[];prev='0:v';total=clips[0][1]
for i in range(1,len(clips)):
 label=f'x{i}';graph.append(f'[{prev}][{i}:v]xfade=transition=fade:duration=0.4:offset={total-.4}[{label}]');prev=label;total+=clips[i][1]-.4
run([*args,'-filter_complex',';'.join(graph),'-map',f'[{prev}]','-an','-c:v','libx264','-preset','fast','-crf','19','-pix_fmt','yuv420p','-movflags','+faststart','BIMaestro-Accueil-Revit-v3.mp4'])
run(['-i','BIMaestro-Accueil-Revit-v3.mp4','-vf','fps=1/3,scale=480:-1,tile=4x3','-frames:v','1','verification.jpg'])
print(f'{total:.2f} secondes')
