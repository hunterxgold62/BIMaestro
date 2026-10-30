from pathlib import Path
import subprocess
P=Path(__file__).resolve().parent
exec((P.parent/'video-accueil-v2'/'montage.py').read_text(encoding='utf-8').split('# One fixed canvas')[0])
SRC='C:/Users/plemert/Videos/2026-10-28 14-13-18.mp4'
clips=[]
def scene(name,start,dur,title,sub,crop=None,zoom=False,still=False):
 banner(name,title,sub)
 args=['-ss',str(start),'-i',SRC]
 if still:
  run([*args,'-frames:v','1',name+'-source.png']);args=['-loop','1','-framerate','60','-i',name+'-source.png']
 if zoom:
  q='min(max((on-75)/120,0),1)';z=f'1+1.1*({q})*({q})*(3-2*({q}))'
  base=f"crop=2560:1200:0:35,zoompan=z='{z}':x='iw-iw/zoom':y=0:d=1:s=1920x900:fps=60"
 else:
  base=f'crop={crop or "2560:1200:0:35"},scale=1920:900:force_original_aspect_ratio=decrease,pad=1920:900:(ow-iw)/2:(oh-ih)/2:color=0xf3f6f5,fps=60'
 vf=f'[0:v]{base},pad=1920:1080:0:90:color=0xf3f6f5,setsar=1,format=yuv420p[v];[v][1:v]overlay=0:0[out]'
 run([*args,'-i',name+'.png','-filter_complex',vf,'-map','[out]','-t',str(dur),'-an','-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p',name+'.mp4']);clips.append((name,dur))
scene('exemple',20,2.8,'Personnalisez votre accueil Revit','Un aperçu du résultat, avant de commencer.')
scene('interface',0,5,'01   Ouvrez les réglages','Dans le ruban BIMaestro, ouvrez Couleurs.',zoom=True)
scene('onglet',7,4,'02   Accédez à Accueil Revit','Dans la fenêtre Apparence BIMaestro, ouvrez l’onglet Accueil Revit.',crop='1600:900:470:145')
scene('choix',11,8.5,'03   Choisissez et appliquez','Choisissez Bibliothèque, puis « Enregistrer et afficher l’accueil ». ',crop='1600:900:470:145')
scene('bibliotheque',20,3.2,'Quelques résultats de votre démonstration','Bibliothèque — vos fichiers présentés en cartes.')
scene('autodesk',39.7,2.8,'Quelques résultats de votre démonstration','Accueil Autodesk — affichage en cartes.',still=True)
scene('mixte',43,4,'Quelques résultats de votre démonstration','Accueil Autodesk — fichiers épinglés et fichiers récents.')
args=[]
for name,dur in clips:args+=['-i',name+'.mp4']
graph=[];prev='0:v';total=clips[0][1]
for i in range(1,len(clips)):
 label=f'x{i}';graph.append(f'[{prev}][{i}:v]xfade=transition=fade:duration=0.35:offset={total-.35}[{label}]');prev=label;total+=clips[i][1]-.35
run([*args,'-filter_complex',';'.join(graph),'-map',f'[{prev}]','-an','-c:v','libx264','-preset','fast','-crf','19','-pix_fmt','yuv420p','-movflags','+faststart','BIMaestro-Accueil-Revit-prise-utilisateur.mp4'])
run(['-i','BIMaestro-Accueil-Revit-prise-utilisateur.mp4','-vf','fps=1/3,scale=480:-1,tile=4x3','-frames:v','1','verification.jpg'])
print(f'{total:.2f} secondes — 60 images/s')
