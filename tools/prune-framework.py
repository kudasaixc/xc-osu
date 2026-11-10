#!/usr/bin/env python3
# xc-osu! — removes superseded fingerprinted files from a publish folder (xc softworks / kudasaixc)
# usage: prune-framework.py <wwwroot>
# Every publish adds name.<hash>.ext files to _framework without removing the previous ones. A file is deleted only when
# a newer sibling with the same name/extension exists AND nothing in the current boot files mentions it.
import pathlib, re, sys

root = pathlib.Path(sys.argv[1])
fw = root / '_framework'
index = (root / 'index.html').read_text()
refs = index
for m in set(re.findall(r'_framework/(dotnet(?:\.[a-z0-9]{10})?\.js)', index)):
    p = fw / m
    if p.exists(): refs += p.read_text(errors='ignore')

groups = {}
for f in fw.iterdir():
    m = re.match(r'^(.+)\.([a-z0-9]{10})(\.[A-Za-z.]+)$', f.name)
    if f.is_file() and m: groups.setdefault((m.group(1), m.group(3)), []).append(f)

removed = size = 0
for files in groups.values():
    if len(files) < 2: continue
    files.sort(key=lambda f: f.stat().st_mtime)
    for f in files[:-1]:
        if f.name in refs: continue
        size += f.stat().st_size; removed += 1
        f.unlink()
print(f'pruned {removed} stale files ({size / 1048576:.0f} MB) from {fw}')
