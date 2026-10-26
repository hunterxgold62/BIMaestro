"""Regenerate the three October 2026 tooltip animations (Pillow + ffmpeg)."""
from pathlib import Path
import argparse
import math
import shutil
import subprocess

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2] / 'BIMaestro/Content/ToolTipExamples'
BG, INK, BLUE, GREEN = '#F4F7FB', '#263548', '#2378E1', '#4F9862'
FONT = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 22)
SMALL = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 18)
BOLD = ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf', 24)


def base(title, footer):
    im = Image.new('RGB', (640, 360), BG)
    d = ImageDraw.Draw(im)
    d.text((24, 18), title, font=BOLD, fill=INK)
    d.text((24, 325), footer, font=SMALL, fill='#65758A')
    return im, d


def card(d, rect, fill='white'):
    d.rounded_rectangle(rect, radius=9, fill=fill, outline='#D7E2EE', width=2)


def smooth(value):
    v = max(0, min(1, value))
    return v * v * (3 - 2 * v)


def plan(d, x, y):
    d.rectangle((x, y, x + 81, y + 43), fill='#EAF1F8', outline='#92A8C0', width=2)
    d.line((x + 38, y, x + 38, y + 29), fill='#92A8C0', width=2)
    d.line((x, y + 22, x + 24, y + 22), fill=BLUE, width=3)


def home(t):
    im, d = base('Choisir son accueil Revit', 'Présentation → projets et familles → accès rapide')
    list_mode = 2.5 <= t < 5
    for i, label in enumerate(('Cartes', 'Liste')):
        x = 30 + i * 146
        chosen = list_mode == (i == 1)
        d.rounded_rectangle((x, 70, x + 132, 106), radius=6, fill=BLUE if chosen else 'white')
        d.text((x + 22, 75), label, font=SMALL, fill='white' if chosen else INK)
    card(d, (24, 118, 615, 308))
    d.text((40, 126), 'Projets récents', font=SMALL, fill=INK)
    if list_mode:
        for i, label in enumerate(('Bâtiment A.rvt', 'Réseaux MEP.rvt', 'Local technique.rvt')):
            y = 161 + i * 42
            d.rectangle((39, y, 599, y + 34), fill='#EAF1F8' if i == 1 else BG)
            d.text((53, y + 4), label, font=SMALL, fill=INK)
            d.text((421, y + 4), 'Projet Revit', font=SMALL, fill='#65758A')
    else:
        for i, label in enumerate(('Bâtiment A', 'Réseaux MEP', 'Local technique')):
            x = 39 + i * 188
            card(d, (x, 160, x + 173, 289), BG)
            plan(d, x + 45, 177)
            d.text((x + 12, 237), label, font=SMALL, fill=INK)
    return im


def properties(t):
    im, d = base('Organiser la palette Propriétés', 'Profil → couleurs et groupes → aperçu réversible')
    styled = 1.3 <= t < 4.8
    card(d, (24, 77, 258, 304))
    d.text((40, 89), 'Profil : Canalisations', font=SMALL, fill=INK)
    for i, (label, col) in enumerate((('Groupes', BLUE), ('Paramètres', '#8656BA'), ('Fond', '#E4EEF9'))):
        y = 134 + i * 43
        d.text((41, y), label, font=SMALL, fill=INK)
        d.rounded_rectangle((211, y, 237, y + 26), radius=4, fill=col)
    d.rounded_rectangle((41, 267, 240, 293), radius=5, fill=BLUE if styled else '#E4EEF9')
    d.text((53, 268), 'Aperçu' if styled else 'Retour initial', font=SMALL, fill='white' if styled else INK)
    card(d, (280, 77, 615, 304))
    d.text((298, 89), 'Propriétés', font=FONT, fill=INK)
    groups = [('Dimensions', [('Diamètre', '100 mm'), ('Longueur', '2 400 mm')]),
              ('Identification', [('Repère', 'CVC-01')])]
    y = 126
    for label, rows in groups:
        d.rectangle((293, y, 602, y + 29), fill=BLUE if styled else '#DEE5ED')
        d.text((303, y + 2), label, font=SMALL, fill='white' if styled else INK)
        y += 31
        for name, value in rows:
            d.rectangle((293, y, 602, y + 30), fill='#E4EEF9' if styled else 'white')
            d.text((303, y + 2), name, font=SMALL, fill='#7146A3' if styled else INK)
            d.text((460, y + 2), value, font=SMALL, fill=INK)
            y += 32
    return im


