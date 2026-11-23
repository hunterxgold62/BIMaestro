"""Monte un tutoriel vidéo BIMaestro à partir d'une capture d'écran et d'un fichier de réglages JSON.

Usage : python tutoriel.py reglages.json [--basique] [--verif]
  --basique : seulement zooms + encadrés + ondes de clic, sans coupes ni habillage
  --verif   : produit aussi une planche de 9 images de contrôle (verification.jpg)

Les coordonnées du JSON sont relevées sur des images réduites à ref_width (1280 par défaut).
Tous les temps (encadrés, clics, zooms, segments, débuts d'étape) sont ceux de la vidéo D'ORIGINE :
le script recalcule lui-même les temps après coupes et accélérations.
Les fichiers produits sont écrits dans le dossier du JSON.
"""
import json
import subprocess
import sys
from pathlib import Path

cfg_path = Path(sys.argv[1]).resolve()
C = json.loads(cfg_path.read_text(encoding='utf-8'))
BASIQUE = '--basique' in sys.argv
D = cfg_path.parent
SRC = C['source']
OUTW, OUTH = C.get('output_size', [1920, 1080])
FPS = C.get('fps', 60)


def probe(src):
    o = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries',
                        'stream=width,height', '-of', 'csv=p=0', src],
                       capture_output=True, text=True, check=True)
    w, h = o.stdout.strip().split(',')[:2]
    return int(w), int(h)


SW, SH = probe(SRC)
K = SW / C.get('ref_width', 1280)


def ts(t):
    t = max(t, 0)
    return f'{int(t // 3600)}:{int(t // 60) % 60:02}:{t % 60:05.2f}'


def rr(w, h, r=12):
    """Rectangle arrondi en dessin ASS."""
    w, h, r = int(w), int(h), int(r)
    return (f'm {r} 0 l {w-r} 0 b {w} 0 {w} 0 {w} {r} l {w} {h-r} b {w} {h} {w} {h} {w-r} {h} '
            f'l {r} {h} b 0 {h} 0 {h} 0 {h-r} l 0 {r} b 0 0 0 0 {r} 0')


def ass_file(name, resx, resy, events):
    hdr = (f'[Script Info]\nScriptType: v4.00+\nPlayResX: {resx}\nPlayResY: {resy}\nScaledBorderAndShadow: yes\n\n'
           '[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, '
           'Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, '
           'Alignment, MarginL, MarginR, MarginV, Encoding\n'
           'Style: Default,Segoe UI,30,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1\n\n'
           '[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n')
    (D / name).write_text(hdr + '\n'.join(events), encoding='utf-8-sig')


ev = []


def E(a, b, txt, layer=1):
    ev.append(f'Dialogue: {layer},{ts(a)},{ts(b)},Default,,0,0,0,,{txt}')


# ---------- Passe 1 : encadrés + ondes de clic, dessinés dans les coordonnées de la source ----------
S = SW / 2560  # tailles de police et de trait réglées pour une source de 2560 px de large
steps = C['steps']
for n, st in enumerate(steps, 1):
    if 'box' in st:
        a, b = st['box_time']
        x, y, w, h = [v * K for v in st['box']]
        p = 6 * S
        x, y, w, h = int(x - p), int(y - p), int(w + 2 * p), int(h + 2 * p)
        E(a, b, '{' + rf'\an7\pos({x},{y})\p1\bord{6*S:.0f}\shad0\1a&HFF&\3c&H32ABEF&\3a&H00&\fad(100,200)'
          rf'\clip({x-10},{y-10},{x-9},{y+h+10})\t(0,400,\clip({x-10},{y-10},{x+w+10},{y+h+10}))' + '}'
          + rr(w, h, 14 * S))
        txt = f"{n}  {st.get('label', st['title'])}"
        fs = int(30 * S)
        lw, lh = len(txt) * fs * 0.57 + 40 * S, 46 * S
        ly = y + h + 10 * S if y < 240 * S else y - lh - 10 * S
        lx = min(x, SW - lw - 20 * S)
        E(a, b, '{' + rf'\an7\pos({lx:.0f},{ly:.0f})\p1\bord0\shad0\1c&H323E10&\fad(140,200)' + '}'
          + rr(lw, lh, 10 * S), 2)
        E(a, b, '{' + rf'\an7\pos({lx+18*S:.0f},{ly+7*S:.0f})\fs{fs}\b1\1c&HFFFFFF&\bord0\shad0\fad(140,200)'
          + '}' + txt, 3)
    if 'click' in st:
        cx, cy, t = st['click']
        cx, cy = cx * K, cy * K
        for d in (0, .12):
            E(t + d, t + d + .65,
              '{' + rf'\an5\pos({cx:.0f},{cy:.0f})\p1\bord{5*S:.0f}\shad0\1a&HFF&\3c&H32ABEF&'
              rf'\fscx{45*S:.0f}\fscy{45*S:.0f}\t(0,600,\fscx{200*S:.0f}\fscy{200*S:.0f})\fad(50,520)' + '}'
              + 'm 0 -24 b 13 -24 24 -13 24 0 b 24 13 13 24 0 24 b -13 24 -24 13 -24 0 b -24 -13 -13 -24 0 -24', 4)
