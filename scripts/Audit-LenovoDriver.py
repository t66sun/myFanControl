"""Read-only disassembly index for the installed Lenovo ACPI driver; never loads it."""
import hashlib, json, pathlib, sys
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / '.tools/python-packages'))
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM
root = pathlib.Path(__file__).resolve().parents[1]
source = pathlib.Path(r'C:\Windows\System32\drivers\AcpiVpc.sys')
data = source.read_bytes()
pe = pefile.PE(data=data)
md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
base = pe.OPTIONAL_HEADER.ImageBase
functions = [(e.struct.BeginAddress, e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION]
records = []
for start, end in functions:
    insns = list(md.disasm(pe.get_data(start, end-start), base+start))
    matches = []
    for i, ins in enumerate(insns):
        for op in ins.operands:
            if op.type != X86_OP_IMM:
                continue
            value = op.imm & 0xFFFFFFFF
            raw = value.to_bytes(4, 'little')
            ioctl = (value >> 16) == 0x8310
            name = all(65 <= c <= 90 or c == 95 or 48 <= c <= 57 for c in raw)
            if not (ioctl or name):
                continue
            matches.append({'kind': 'ioctl' if ioctl else 'acpi-name-candidate',
                'value': f'0x{value:08X}', 'ascii': raw.decode('ascii') if name else None,
                'rva': f'0x{ins.address-base:X}',
                'context': [f'{x.address-base:05X}: {x.mnemonic} {x.op_str}' for x in insns[max(0,i-3):i+7]]})
    if matches:
        records.append({'functionBeginRva': f'0x{start:X}', 'functionEndRva': f'0x{end:X}', 'matches': matches})
out = root / 'artifacts/diagnostics/lenovo-driver-index.json'
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps({'source': str(source), 'sha256': hashlib.sha256(data).hexdigest(),
    'size': len(data), 'note': 'Instruction constants are candidates; confirm data/control flow before calling.',
    'functions': records}, indent=2), encoding='utf-8')
print(out)
for r in records:
    print(r['functionBeginRva'], ', '.join(m['value']+((' '+m['ascii']) if m['ascii'] else '') for m in r['matches']))
