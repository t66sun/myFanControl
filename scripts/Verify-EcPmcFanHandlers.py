"""Execute fixed candidate handler bytes in mock 8051 RAM; never open a device.
This proves only the enumerated local firmware paths under the inferred bank mapping.
"""
from pathlib import Path
import hashlib,json
ROOT=Path(__file__).resolve().parents[1]
b=(ROOT/'artifacts/firmware/payload/HYEC42WW-candidate.bin').read_bytes()
HASH='73e7dce2a408fd8aae851450259f58e270a1482306e80523e0f45ac19c8a558c'
assert hashlib.sha256(b).hexdigest()==HASH
index=json.loads((ROOT/'research/ec-pmc2-command-candidate.json').read_text())
def entry(phase,cmd):
 return int(next(x['target'] for x in index[phase]['entries'] if int(x['command'],16)==cmd),16)
# Allow only reviewed instructions in explicit local handler ranges.
ranges=[(0x8888,0x888c),(0x8919,0x891d),(0x8943,0x894f),(0x8e37,0x8e64),(0x8e8e,0x8e9a),(0x91cf,0x91d2),(0x91de,0x91e1),(0x91e4,0x91e7),(0x927a,0x927d),(0x928f,0x9292)]
def execute(pc,iram,xram):
 a=0;dptr=0;writes=[];trace=[]
 for step in range(64):
  assert any(lo<=pc<hi for lo,hi in ranges),hex(pc)
  off=pc+0x8000;op=b[off];trace.append(f'{pc:04X}')
  def imm(n=1):return b[off+n]
  if op==0x22:return {'xram':xram,'iram':iram,'writes':writes,'trace':trace}
  elif op==0x02:pc=int.from_bytes(b[off+1:off+3],'big');continue
  elif op==0x75:iram[imm()]=imm(2);pc+=3
  elif op==0xe5:a=iram[imm()];pc+=2
  elif op==0xb4:
   delta=imm(2) if imm(2)<128 else imm(2)-256
   pc+=3+(delta if a!=imm() else 0)
  elif op==0x90:dptr=int.from_bytes(b[off+1:off+3],'big');pc+=3
  elif op==0xe0:a=xram[dptr];pc+=1
  elif op==0xf0:xram[dptr]=a;writes.append([f'{dptr:04X}',a]);pc+=1
  elif op==0xe4:a=0;pc+=1
  elif op==0x24:a=(a+imm())&255;pc+=2
  elif op==0x14:a=(a-1)&255;pc+=1
  elif op==0x54:a&=imm();pc+=2
  elif op==0x44:a|=imm();pc+=2
  elif op in (0x60,0x70):
   delta=imm() if imm()<128 else imm()-256
   take=(a==0) if op==0x60 else (a!=0)
   pc+=2+(delta if take else 0)
  else:raise AssertionError(f'Unimplemented opcode {op:02X} at {pc:04X}')
 raise AssertionError('Exceeded bounded step budget')
base={0x880c:37,0x880d:43,0x880e:17,0x880f:19,0x8138:0xad,0x9000:0x55}
checks=0;samples=[]
for cmd,address in [(0xc7,0x880d),(0xd7,0x880c)]:
 init=execute(entry('command_phase',cmd),{0x3b:0,0x3c:0},base.copy())
 assert init['iram'][0x3b]==1 and init['writes']==[]
 for count in (0,1,2,255):
  for payload in range(256):
   got=execute(entry('data_phase',cmd),{0x3b:count,0x3c:payload},base.copy())
   expected=base.copy()
   if count==1:expected[address]=payload
   assert got['xram']==expected
   assert got['writes']==([[f'{address:04X}',payload]] if count==1 else [])
   checks+=1
   if count==1 and payload in (0,34,75,255):samples.append({'command':f'{cmd:02X}','payload':payload,'trace':got['trace'],'writes':got['writes']})
for state in range(256):
 ram=base.copy();ram[0x8138]=state
 got=execute(entry('data_phase',0xd5),{0x3b:1,0x3c:0x10},ram.copy())
 expected=ram.copy();expected[0x8138]=state&0xf7;expected[0x880c]=expected[0x880d]=0
 assert got['xram']==expected
 assert got['writes']==[['8138',state&0xf7],['880C',0],['880D',0]]
 checks+=1
report={'candidateSha256':HASH,'performedBy':'root','hardwareAccess':False,'bankMappingRuntimeVerified':False,'handlerCasesPassed':checks,'commandInitializersChecked':2,'fan1Command':'D7','fan2Command':'C7','payloadUnit':'Handler stores one raw byte; downstream target derivation remains separate','payloadBoundsCheckedByTheseHandlers':False,'clearCommand':'D5/10 clears bit3 and both byte overrides; leaves PWM overrides and other flags untouched','fullAutomaticRestorationProven':False,'samples':samples}
(ROOT/'research/ec-pmc2-dual-fan-offline-checks.json').write_text(json.dumps(report,indent=2)+'\n')
print(f'{checks} firmware handler cases and 2 initializers passed offline; no hardware access.')
