"""Extract existing family thumbnails, leaving the five export exercises empty."""
from pathlib import Path
from io import BytesIO
import struct
import zlib
from PIL import Image

root = Path(__file__).resolve().parents[1] / 'Demo' / 'NavigateurFamilles'
signature = bytes.fromhex('89504e470d0a1a0a')
count = 0
for family in (root / 'Familles').rglob('*.rfa'):
    relative = family.relative_to(root / 'Familles')
    if 'Salle de réunion' in relative.parts:
        continue
    data = family.read_bytes()
    start = data.find(signature)
    if start < 0:
        raise ValueError(f'No embedded PNG: {family}')
    end = start + len(signature)
    while True:
        size = struct.unpack_from('>I', data, end)[0]
        kind = data[end + 4:end + 8]
        chunk_end = end + 12 + size
        if chunk_end > len(data):
            raise ValueError(f'Invalid PNG chunk: {family}')
        expected = struct.unpack_from('>I', data, end + 8 + size)[0]
        if zlib.crc32(data[end + 4:end + 8 + size]) & 0xffffffff != expected:
            raise ValueError(f'Invalid PNG CRC: {family}')
        end = chunk_end
        if kind == b'IEND':
            break
    png = data[start:end]
    image = Image.open(BytesIO(png))
    image.load()
    target = (root / 'Images' / relative).with_suffix('.png')
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(png)
    count += 1
print(f'{count} verified PNGs extracted; Salle de réunion left empty.')
