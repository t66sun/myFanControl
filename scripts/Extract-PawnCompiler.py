"""Extract the pinned compiler from the official NSIS archive without installing it."""
from pathlib import Path
import hashlib
import lzma
import struct
import sys

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / '.tools/driver-inspection'))
import pefile

archive = root / '.tools/pawn-compiler/pawn-4.1.7487.exe'
blob = archive.read_bytes()
assert hashlib.sha256(blob).hexdigest() == '106527794a995acd89881ca55985450af6ecde0376f525e7aa5f49314556cb40'
header = blob.index(b'\xef\xbe\xad\xdeNullsoftInst') - 4
flags, _, _, _, _, header_size, archive_size = struct.unpack_from('<7I', blob, header)
assert flags == 0 and header + archive_size == len(blob)
payload = blob[header + 28:]
prop = payload[0]
lc, remainder = prop % 9, prop // 9
lp, pb = remainder % 5, remainder // 5
dictionary = struct.unpack_from('<I', payload, 1)[0]
decoded = lzma.decompress(payload[5:], format=lzma.FORMAT_RAW, filters=[
    {'id': lzma.FILTER_LZMA1, 'lc': lc, 'lp': lp, 'pb': pb, 'dict_size': dictionary}
])
assert struct.unpack_from('<I', decoded)[0] == header_size
offset = 0
candidates = []
while offset < len(decoded):
    assert offset + 4 <= len(decoded)
    length = struct.unpack_from('<I', decoded, offset)[0]
    start = offset + 4
    assert start + length <= len(decoded)
    data = decoded[start:start + length]
    if data.startswith(b'MZ'):
        pe = pefile.PE(data=data)
        for group in getattr(pe, 'FileInfo', []):
            for entry in group:
                for table in getattr(entry, 'StringTable', []):
                    if table.entries.get(b'OriginalFilename') == b'pawncc.exe':
                        assert table.entries[b'FileVersion'] == b'4.1.7487'
                        candidates.append(data)
    offset = start + length
assert offset == len(decoded) and len(candidates) == 1
compiler = candidates[0]
assert hashlib.sha256(compiler).hexdigest() == '2136ef8776744e88c72bafc6e0247bce4f10bd2283d1a5a46b98d52a2da2d4a1'
target = root / '.tools/pawn-compiler/bin/pawncc.exe'
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes(compiler)
print(f'Extracted {target}; no installer executed')
