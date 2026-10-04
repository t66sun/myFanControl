"""Offline 8051 candidate decoder; no device access or hardware commands.
Uses the official opcode metadata. Bank mapping is not modeled: listing only.
"""
import argparse, hashlib, json
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
EXPECTED = '73e7dce2a408fd8aae851450259f58e270a1482306e80523e0f45ac19c8a558c'

def decode(data, offset, pc):
    op = data[offset]
    row = OPCODES[f'{op:02X}']
    if not row[1]:
        return {'offset':offset,'pc':pc,'bytes':data[offset:offset+1].hex(' '),'length':1,'mnemonic':'reserved','operands':'','targets':[],'terminal':True}
    n = int(row[1]); raw = data[offset:offset+n]
    if len(raw) != n: raise ValueError('Truncated instruction')
    m, operands = row[2], row[3]; targets = []
    if 'addr16' in operands:
        target = int.from_bytes(raw[1:3], 'big'); targets.append(target)
        operands = operands.replace('addr16',f'0x{target:04X}')
    elif 'addr11' in operands:
        target = ((pc+2)&0xF800)|((op&0xE0)<<3)|raw[1]; targets.append(target)
        operands = operands.replace('addr11',f'0x{target:04X}')
    elif 'offset' in operands:
        delta = raw[-1] if raw[-1]<128 else raw[-1]-256
        target = (pc+n+delta)&65535; targets.append(target)
        operands = operands.replace('offset',f'0x{target:04X}')
    if op == 0x90: operands = f'DPTR, #0x{int.from_bytes(raw[1:3], "big"):04X}'
    # Other operands retain metadata labels. Raw bytes are authoritative.
    return {'offset':offset,'pc':pc,'bytes':raw.hex(' '),'length':n,'mnemonic':m,'operands':operands,'targets':targets,
            'terminal':m in ('RET','RETI','LJMP','AJMP','SJMP','JMP')}

OPCODES = {r[0]:r for r in json.loads((ROOT/'research/8051-opcodes.json').read_text())['opcodes']}
if __name__ == '__main__':
    p=argparse.ArgumentParser();p.add_argument('--offset',type=lambda x:int(x,0),required=True);p.add_argument('--length',type=lambda x:int(x,0),default=0x100)
    p.add_argument('--pc',type=lambda x:int(x,0));args=p.parse_args()
    data=(ROOT/'artifacts/firmware/payload/HYEC42WW-candidate.bin').read_bytes()
    if hashlib.sha256(data).hexdigest()!=EXPECTED:raise SystemExit('Unexpected EC candidate hash')
    if args.offset<0 or args.length<1 or args.offset+args.length>len(data):raise SystemExit('Invalid range')
    offset=args.offset;pc=args.pc if args.pc is not None else offset&65535
    print('Candidate linear listing; bank mapping and code/data boundaries unproven.')
    while offset<args.offset+args.length:
        i=decode(data,offset,pc)
        print(f'{offset:05X} PC={pc:04X} {i["bytes"]:9} {i["mnemonic"]:7} {i["operands"]}')
        offset+=i['length'];pc=(pc+i['length'])&65535
