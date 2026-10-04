"""Static PMC2 candidate tables; no hardware access. Bank mapping remains inferred."""
from pathlib import Path
import hashlib, importlib.util, json
ROOT=Path(__file__).resolve().parents[1]
b=(ROOT/'artifacts/firmware/payload/HYEC42WW-candidate.bin').read_bytes()
EXPECTED='73e7dce2a408fd8aae851450259f58e270a1482306e80523e0f45ac19c8a558c'
assert hashlib.sha256(b).hexdigest()==EXPECTED
spec=importlib.util.spec_from_file_location('decoder',ROOT/'scripts/Decode-ThinkBookEc.py')
d=importlib.util.module_from_spec(spec);spec.loader.exec_module(d)
def table(start):
 rows=[];o=start
 for _ in range(100):
  target=int.from_bytes(b[o:o+2],'big')
  if target==0:return {'offset':hex(start),'entries':rows,'default':hex(int.from_bytes(b[o+2:o+4],'big'))}
  rows.append({'offset':hex(o),'command':hex(b[o+2]),'target':hex(target),'raw':b[o:o+3].hex(' ')})
  o+=3
 raise ValueError('Missing terminator')
assert b[0x11186:0x11189]==b[0x111fb:0x111fe]==bytes.fromhex('12 7d a8')
r={'sha256':EXPECTED,'scope':'Static only: host ports, runtime bank mapping and command reachability unverified',
   'data_phase':table(0x11189),'command_phase':table(0x111fe),'d4_subcommands':table(0x10bfb)}
assert next(x for x in r['data_phase']['entries'] if x['command']=='0xd7')['target']=='0x91e4'
assert b[0x111e4:0x111e7]==bytes.fromhex('02 8e 8e')
assert next(x for x in r['data_phase']['entries'] if x['command']=='0xc7')['target']=='0x91cf'
assert b[0x111cf:0x111d2]==bytes.fromhex('02 89 43')
assert next(x for x in r['command_phase']['entries'] if x['command']=='0xc7')['target']=='0x927a'
assert b[0x1127a:0x1127d]==bytes.fromhex('02 88 88')
assert b[0x10888:0x1088c]==bytes.fromhex('75 3b 01 22')
assert b[0x10bf8:0x10bfb]==bytes.fromhex('12 7d a8')
assert next(x for x in r['d4_subcommands']['entries'] if x['command']=='0x10')['target']=='0x8c65'
(ROOT/'research/ec-pmc2-command-candidate.json').write_text(json.dumps(r,indent=2)+'\n')
lines=[]
# Explicit code ranges only: both dispatch tables are deliberately excluded.
for start,end,pc in [(0x10888,0x1088c,0x8888),(0x108f6,0x1093b,0x88f6),(0x10943,0x1094f,0x8943),(0x10e37,0x10e9a,0x8e37),(0x111c3,0x111fa,0x91c3),(0x11256,0x112ea,0x9256),(0x107a7,0x10805,0x87a7),(0x10c65,0x10c75,0x8c65)]:
 o=start
 while o<end:
  i=d.decode(b,o,pc)
  if o+i['length']>end:break
  lines.append(f"{o:05X} PC={pc:04X} {i['bytes']:9} {i['mnemonic']:7} {i['operands']}")
  o+=i['length'];pc=(pc+i['length'])&65535
(ROOT/'research/ec-pmc2-command-context.txt').write_text('Candidate code ranges; raw bytes authoritative; no table bytes decoded as instructions.\n'+'\n'.join(lines)+'\n')
print('PMC2 command/data tables, D4 subcommand table and fixed code ranges indexed offline.')
