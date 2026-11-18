#!/usr/bin/env python3
# xc-osu!: compare two tools/perfbench.mjs result files (median over runs, per phase). xc softworks (kudasaixc)
# usage: python3 tools/perfbench-compare.py base.json candidate.json
import json, statistics, sys

def load(path):
    d = json.load(open(path))
    return d['label'], [r['summary'] for r in d['results'] if r['summary'].get('done')]

def med(xs):
    xs = [x for x in xs if isinstance(x, (int, float))]
    return statistics.median(xs) if xs else None

METRICS = [
    ('update ms/frame', lambda p: p['update']['msPerFrame']),
    ('update Hz', lambda p: p['update']['hz']),
    ('update busy %', lambda p: p['update']['busyPct']),
    ('update max ms', lambda p: p['update']['maxMs']),
    ('slow updates', lambda p: p['spikes']['update']),
    ('draw ms/frame', lambda p: p['draw']['msPerFrame']),
    ('audio busy %', lambda p: p['audio']['busyPct']),
    ('GC minor', lambda p: p['gc']['minor']),
    ('GC major', lambda p: p['gc']['major']),
    ('GC pause ms', lambda p: p['gc']['pauseMs']),
    ('alloc MB/s', lambda p: p['gc']['allocMBs']),
    ('managed heap max MB', lambda p: p['gc']['heapMax']),
    ('wasm memory max MB', lambda p: p['mem']['wasm']),
    ('PSS max MB', lambda p: p['mem']['pssMax']),
    ('pack reads on game threads', lambda p: p['res']['fg']),
]

(la, a), (lb, b) = load(sys.argv[1]), load(sys.argv[2])
print(f'{la}: {len(a)} complete runs · {lb}: {len(b)} complete runs (medians)\n')
for k in ['menu', 'attached', 'runtime-created']:
    print(f"boot {k:16} {med([s['boot'].get(k) for s in a])!s:>8} -> {med([s['boot'].get(k) for s in b])!s:>8} s")
print(f"PSS max (whole run)   {med([s['pssMax'] for s in a])!s:>8} -> {med([s['pssMax'] for s in b])!s:>8} MB\n")
phases = [p['phase'] for p in (a or b)[0]['phases']]
for ph in phases:
    if ph in ('import', 'results'): continue
    pa = [p for s in a for p in s['phases'] if p['phase'] == ph and p['windows']]
    pb = [p for s in b for p in s['phases'] if p['phase'] == ph and p['windows']]
    if not pa or not pb: continue
    print(f'== {ph}')
    for name, f in METRICS:
        va, vb = med([f(p) for p in pa]), med([f(p) for p in pb])
        if va is None and vb is None: continue
        d = '' if not va or vb is None else f'  ({(vb - va) / va * 100:+.0f} %)'
        print(f'   {name:28} {va!s:>9} -> {vb!s:>9}{d}')
