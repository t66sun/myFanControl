"""Offline validation/summary of fixed EC diagnostic bytes; never accesses hardware."""
import argparse, hashlib, json
from datetime import datetime
from pathlib import Path


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def summarize(source, mode_source=None):
    raw = source.read_bytes()
    doc = json.loads(raw)
    assert doc['SchemaVersion'] == 1 and doc['IsAdministrator'] is True
    assert doc['DeviceOpenAttempted'] is True and doc['PawnClientInitialized'] is True
    assert doc['ReadCompleted'] is True and doc['Errors'] == []
    assert doc['PhysicalRpmVerified'] is False and doc['ControlVerified'] is False
    assert doc['Plan']['Model'] == '21CX' and doc['Plan']['Bios'] == 'HYCN42WW'
    assert doc['Plan']['ModuleSha256'] == 'b3896a1cab0d808fca31fe2ebcae045d59dac690da87b17c858bb8da357eb45e'
    assert not doc['Plan']['FanPayloadWrites'] and not doc['Plan']['MappingChanges']
    assert len(doc['Samples']) == doc['Plan']['SampleCount'] and len(doc['Samples']) in (20, 60)
    boundaries = None
    mode_evidence = None
    if mode_source:
        mraw = mode_source.read_bytes()
        mode = json.loads(mraw)
        assert mode['Model'] == '21CX' and mode['Bios'] == 'HYCN42WW'
        assert mode['Passed'] and mode['TargetVerified'] and mode['OriginalRestored'] and mode['Errors'] == []
        events = {e['Name']: e for e in mode['Events']}
        before = int(events['BeforeState']['Raw'], 16)
        original = int(events['BeforeMmc']['Raw'], 16) >> 16
        target = 3 if original == 2 else 2
        expected = (before & 0xFFFF0FFF) | (target << 12)
        assert original in (2, 3) and before >> 16 == 0x801
        assert int(events['TargetState']['Raw'], 16) == expected
        assert int(events['TargetMmc']['Raw'], 16) >> 16 == target
        assert int(events['RestoredState']['Raw'], 16) == before
        assert int(events['RestoredMmc']['Raw'], 16) >> 16 == original
        boundaries = (instant(events['SetTargetResponse']['TimeUtc']), instant(events['RestoreResponse']['TimeUtc']))
        mode_evidence = dict(source=str(mode_source), sha256=hashlib.sha256(mraw).hexdigest(), original_mode=original,
                             target_mode=target, original_state_restored=True)
    rows = []
    previous_end = None
    long_gaps = []
    segment_id = 0
    for sample in doc['Samples']:
        assert sample['ChipId'] == 0x5571 and sample['Mapping1060'] == 0 and sample['FinalMapping1060'] == 0
        assert sample['SelectorsRestored'] and sample['ReadCompleted'] and sample['Errors'] == []
        began, ended = instant(sample['StartedUtc']), instant(sample['CompletedUtc'])
        assert began <= ended and (previous_end is None or began >= previous_end)
        if previous_end is not None and (began - previous_end).total_seconds() > 2:
            segment_id += 1
            long_gaps.append(dict(kind='between_samples', sequence=len(rows)+1, seconds=(began-previous_end).total_seconds()))
        if (ended-began).total_seconds() > 2:
            long_gaps.append(dict(kind='transaction', sequence=len(rows)+1, seconds=(ended-began).total_seconds()))
        previous_end = ended
        assert len(sample['Blocks']) == 2
        blocks = {}
        for b in sample['Blocks']:
            addr = b['StartAddressHex']
            assert addr in ('0x0800', '0x8800') and addr not in blocks
            first, second = bytes.fromhex(b['FirstHex']), bytes.fromhex(b['SecondHex'])
            assert len(first) == len(second) == 16
            agree = first == second
            assert b['RepeatedBytesAgree'] == agree
            words = [int.from_bytes(first[i:i+2], 'big') if agree else None for i in (0, 2)]
            assert b['Word01BigEndian'] == words[0] and b['Word23BigEndian'] == words[1]
            assert began <= instant(b['StartedUtc']) <= instant(b['CompletedUtc']) <= ended
            blocks[addr] = dict(agree=agree, words=words, first=first.hex().upper(), second=second.hex().upper())
        low = blocks['0x0800']
        data = bytes.fromhex(low['first'])
        phase = 'baseline'
        if boundaries:
            phase = 'before_target' if began < boundaries[0] else 'target_hold' if began < boundaries[1] else 'after_restore'
            if began < boundaries[0] < ended or began < boundaries[1] < ended:
                phase = 'transition'
        rows.append(dict(started_utc=sample['StartedUtc'], segment_id=segment_id, phase=phase, low_agree=low['agree'],
                         low_words=low['words'], low_targets=[data[4], data[5]], low_overrides=list(data[12:16]),
                         high_agree=blocks['0x8800']['agree']))
    phases = {}
    for phase in sorted({r['phase'] for r in rows}):
        group = [r for r in rows if r['phase'] == phase]
        stable = [r for r in group if r['low_agree']]
        phases[phase] = dict(sample_count=len(group), low_repeated_equal_count=len(stable),
            high_repeated_equal_count=sum(r['high_agree'] for r in group),
            channel_words=[sorted({r['low_words'][i] for r in stable}) for i in (0, 1)],
            target_bytes=[sorted({r['low_targets'][i] for r in stable}) for i in (0, 1)],
            matches_target_times_100_within_100=sum(all(abs(r['low_words'][i]-r['low_targets'][i]*100) <= 100 for i in (0, 1)) for r in stable))
    return dict(schema_version=1, source=str(source), source_sha256=hashlib.sha256(raw).hexdigest(),
                sample_count=len(rows), all_identity_mapping_restore_checks_passed=True, mode_evidence=mode_evidence,
                continuous_short_capture=(len(long_gaps)==0), long_gaps=long_gaps, segment_count=segment_id+1,
                phases=phases, physical_rpm_verified=False, arbitrary_speed_control_verified=False,
                assessment='Validated raw transport and observations only. Phase changes support feedback identification but do not verify arbitrary control.', rows=rows)


if __name__ == '__main__':
    p=argparse.ArgumentParser()
    p.add_argument('--input', type=Path, required=True)
    p.add_argument('--modes', type=Path)
    p.add_argument('--output', type=Path, required=True)
    a=p.parse_args()
    result=summarize(a.input, a.modes)
    a.output.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k!='rows'}, ensure_ascii=False, indent=2))