ass_file('encadres.ass', SW, SH, ev)

# Zooms progressifs : [début, fin, centre x, centre y, facteur], fenêtres sans chevauchement
Z = C.get('zooms', [])
R = 0.7  # durée de la montée / descente du zoom


def ease(a, b):
    u = f'clip(min((it-{a})/{R},({b}-it)/{R}),0,1)'
    return f'({u}*{u}*(3-2*{u}))'


zexpr = '1' + ''.join(f'+{f-1}*{ease(a, b)}' for a, b, _, _, f in Z)


def pick(i):
    e = '0'
    for a, b, cx, cy, _ in reversed(Z):
        e = f'if(between(it,{a},{b}),{(cx, cy)[i] * K},{e})'
    return e


base = (f"[0:v]ass=encadres.ass,zoompan=z='{zexpr}'"
        f":x='clip({pick(0)}-iw/zoom/2,0,iw-iw/zoom)':y='clip({pick(1)}-ih/zoom/2,0,ih-ih/zoom)'"
        f":d=1:s={OUTW}x{OUTH}:fps={FPS}")
enc = ['-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p', '-movflags', '+faststart']
name = C.get('output', 'tutoriel')
out = f'{name}-basique.mp4' if BASIQUE else f'{name}.mp4'

if BASIQUE:
    subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', '-i', SRC,
                    '-filter_complex', base + '[v]', '-map', '[v]', '-an', *enc, out], cwd=D, check=True)
