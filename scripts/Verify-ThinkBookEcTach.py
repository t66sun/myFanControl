"""Execute only the candidate tach conversion routines in isolated Python memory.
No hardware access. This is a limited 8051 interpreter, not a complete emulator.
Unsupported opcodes/control flow abort. Positive divisor paths only.
"""
from pathlib import Path
import hashlib, json, random
ROOT=Path(__file__).resolve().parents[1]
IMAGE=ROOT/'artifacts/firmware/payload/HYEC42WW-candidate.bin'
EXPECTED='73e7dce2a408fd8aae851450259f58e270a1482306e80523e0f45ac19c8a558c'
class Machine:
    def __init__(self, code, entry, external):
        self.code=code;self.pc=entry;self.mem=bytearray(256);self.x=dict(external);self.stack=[];self.steps=0
    def bit(self,b):
        address=(0x20+b//8) if b<128 else b&0xf8
        return (self.mem[address]>>(b&7))&1
    def setbit(self,b,value):
        address=(0x20+b//8) if b<128 else b&0xf8
        self.mem[address]=(self.mem[address]&~(1<<(b&7)))|((value&1)<<(b&7))
    @property
    def a(self):return self.mem[0xe0]
    @a.setter
    def a(self,v):self.mem[0xe0]=v&255
    @property
    def c(self):return self.bit(0xd7)
    @c.setter
    def c(self,v):self.setbit(0xd7,v)
    @property
    def dptr(self):return self.mem[0x83]*256+self.mem[0x82]
    def jump(self,delta):self.pc=(self.pc+(delta if delta<128 else delta-256))&65535
    def run(self):
        while self.steps<10000:
            if not any(lo<=self.pc<hi for lo,hi in ((0xc46e,0xc4e2),(0xcb45,0xcb67),(0x7be5,0x7c77),(0x7dec,0x7e30))):
                raise ValueError(f'Unexpected code address {self.pc:04x}')
            op=self.code[self.pc];self.pc+=1;self.steps+=1
            def byte():
                v=self.code[self.pc];self.pc+=1;return v
            if op==0x90:self.mem[0x83]=byte();self.mem[0x82]=byte()
            elif op==0xe0:
                if self.dptr not in self.x:raise ValueError(f'Uninitialized XDATA read {self.dptr:04x}')
                self.a=self.x[self.dptr]
            elif op==0xf0:self.x[self.dptr]=self.a
            elif op==0xa3:
                v=(self.dptr+1)&65535;self.mem[0x83]=v>>8;self.mem[0x82]=v&255
            elif 0xe8<=op<=0xef:self.a=self.mem[op-0xe8]
            elif 0xf8<=op<=0xff:self.mem[op-0xf8]=self.a
            elif 0x78<=op<=0x7f:self.mem[op-0x78]=byte()
            elif 0xa8<=op<=0xaf:self.mem[op-0xa8]=self.mem[byte()]
            elif 0x88<=op<=0x8f:self.mem[byte()]=self.mem[op-0x88]
            elif op==0xe5:self.a=self.mem[byte()]
            elif op==0xf5:self.mem[byte()]=self.a
            elif op==0x75:address=byte();self.mem[address]=byte()
            elif op==0x74:self.a=byte()
            elif op==0xe4:self.a=0
            elif op==0xc3:self.c=0
            elif op==0xd3:self.c=1
            elif op in (0xc2,0xd2,0xb2):
                bit=byte();self.setbit(bit,0 if op==0xc2 else 1 if op==0xd2 else 1-self.bit(bit))
            elif 0xc8<=op<=0xcf:
                r=op-0xc8;v=self.a;self.a=self.mem[r];self.mem[r]=v
            elif 0x28<=op<=0x2f or 0x38<=op<=0x3f:
                adc=op>=0x38;r=op-(0x38 if adc else 0x28);v=self.a+self.mem[r]+(self.c if adc else 0);self.a=v;self.c=int(v>255)
            elif op==0x94 or 0x98<=op<=0x9f:
                rhs=byte() if op==0x94 else self.mem[op-0x98];v=self.a-rhs-self.c;self.a=v;self.c=int(v<0)
            elif op==0x33:v=self.a*2+self.c;self.a=v;self.c=int(v>255)
            elif op==0x84:
                if self.mem[0xf0]==0:raise ValueError('DIV by zero')
                q,r=divmod(self.a,self.mem[0xf0]);self.a=q;self.mem[0xf0]=r;self.c=0
            elif 0x08<=op<=0x0f:self.mem[op-0x08]=(self.mem[op-0x08]+1)&255
            elif op==0x04:self.a+=1
            elif op==0x12:
                target=byte()*256+byte();self.stack.append(self.pc);self.pc=target
            elif op==0x02:self.pc=byte()*256+byte()
            elif op==0x22:
                if not self.stack:return self.x,self.steps
                self.pc=self.stack.pop()
            elif op in (0x60,0x70,0x40,0x50,0x80):
                delta=byte();take={0x60:self.a==0,0x70:self.a!=0,0x40:self.c==1,0x50:self.c==0,0x80:True}[op]
                if take:self.jump(delta)
            elif op in (0x10,0x20,0x30):
                bit=byte();delta=byte();value=self.bit(bit)
                if (value==1 if op in (0x10,0x20) else value==0):
                    if op==0x10:self.setbit(bit,0)
                    self.jump(delta)
            elif 0xb8<=op<=0xbf:
                lhs=self.mem[op-0xb8];rhs=byte();delta=byte();self.c=int(lhs<rhs)
                if lhs!=rhs:self.jump(delta)
            elif 0xd8<=op<=0xdf:
                r=op-0xd8;delta=byte();self.mem[r]=(self.mem[r]-1)&255
                if self.mem[r]:self.jump(delta)
            elif op==0xd5:
                address=byte();delta=byte();self.mem[address]=(self.mem[address]-1)&255
                if self.mem[address]:self.jump(delta)
            else:raise ValueError(f'Unsupported opcode {op:02x} at {self.pc-1:04x}')
        raise ValueError('Instruction budget exceeded')
if __name__=='__main__':
    code=IMAGE.read_bytes()
    if hashlib.sha256(code).hexdigest()!=EXPECTED:raise SystemExit('Unexpected candidate hash')
    # Cases cover zero, byte-boundary divisors, clamp boundary and the full 16-bit domain.
    rng=random.Random(21)
    counts=sorted(set([0,1,2,255,256,257,287,288,289,300,500,600,1000,65534,65535]+[rng.randrange(1,65536) for _ in range(500)]))
    cases=[]
    for raw in counts:
        expected=0 if raw==0 else min((0x20e6da//raw)&65535,7500)
        for channel,entry,hi,lo,out in ((1,0xc46e,0x181f,0x181e,0x8800),(2,0xc4a8,0x1821,0x1820,0x8802)):
            external,steps=Machine(code,entry,{hi:raw>>8,lo:raw&255}).run()
            actual=external[out]*256+external[out+1]
            if actual!=expected:raise ValueError(f'Conversion mismatch channel={channel} raw={raw}: {actual} != {expected}')
            cases.append({'channel':channel,'raw':raw,'output':actual,'steps':steps})
    result={'scope':'Isolated offline execution of candidate firmware routines; no runtime EC/RPM validation.',
            'image_sha256':EXPECTED,'candidate_formula':'raw==0 ? 0 : min((2156250 // raw) & 0xFFFF, 7500)',
            'byte_order':'XDATA 8800/8802 high byte, 8801/8803 low byte','case_count':len(cases),'passed':True,'cases':cases}
    (ROOT/'research/ec-tach-offline-verification.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({k:v for k,v in result.items() if k!='cases'},indent=2))