def maintenance(t):
    im, d = base('Préparer une intervention MEP', 'Repères → cotes → obstacles • exemple géométrique')
    card(d, (24, 78, 398, 306))
    card(d, (415, 78, 615, 306))
    d.text((40, 88), 'Extraction du moteur', font=SMALL, fill=INK)
    d.text((431, 88), 'Manœuvre', font=SMALL, fill=INK)
    # Plan with a clearance envelope along a bent extraction route.
    d.rectangle((43, 131, 134, 230), fill='#CFDCE9', outline='#8FA4BA', width=2)
    d.rectangle((224, 127, 290, 211), fill='#F3D8CE', outline='#C67758', width=2)
    d.text((222, 215), 'Obstacle', font=SMALL, fill='#AC6043')
    route = [(112, 263), (332, 263), (332, 151)]
    d.line(route, fill='#B5D4EF', width=3)
    p = smooth((t - .6) / 4.2) * 332
    x, y = (112 + p, 263) if p <= 220 else (332, 263 - (p - 220))
    d.rounded_rectangle((x - 34, y - 29, x + 34, y + 29), radius=9, outline=GREEN, width=2)
    d.rounded_rectangle((x - 24, y - 17, x + 24, y + 17), radius=5, fill=BLUE)
    for offset in (-12, -4, 4, 12):
        d.line((x + offset, y - 12, x + offset, y + 12), fill='#9BC4F2', width=2)
    d.ellipse((x - 7, y - 7, x + 7, y + 7), fill='white')
    # Operator and swept lever illustrate the other intervention mode.
    d.line((432, 235, 597, 235), fill='#AABDD0', width=10)
    d.polygon([(504, 224), (520, 235), (536, 224), (536, 246), (520, 235), (504, 246)], fill=BLUE)
    d.line((520, 234, 520, 201), fill=BLUE, width=4)
    a = math.radians(-120 + 90 * smooth((t - 1) / 3))
    tip = (520 + 44 * math.cos(a), 201 + 44 * math.sin(a))
    d.arc((476, 157, 564, 245), 240, 330, fill='#A9D5B2', width=5)
    d.line((520, 201, *tip), fill=GREEN, width=6)
    d.ellipse((445, 143, 467, 165), fill='#E8BC90')
    d.line((456, 166, 456, 210), fill='#E5A349', width=13)
    d.line((456, 177, *tip), fill='#E5A349', width=6)
    d.line((456, 210, 443, 258), fill=INK, width=6)
    d.line((456, 210, 474, 258), fill=INK, width=6)
    d.text((431, 278), 'Cotes réglables', font=SMALL, fill=INK)
    return im


SCENES = {'MaintenanceMep': maintenance, 'InfoHomeAppearance': home,
          'InfoPropertiesAppearance': properties}


def generate(name):
    ffmpeg = shutil.which('ffmpeg')
    if not ffmpeg:
        raise RuntimeError('ffmpeg must be on PATH')
    ROOT.mkdir(parents=True, exist_ok=True)
    command = [ffmpeg, '-hide_banner', '-loglevel', 'error', '-y', '-f', 'rawvideo',
               '-pixel_format', 'rgb24', '-video_size', '640x360', '-framerate', '20',
               '-i', '-', '-an', '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
               '-movflags', '+faststart', str(ROOT / (name + '.mp4'))]
    with subprocess.Popen(command, stdin=subprocess.PIPE) as process:
        for frame in range(120):
            im = SCENES[name](frame / 20)
            if frame == 70:
                im.save(ROOT / (name + 'Poster.png'))
            process.stdin.write(im.tobytes())
        process.stdin.close()
        if process.wait() != 0:
            raise RuntimeError('Video encoding failed: ' + name)
    print(name)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('names', nargs='*', choices=list(SCENES))
    for name in parser.parse_args().names or SCENES:
        generate(name)
