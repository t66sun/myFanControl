import hashlib,json,re,struct
from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=root/'research/upstream/LibreHardwareMonitor/LibreHardwareMonitorLib/Resources/PawnIo/LpcIO.bin'
b=p.read_bytes()
assert hashlib.sha256(b).hexdigest()=='b3896a1cab0d808fca31fe2ebcae045d59dac690da87b17c858bb8da357eb45e'
siglen=struct.unpack_from('<I',b)[0]
a=b[4+siglen:]
size,magic,fv,av,flags,defs,*v=struct.unpack_from('<IHBBHH12I',a)
cod,dat,hea,stp,cip,publics,natives,libraries,pubvars,tags,nametable,overlays=v
assert size==len(a) and magic==0xf1e1 and fv==11 and defs==8
assert 60<=publics<=natives<=libraries<=pubvars<=tags<=cod<=dat<=hea<=size
assert (dat-cod)%8==0 and not flags&0x19
src=(root/'research/upstream/PawnPP/amx.h').read_text()
block=src.split('OP_NOP = 0,',1)[1].split('OP_NUM_OPCODES',1)[0]
names=['OP_NOP']+re.findall(r'\bOP_[A-Z_]+\b',block)
assert len(names)==75
loader=(root/'research/upstream/PawnIO/PawnIO/src/amx_loader.h').read_text()
counts=list(map(int,re.findall(r'\d+',loader.split('operand_counts[]{',1)[1].split('};',1)[0])))
assert len(counts)==74
code=list(struct.unpack('<'+'q'*((dat-cod)//8),a[cod:dat]))
def table(start,end):
    r={}
    for off in range(start,end,defs):
        addr,no=struct.unpack_from('<II',a,off)
        assert no<size
        endname=a.index(0,no)
        r[a[no:endname].decode('ascii')]=addr
    return r
pub=table(publics,natives);nat=table(natives,libraries)
rows=[];i=0
while i<len(code):
    op=code[i]; assert 0<=op<len(names),(i,op)
    n=2+2*code[i+1] if op==74 else counts[op]
    assert n>=0 and i+1+n<=len(code)
    rows.append((i*8,names[op],code[i+1:i+1+n]))
    i+=1+n
assert i==len(code)
lines=[f'module sha256 {hashlib.sha256(b).hexdigest()}',f'signature bytes {siglen}; AMX payload {len(a)}; cell bits 64',f'publics {json.dumps(pub)}',f'natives {json.dumps(nat)}']
labels={addr:name for name,addr in pub.items()}
for off,name,args in rows:
    if off in labels: lines.append('\nPUBLIC '+labels[off])
    lines.append(f'{off:04X}: {name:16} '+', '.join(f'{x} ({x & ((1<<64)-1):X})' for x in args))
(root/'research/lpcio-pinned-disassembly.txt').write_text('\n'.join(lines)+'\n')
report={'moduleSha256':hashlib.sha256(b).hexdigest(),'signatureLength':siglen,'signatureCryptographicallyVerifiedByThisScript':False,'cellBits':64,'fileVersion':fv,'flags':flags,'instructionCount':len(rows),'publics':pub,'natives':nat,'hardwareAccess':False,'portWhitelistConclusion':'pending control-flow review'}
(root/'research/lpcio-pinned-binary-index.json').write_text(json.dumps(report,indent=2)+'\n')
# A deliberately limited, fail-closed interpreter; never calls a native or hardware API.
byaddr={off:(name,args) for off,name,args in rows}
mask=(1<<64)-1

def run(entry,args,selected=0x4e,bars=()):
    mem={off:val for off,val in enumerate(struct.unpack('<'+'q'*((hea-dat)//8),a[dat:hea]))}
    mem={off*8:val&mask for off,val in mem.items()}
    mem[0]=selected;mem[1032]=len(bars)
    for ix,bar in enumerate(bars):mem[8+ix*8]=bar
    stack=0x100000;frm=0;pri=alt=0;pc=entry;trace=[]
    def write(addr,val):
        assert addr%8==0 and 0<=addr<=0x100000
        mem[addr]=val&mask
    def read(addr):
        assert addr%8==0 and addr in mem,hex(addr)
        return mem[addr]
    def push(val):
        nonlocal stack
        stack-=8;write(stack,val)
    def pop():
        nonlocal stack
        val=read(stack);stack+=8;return val
    def signed(val):return val-(1<<64) if val&(1<<63) else val
    for val in reversed(args):push(val)
    push(len(args)*8);push(mask)
    # Input/out pointers are pure mock RAM. No device object is present.
    write(0x80000,run.port);write(0x80008,0)
    for step in range(1000):
        if pc==mask:return {'return':pri&0xffffffff,'steps':step,'nativeReached':False,'trace':trace}
        assert pc in byaddr,hex(pc)
        off=pc;name,operands=byaddr[pc];trace.append(f'{pc:04X}')
        pc+=8*(1+len(operands));operand=operands[0] if operands else 0
        if name in ('OP_BREAK','OP_NOP'):pass
        elif name=='OP_PROC':push(frm);frm=stack
        elif name=='OP_RETN':
            frm=pop();pc=pop();stack+=read(stack)+8
        elif name=='OP_CALL':push(pc);pc=off+operand
        elif name=='OP_JUMP':pc=off+operand
        elif name=='OP_JZER':
            if pri==0:pc=off+operand
        elif name=='OP_JNZ':
            if pri!=0:pc=off+operand
        elif name=='OP_CONST_PRI':pri=operand&mask
        elif name=='OP_CONST_ALT':alt=operand&mask
        elif name=='OP_LOAD_PRI':pri=read(operand)
        elif name=='OP_LOAD_ALT':alt=read(operand)
        elif name=='OP_LOAD_S_PRI':pri=read(frm+operand)
        elif name=='OP_LOAD_S_ALT':alt=read(frm+operand)
        elif name=='OP_ADDR_PRI':pri=frm+operand
        elif name=='OP_STOR_S':write(frm+operand,pri)
        elif name=='OP_STOR':write(operand,pri)
        elif name=='OP_LOAD_I':pri=read(pri)
        elif name=='OP_INC_I':write(pri,read(pri)+1)
        elif name=='OP_PUSH_PRI':push(pri)
        elif name=='OP_POP_ALT':alt=pop()
        elif name=='OP_STACK':stack+=operand
        elif name=='OP_EQ':pri=int(pri==alt)
        elif name=='OP_NEQ':pri=int(pri!=alt)
        elif name=='OP_SLESS':pri=int(signed(pri)<signed(alt))
        elif name=='OP_SLEQ':pri=int(signed(pri)<=signed(alt))
        elif name=='OP_SGRTR':pri=int(signed(pri)>signed(alt))
        elif name=='OP_SGEQ':pri=int(signed(pri)>=signed(alt))
        elif name=='OP_ADD':pri=(pri+alt)&mask
        elif name=='OP_AND':pri&=alt
        elif name=='OP_SHL_C_PRI':pri=(pri<<operand)&mask
        elif name=='OP_BOUNDS':assert pri<=operand
        elif name=='OP_SYSREQ':
            return {'return':None,'steps':step,'nativeReached':True,'nativeIndex':operand,'trace':trace}
        else:raise AssertionError('Unsupported '+name)
    raise AssertionError('Step budget exceeded')
run.port=0
cases=[]
for port in (0x68,0x6c,0x62,0x66,0x4e,0x4f,0x25c,0x25d,0xcf8,0xcff,0x300):
    run.port=port
    result=run(pub['ioctl_pio_inb'],[0x80000,1,0x80008,1])
    expected=port in (0x4e,0x4f,0x25c,0x25d)
    assert result['nativeReached']==expected,(port,result)
    if not expected:assert result['return']==0xc0000022
    cases.append({'port':f'{port:04X}',**result})
run.port=0x6c
assert run(pub['ioctl_pio_inb'],[0x80000,1,0x80008,1],selected=0)['return']==0xc00000a3
run.port=0x300
assert run(pub['ioctl_pio_inb'],[0x80000,1,0x80008,1],bars=(0x300,))['nativeReached']
for lowbar in (0x62,0x66,0x68,0x6c,0xff):
    rejected=run(0x530,[lowbar,lowbar])
    assert rejected['return']==0 and not rejected['nativeReached']
report['lowBarAdditionsRejectedOffline']=['0062','0066','0068','006C','00FF']
report.update({'portWhitelistConclusion':'Pinned bytecode returns STATUS_ACCESS_DENIED for 68/6C with slot 1 selected and zero discovered BARs, before any native I/O call. Offline model, not hardware result.','limitedInterpreter':True,'unsupportedInstructionPolicy':'abort','barDiscoveryCalled':False,'pioInputCases':cases,'notReadyAndMockBarCasesPassed':True})
(root/'research/lpcio-pinned-binary-index.json').write_text(json.dumps(report,indent=2)+'\n')
print('Offline port checks passed; 68/6C denied before native I/O, selected slot 1 and zero BARs.')
