"""Index a fixed, hash-verified HYCN42WW image; never access hardware.
Byte-pattern matches are candidates, not instruction/control-flow proofs.
"""
from pathlib import Path
from collections import Counter
import hashlib, json, re, struct

ROOT = Path(__file__).resolve().parents[1]
image = ROOT / 'artifacts/firmware/payload/WinHYCN42WW.fd'
data = image.read_bytes()
expected = 'd6fd2eb9cdbd49164d70eedf5a049eba8a4b8a522e63891d3a1e6c5e5b041056'
if hashlib.sha256(data).hexdigest() != expected:
    raise SystemExit('Unexpected firmware image SHA256; refusing fixed-offset extraction')
base, length = 0x9E4D90, 0x20000
block = data[base:base + length]
assert len(block) == length
assert block[:3] == bytes.fromhex('02 00 70')
assert block[0x50:0x5D] == b'ITE EC-V14.4\0'
assert block[0x8052:0x805A] == b'HYEC42WW'
fv = base + length
assert data[fv + 40:fv + 44] == b'_FVH'
header_length = struct.unpack_from('<H', data, fv + 48)[0]
assert header_length >= 56 and header_length % 2 == 0
assert sum(struct.unpack_from('<' + 'H' * (header_length // 2), data, fv)) & 0xFFFF == 0
out = ROOT / 'artifacts/firmware/payload/HYEC42WW-candidate.bin'
out.write_bytes(block)
patterns = []
for i in range(len(block) - 2):
    if block[i] == 0x90:
        address = int.from_bytes(block[i+1:i+3], 'big')
        patterns.append({'offset': f'0x{i:05X}', 'address': f'0x{address:04X}',
                         'following_bytes': block[i+3:i+11].hex(' ')})
counts = Counter(x['address'] for x in patterns)
known = ['0xC830','0xC831','0xC832','0xC833','0xC83C','0xC83D','0x1060']
result = {
    'source_image_sha256': expected,
    'source_offset': hex(base), 'candidate_length': length,
    'candidate_sha256': hashlib.sha256(block).hexdigest(),
    'boundary_evidence': 'Starts with LJMP 0x0070 pattern; ends at next checksum-valid FV header. Exact EC region layout remains unproven.',
    'scope': 'Raw 90 hi lo pattern search. Data can match; bank mapping and dynamic DPTR addresses are not resolved. No matches does not prove unsupported control.',
    'instruction_reference': 'https://www.keil.com/support/man/docs/is51/is51_opcodes.asp?bhcp=1',
    'ascii_strings': [{'offset': hex(m.start()), 'text': m.group().decode('ascii')}
                      for m in re.finditer(rb'[ -~]{8,}', block)
                      if any(s in m.group() for s in (b'ITE', b'ECVer', b'HYEC', b'Project', b'SOC:', b'Date:'))],
    'other_model_addresses_raw_match_counts': {a: counts[a] for a in known},
    'most_common_raw_dptr_patterns': counts.most_common(40),
    'raw_dptr_patterns': patterns,
}
report = ROOT / 'research/ec-firmware-index.json'
report.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
print(json.dumps({k: v for k, v in result.items() if k != 'raw_dptr_patterns'}, indent=2))
