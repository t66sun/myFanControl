"""Inspect only two fixed, straight-line zeroing routines; never access hardware.
These routines are not host commands, and their callers remain unresolved.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
blob = (ROOT / 'artifacts/firmware/payload/HYEC42WW-candidate.bin').read_bytes()
expected = '73e7dce2a408fd8aae851450259f58e270a1482306e80523e0f45ac19c8a558c'
assert hashlib.sha256(blob).hexdigest() == expected


def inspect(start, end):
    pc = start
    accumulator = None
    dptr = None
    writes = []
    while pc < end:
        opcode = blob[pc]
        if opcode == 0xE4:  # CLR A
            accumulator = 0
            pc += 1
        elif opcode == 0x90:  # MOV DPTR, immediate
            dptr = int.from_bytes(blob[pc+1:pc+3], 'big')
            pc += 3
        elif opcode == 0xA3:  # INC DPTR
            assert dptr is not None
            dptr += 1
            pc += 1
        elif opcode == 0xF0:  # MOVX @DPTR, A
            assert dptr is not None and accumulator == 0
            writes.append(f'{dptr:04X}')
            pc += 1
        elif opcode == 0x22:  # RET
            assert pc+1 == end
            return {'offset': f'{start:05X}', 'endExclusive': f'{end:05X}',
                    'raw': blob[start:end].hex(' '), 'zeroWrites': writes,
                    'immediateCallPatternCandidates': [f'{i:05X}' for i in range(len(blob)-2)
                        if blob[i] in (0x02, 0x12) and blob[i+1:i+3] == start.to_bytes(2, 'big')]}
        else:
            raise AssertionError(f'Unexpected instruction {opcode:02X} at {pc:05X}')
    raise AssertionError('No return at the reviewed boundary')


first = inspect(0xC656, 0xC68E)
second = inspect(0xC68E, 0xC6C6)
assert len(first['zeroWrites']) == len(second['zeroWrites']) == 14
assert {'1804', '880C', '880E'} <= set(first['zeroWrites'])
assert {'1805', '880D', '880F'} <= set(second['zeroWrites'])
report = {'candidateSha256': expected, 'hardwareAccess': False,
          'bankMappingRuntimeVerified': False, 'hostInvocationKnown': False,
          'automaticControlRestorationProven': False,
          'routines': [first, second],
          'interpretation': 'These clear output, feedback, targets, RPM/PWM overrides and other fields. '
                            'Absence of immediate call patterns does not exclude indirect/banked callers. '
                            'Do not expose these as a host reset or automatic-control command.'}
(ROOT / 'research/ec-fan-reset-paths.json').write_text(json.dumps(report, indent=2)+'\n')
print('Two fixed zeroing routines inspected; 14 writes each; no host invocation established.')
