"""Index literal ACPI _WDG buffers without executing any AML or WMI methods."""
import hashlib, json, pathlib, re, uuid
root = pathlib.Path(__file__).resolve().parents[1]
records, buffers = [], []
for file in sorted((root/'artifacts/acpi').glob('*.dsl')):
    text = file.read_text(encoding='utf-8', errors='strict')
    clean = re.sub(r'/\*.*?\*/|//[^\r\n]*', lambda m: ''.join('\n' if c == '\n' else ' ' for c in m.group()), text, flags=re.S)
    for match in re.finditer(r'Name\s*\(\s*_WDG\s*,\s*Buffer\s*\(\s*(0x[0-9A-Fa-f]+|[0-9]+)\s*\)\s*\{([^}]*)\}', clean, re.S):
        expected = int(match.group(1),0)
        body = re.sub(r'/\*.*?\*/|//[^\n]*', '', match.group(2), flags=re.S)
        # These local buffers use only hexadecimal byte literals. Reject other forms.
        values = re.findall(r'0x[0-9A-Fa-f]+', body)
        remainder = re.sub(r'0x[0-9A-Fa-f]+|[\s,]', '', body)
        if remainder:
            raise ValueError(f'Unsupported initializer in {file.name}: {remainder}')
        raw = bytes(int(v,16) for v in values)
        if len(raw) != expected or len(raw)%20:
            raise ValueError(f'Invalid _WDG size in {file.name}: {len(raw)} vs {expected}')
        # Independently confirm the disassembled byte buffer exists in the original AML.
        dat = file.with_suffix('.dat').read_bytes()
        if raw not in dat:
            raise ValueError(f'_WDG initializer not found in original AML {file.name}')
        line = text.count('\n',0,match.start())+1
        buffers.append({'file':file.name,'line':line,'bytes':len(raw),'sha256':hashlib.sha256(raw).hexdigest()})
        for offset in range(0,len(raw),20):
            entry = raw[offset:offset+20]
            flags = entry[19]
            object_id = entry[16:18].decode('ascii',errors='replace') if not flags&8 else None
            method = ('WM' if flags&2 else 'WQ')+object_id if object_id else None
            locations = [{'file':other.name,'line':i+1,'text':s.strip()}
                for other in sorted((root/'artifacts/acpi').glob('*.dsl'))
                for i,s in enumerate(other.read_text(encoding='utf-8').splitlines())
                if method and re.search(r'(?:Method|Name)\s*\(\s*'+re.escape(method)+r'\b',s)]
            records.append({'file':file.name,'bufferLine':line,'offset':offset,
                'guid':str(uuid.UUID(bytes_le=entry[:16])).upper(), 'objectId':object_id,
                'notificationId':entry[16] if flags&8 else None,
                'instanceCount':entry[18],'flags':flags,'isMethod':bool(flags&2),'isEvent':bool(flags&8),
                'handler':method,'handlerCandidates':locations})
known = {'GameZone':'887B54E3-DDDC-4B2C-8B88-68A26A8835D0',
    '16pGen4UndocumentedFan':'777B54E3-DDDC-4B2C-8B88-68A26A8835D0',
    'OtherMethod':'DC2A8805-3A8C-41BA-A6F7-092E0089CD3B'}
result={'schemaVersion':1,'scope':'Literal _WDG buffers in the exported DSDT and 20 SSDTs; no WMI execution',
    'buffers':buffers,'entries':records,'knownGuidMatches':{name:[r for r in records if r['guid']==guid] for name,guid in known.items()}}
out=root/'research/acpi-wmi-index.json'
out.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(out)
print('Buffers:',len(buffers),'Entries:',len(records))
for r in records:
    print(r['file'],r['guid'],r['handler'] or 'event',r['flags'])
print('Known GUID matches:',{k:len(v) for k,v in result['knownGuidMatches'].items()})
