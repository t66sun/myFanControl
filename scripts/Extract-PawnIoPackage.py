"""Offline extraction of pinned official PawnIO packages; does not execute installers.
Rename CAB file-table entries only so duplicate names do not overwrite architectures.
Compressed driver payload and extracted driver bytes are not patched.
"""
from pathlib import Path
import sys,struct,json,hashlib
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'.tools/driver-inspection'))
import pefile,zstandard
version=sys.argv[1]
hashes={'2.1.0':'a3a46226c5e2824f4cdd42be0eecbabfc672c86f7889710f5ab1e6ad385b47a0','2.0.1':'3a34b5df231f10f252c4b50d3c777534b2a2cec5e1df9466ab2dca401c068c44'}
p=ROOT/'artifacts/drivers'/('PawnIO-'+version)/'PawnIO_setup.exe'
b=p.read_bytes();assert hashlib.sha256(b).hexdigest()==hashes[version]
pe=pefile.PE(data=b);cabs=[]
for typ in pe.DIRECTORY_ENTRY_RESOURCE.entries:
 if typ.id!=10:continue
 for name in typ.directory.entries:
  for lang in name.directory.entries:
   data=pe.get_data(lang.data.struct.OffsetToData,lang.data.struct.Size)
   if data[:4]==bytes.fromhex('28 b5 2f fd'):
    data=zstandard.ZstdDecompressor().decompress(data,max_output_size=128*1024*1024)
   if data[:4]==b'MSCF':cabs.append(data)
assert len(cabs)==1
original=cabs[0];b=bytearray(original);off=struct.unpack_from('<I',b,16)[0];count=struct.unpack_from('<H',b,28)[0];entries=[]
for index in range(count):
 size,folderOffset,folder,date,time,attrs=struct.unpack_from('<IIHHHH',b,off)
 start=off+16;end=b.index(0,start);name=bytes(b[start:end]).decode('ascii')
 assert '/' not in name and '\\' not in name and ':' not in name and len(name)>=2
 replacement=f'{index:02d}'+name[2:];assert len(replacement)==end-start
 b[start:end]=replacement.encode('ascii')
 entries.append({'index':index,'name':name,'inspectionName':replacement,'size':size,'folder':folder,'folderOffset':folderOffset})
 off=end+1
(p.parent/'original-payload.cab').write_bytes(original)
(p.parent/'inspection-renamed.cab').write_bytes(b)
(p.parent/'cab-file-index.json').write_text(json.dumps(entries,indent=2)+'\n')
print(f'{version}: hash verified; {count} CAB entries; installers/drivers not executed.')
