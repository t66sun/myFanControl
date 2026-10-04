"""Extract fixed-offset candidate EC command tables after hash verification.
Not a bank-aware disassembler. No hardware access. Review raw context manually.
"""
from pathlib import Path
import hashlib, importlib.util, json
ROOT=Path(__file__).resolve().parents[1]
b=(ROOT/'artifacts/firmware/payload/HYEC42WW-candidate.bin').read_bytes()
expected='73e7dce2a408fd8aae851450259f58e270a1482306e80523e0f45ac19c8a558c'
if hashlib.sha256(b).hexdigest()!=expected:raise SystemExit('Unexpected EC candidate hash')
spec=importlib.util.spec_from_file_location('ec_decoder',ROOT/'scripts/Decode-ThinkBookEc.py')
d=importlib.util.module_from_spec(spec);spec.loader.exec_module(d)
def table(start):
    rows=[];offset=start
    for _ in range(100):
        target=int.from_bytes(b[offset:offset+2],'big')
        if target==0:
            return {'entries':rows,'default_target':hex(int.from_bytes(b[offset+2:offset+4],'big')),'end_offset':hex(offset+4)}
        rows.append({'offset':hex(offset),'selector':hex(b[offset+2]),'target':hex(target),'raw':b[offset:offset+3].hex(' ')})
        offset+=3
    raise ValueError('Missing terminator')
result={'scope':'Static candidate control flow, no hardware calls. Physical-to-code bank mapping remains unverified.',
        'sha256':expected,'sma2_command_table':table(0xd64a),'sma2_60_subcommands':table(0xd91a),'vpc_command_table':table(0x104d2)}
assert b[0x104cf:0x104d2]==bytes.fromhex('12 7d a8')
assert result['vpc_command_table']['default_target']=='0x858b'
assert b[0x1058b:0x10591]==bytes.fromhex('e4 90 83 b0 f0 22')
(ROOT/'research/ec-command-tables.json').write_text(json.dumps(result,indent=2)+'\n')
lines=[]
for offset,n,pc in ((0x7da8,0x26,0x7da8),(0xd63d,0xd,0xd63d),(0xda3c,0x73,0xda3c),(0x104c5,0xd,0x84c5),(0x1058b,6,0x858b)):
    lines.append(f'\nCandidate range {offset:#x}, PC assumption {pc:#x}')
    end=offset+n
    while offset<end:
        i=d.decode(b,offset,pc)
        lines.append(f'{offset:05X} PC={pc:04X} {i["bytes"]:9} {i["mnemonic"]:7} {i["operands"]}')
        offset+=i['length'];pc=(pc+i['length'])&65535
(ROOT/'research/ec-command-context.txt').write_text('\n'.join(lines)+'\n')
print('Indexed SMA2 and candidate VPC command tables; no hardware commands executed.')