else:
    # ---------- Passe 2 : coupes, accélérations, titre, étapes, barre, chapitres ----------
    SEG = C['segments']          # [début, fin, vitesse] ; ce qui n'est pas listé est coupé
    INTRO = C.get('intro', 2.5)  # durée du titre sur image figée
    OUTRO = C.get('outro', 2.2)  # durée de l'écran de fin (superposé à la fin)

    def m(t):
        """Temps d'origine -> temps dans la vidéo montée."""
        o = INTRO
        for a, b, s in SEG:
            if t < a:
                return o
            if t <= b:
                return o + (t - a) / s
            o += (b - a) / s
        return o

    TOTAL = m(1e9)
    N = len(steps)
    ev = []
    sc = OUTW / 1920
    E(0, INTRO, r'{\an7\pos(0,0)\p1\1c&H1A1A1A&\1a&H40&\fad(0,400)}' + rr(OUTW, OUTH, 0), 5)
    E(0, INTRO, rf'{{\an5\pos({OUTW/2:.0f},{500*sc:.0f})\fs{64*sc:.0f}\b1\1c&HFFFFFF&\fad(200,400)}}' + C['title'], 6)
    E(0, INTRO, rf'{{\an5\pos({OUTW/2:.0f},{580*sc:.0f})\fs{34*sc:.0f}\1c&H8BDDB4&\fad(300,400)}}'
      + f"{C.get('subtitle', 'BIMaestro')} · {N} étapes · {round(TOTAL)} s", 6)
    gap = 6 * sc
    bw = (OUTW - 80 * sc - (N - 1) * gap) / N
    by = OUTH - 28 * sc
    starts = [st['start'] for st in steps]
    for i, st in enumerate(steps):
        a = m(starts[i])
        b = m(starts[i + 1]) if i + 1 < N else TOTAL
        lab = st['title']
        E(a, b, rf'{{\an7\pos({30*sc:.0f},{OUTH-100*sc:.0f})\p1\1c&H323E10&\1a&H10&\fad(150,0)}}'
          + rr((len(lab) * 17 + 190) * sc, 52 * sc), 5)
        E(a, b, rf'{{\an7\pos({48*sc:.0f},{OUTH-92*sc:.0f})\fs{32*sc:.0f}\b1\1c&H32ABEF&\fad(150,0)}}{i+1}/{N}'
          + r'{\1c&HFFFFFF&}   ' + lab, 6)
        for j in range(N):
            x = 40 * sc + j * (bw + gap)
            c = 'H32ABEF' if j < i else ('HFFFFFF' if j == i else 'H6A6A6A')
            E(a, b, rf'{{\an7\pos({x:.0f},{by:.0f})\p1\1c&{c}&\1a&H20&}}' + rr(bw, 8 * sc, 4), 5)
        x = 40 * sc + i * (bw + gap)
        E(a, b, rf'{{\an7\pos({x:.0f},{by:.0f})\p1\1c&H32ABEF&\clip({x:.0f},{by-2:.0f},{x:.0f},{by+12*sc:.0f})'
          rf'\t(\clip({x:.0f},{by-2:.0f},{x+bw:.0f},{by+12*sc:.0f}))}}' + rr(bw, 8 * sc, 4), 6)
    for a, b, s in SEG:
        if s > 1:
            E(m(a), m(b), rf'{{\an9\pos({OUTW-30*sc:.0f},{30*sc:.0f})\p1\1c&H1A1A1A&\1a&H30&\fad(100,100)}}'
              + rr(150 * sc, 52 * sc), 5)
            E(m(a), m(b), rf'{{\an9\pos({OUTW-48*sc:.0f},{38*sc:.0f})\fs{32*sc:.0f}\b1\1c&HFFFFFF&\fad(100,100)}}'
              + '▶▶ x' + f'{s:g}'.replace('.', ','), 6)
    if C.get('end_text'):
        E(TOTAL - OUTRO, TOTAL, r'{\an7\pos(0,0)\p1\1c&H1A1A1A&\1a&H40&\fad(400,0)}' + rr(OUTW, OUTH, 0), 7)
        E(TOTAL - OUTRO, TOTAL, rf'{{\an5\pos({OUTW/2:.0f},{OUTH/2:.0f})\fs{60*sc:.0f}\b1\1c&H8BDDB4&\fad(500,0)}}'
          + '✓  ' + C['end_text'], 8)
    ass_file('interface.ass', OUTW, OUTH, ev)

    meta = f";FFMETADATA1\ntitle={C['title']}\n"
    for i, st in enumerate(steps):
        a = 0 if i == 0 else m(starts[i])
        b = m(starts[i + 1]) if i + 1 < N else TOTAL
        meta += f"[CHAPTER]\nTIMEBASE=1/1000\nSTART={int(a*1000)}\nEND={int(b*1000)}\ntitle={i+1}. {st['title']}\n"
    (D / 'chapitres.txt').write_text(meta, encoding='utf-8')

    k = len(SEG)
    # Titre sur image figée : première image du premier segment répétée INTRO secondes.
    # Ne pas utiliser tpad (ignoré dans ce graphe) ; loop + setpts=N/FPS/TB est fiable.
    fc = base + f',split={k+1}' + ''.join(f'[p{i}]' for i in range(k + 1)) + ';'
    fc += (f'[p{k}]trim=start={SEG[0][0]},trim=end_frame=1,'
           f'loop=loop={int(INTRO*FPS)-1}:size=1:start=0,setpts=N/{FPS}/TB[fz];')
    fc += ';'.join(f'[p{i}]trim=start={a}:end={b},setpts=(PTS-STARTPTS)/{s}[s{i}]'
                   for i, (a, b, s) in enumerate(SEG))
    fc += (';[fz]' + ''.join(f'[s{i}]' for i in range(k))
           + f'concat=n={k+1}:v=1:a=0,fps={FPS},setpts=N/{FPS}/TB,ass=interface.ass[v]')
    subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', '-i', SRC, '-i', 'chapitres.txt',
                    '-filter_complex', fc, '-map', '[v]', '-map_metadata', '1', '-map_chapters', '1',
                    '-an', *enc, out], cwd=D, check=True)
    print(f'Durée montée : {TOTAL:.1f} s')
    print('Début des étapes (s) :', ', '.join(f'{i+1}={m(t):.1f}' for i, t in enumerate(starts)))

if '--verif' in sys.argv:
    dur = float(subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', out],
                               cwd=D, capture_output=True, text=True).stdout)
    sel = '+'.join(f'eq(n\\,{int(dur * FPS * (i + .5) / 9)})' for i in range(9))
    subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', '-i', out,
                    '-vf', f'select={sel},scale=640:-1,tile=3x3', '-frames:v', '1', 'verification.jpg'],
                   cwd=D, check=True)
print('OK ->', D / out)
