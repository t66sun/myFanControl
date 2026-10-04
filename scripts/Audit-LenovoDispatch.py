"""Static IOCTL dispatch tracing; this never opens a device or executes driver code."""
import hashlib,json,pathlib,sys
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'.tools/python-packages'))
import pefile
from capstone import Cs,CS_ARCH_X86,CS_MODE_64
from capstone.x86 import X86_OP_IMM
root=pathlib.Path(__file__).resolve().parents[1]
source=pathlib.Path(r'C:\Windows\System32\drivers\AcpiVpc.sys')
data=source.read_bytes(); pe=pefile.PE(data=data)
base=pe.OPTIONAL_HEADER.ImageBase
md=Cs(CS_ARCH_X86,CS_MODE_64);md.detail=True
functions={entry.struct.BeginAddress:list(md.disasm(pe.get_data(entry.struct.BeginAddress,entry.struct.EndAddress-entry.struct.BeginAddress),base+entry.struct.BeginAddress)) for entry in pe.DIRECTORY_ENTRY_EXCEPTION}
methods={}
for start,insns in functions.items():
    names=[]
    for ins in insns:
        if ins.mnemonic!='mov': continue
        for op in ins.operands:
            if op.type!=X86_OP_IMM: continue
            raw=(op.imm&0xffffffff).to_bytes(4,'little')
            if all(65<=v<=90 or 48<=v<=57 or v==95 for v in raw): names.append(raw.decode('ascii'))
    if names:methods[start]=sorted(set(names))
insns=functions[0x10DC0]
by_address={ins.address:ins for ins in insns}
entries=[]
# These register comparisons were manually checked in this exact driver image.
register_comparisons={0x10EB2:0x83102114,0x10EC5:0x831020E4,0x11565:0x8310213C,0x11597:0x83102124}
for i,ins in enumerate(insns[:-1]):
    if ins.mnemonic!='cmp':continue
    operands=[op.imm&0xffffffff for op in ins.operands if op.type==X86_OP_IMM]
    if not operands and ins.address-base in register_comparisons:
        operands=[register_comparisons[ins.address-base]]
    if not operands or operands[0]>>16!=0x8310:continue
    branch=insns[i+1]
    # Equality falls through unsigned greater/less branches, then takes JE.
    if branch.mnemonic in ('ja','jb','jg','jl'):
        branch=insns[i+2]
    if branch.mnemonic not in ('je','jne') or branch.operands[0].type!=X86_OP_IMM:
        entries.append({'ioctl':f'0x{operands[0]:08X}','unresolved':True});continue
    start=branch.operands[0].imm if branch.mnemonic=='je' else branch.address+branch.size
    address=start;trace=[];calls=[];visited=set()
    # Follow the success fall-through of buffer-size guards. Preserve every instruction
    # so the primary agent can check this assumption against the actual branch target.
    for _ in range(70):
        if address not in by_address or address in visited:break
        visited.add(address);current=by_address[address]
        trace.append(f'{address-base:05X}: {current.mnemonic} {current.op_str}')
        if current.mnemonic=='call' and current.operands[0].type==X86_OP_IMM:
            rva=current.operands[0].imm-base
            calls.append({'rva':f'0x{rva:X}','methodNameCandidates':methods.get(rva,[])})
            if rva in methods:break
        if current.mnemonic=='ret':break
        if current.mnemonic=='jmp':
            if current.operands[0].type!=X86_OP_IMM:break
            address=current.operands[0].imm
        else:address+=current.size
    entries.append({'ioctl':f'0x{operands[0]:08X}','comparisonRva':f'0x{ins.address-base:X}',
        'equalityPathRva':f'0x{start-base:X}','calls':calls,'trace':trace})
result={'source':str(source),'sha256':hashlib.sha256(data).hexdigest(),
    'assumptions':'Static equality branch plus fall-through of length guards; instruction trace requires human review. No commands executed.',
    'coverage':'All 39 IOCTL constants found in the dispatch routine. Internal selector branches are not symbolically evaluated; traces are candidates.',
    'entries':entries}
out=root/'research/lenovo-ioctl-dispatch.json';out.write_text(json.dumps(result,indent=2),encoding='utf-8')
print(out)
for e in entries:print(e['ioctl'],e.get('calls',[]))
