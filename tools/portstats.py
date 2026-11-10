#!/usr/bin/env python3
# xc-osu! — measures how much upstream code the port keeps, and which upstream versions it runs on.
# usage: portstats.py <out.json>     (run by devpub.sh / aotpub.sh; shown on the launcher)
#
# For each upstream checkout: "upstream" = lines of shipped source files at the upstream commit (HEAD),
# "changed" = upstream lines deleted or rewritten by the port (git diff --numstat deletions),
# "added" = lines the port inserted into upstream files + lines of files the port created in that tree.
# Tests, benchmarks, templates and desktop/mobile platform heads are not shipped, so they are not counted.
import json, pathlib, re, subprocess, sys, datetime

ROOT = pathlib.Path(__file__).resolve().parent.parent

def git(repo, *args, inp=None):
    return subprocess.run(['git', '-C', str(repo), *args], input=inp, capture_output=True, check=True).stdout

SKIP = re.compile(r'(\.Tests?(\b|/)|/Tests?/|Benchmarks|Templates|templates/|osu\.Desktop|osu\.Android|osu\.iOS|SampleGame|VisualTests|/bin/|/obj/|\.Tests\.)', re.I)

TREES = [
    # name, repo, shipped path prefixes, extensions
    ('osu!lazer', ROOT / 'osu', ['osu.Game/', 'osu.Game.Rulesets.Osu/'], ('.cs',)),
    ('osu-framework', ROOT / 'osu-framework', ['osu.Framework/'], ('.cs',)),
    ('realm-core', ROOT / 'native/realm-dotnet/wrappers/realm-core', ['src/realm/'], ('.cpp', '.hpp', '.h', '.c')),
    ('realm-dotnet', ROOT / 'native/realm-dotnet', ['Realm/Realm/', 'wrappers/src/'], ('.cs', '.cpp', '.hpp', '.h')),
]

def shipped(path, prefixes, exts):
    return path.endswith(exts) and any(path.startswith(p) for p in prefixes) and not SKIP.search('/' + path)

def count_lines(data):
    return data.count(b'\n') + (1 if data and not data.endswith(b'\n') else 0)

def tree_stats(name, repo, prefixes, exts):
    files = [l.split(b'\t', 1)[1].decode() for l in git(repo, 'ls-tree', '-r', 'HEAD').splitlines() if b' blob ' in l]
    files = [f for f in files if shipped(f, prefixes, exts)]
    upstream = 0
    # one cat-file batch for all blobs at HEAD
    out = git(repo, 'cat-file', '--batch', inp=''.join(f'HEAD:{f}\n' for f in files).encode())
    i = 0
    while i < len(out):
        nl = out.index(b'\n', i)
        size = int(out[i:nl].split()[2])
        upstream += count_lines(out[nl + 1:nl + 1 + size])
        i = nl + 1 + size + 1
    changed = added = touched = 0
    for l in git(repo, '-c', 'core.autocrlf=false', 'diff', '--numstat', 'HEAD').decode().splitlines():
        a, d, f = l.split('\t', 2)
        if a == '-' or not shipped(f, prefixes, exts): continue
        added += int(a); changed += int(d); touched += 1
    created = 0
    for f in git(repo, 'ls-files', '--others', '--exclude-standard').decode().splitlines():
        if shipped(f, prefixes, exts):
            created += 1
            added += count_lines((repo / f).read_bytes())
    return {'name': name, 'files': len(files), 'upstream': upstream, 'changed': changed, 'added': added,
            'touched': touched, 'created': created}

def xc_only():
    # code written for the port that has no upstream counterpart (vendored third-party libraries excluded)
    total = 0
    globs = [('osu/osu.Web', ('.cs', '.js', '.c', '.html')), ('server/XcOsu.Server', ('.cs',)), ('native/xcbass', ('.c', '.h', '.cpp', '.sh')), ('tools', ('.py', '.sh', '.mjs'))]
    for d, exts in globs:
        for p in (ROOT / d).rglob('*'):
            if p.is_file() and p.suffix in exts and not re.search(r'/(bin|obj|node_modules|build|publish|third_party|Native/sqlite)/', str(p)) and p.stat().st_size < 2_000_000:
                total += count_lines(p.read_bytes())
    return total

def describe(repo):
    try: return git(repo, 'describe', '--tags', '--always').decode().strip()
    except Exception: return None

def main():
    out = pathlib.Path(sys.argv[1])
    trees = [tree_stats(*t) for t in TREES if (t[1] / '.git').exists()]
    up = sum(t['upstream'] for t in trees); ch = sum(t['changed'] for t in trees); ad = sum(t['added'] for t in trees)
    xc = xc_only()
    lazer = describe(ROOT / 'osu') or ''
    m = re.match(r'^(\d{4}\.\d+\.\d+)(?:-(\d+)-g([0-9a-f]+))?$', lazer)
    data = {
        'generated': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
        'lazer': {'release': m.group(1) if m else lazer, 'ahead': int(m.group(2)) if m and m.group(2) else 0,
                  'commit': git(ROOT / 'osu', 'rev-parse', 'HEAD').decode().strip()[:10],
                  'date': git(ROOT / 'osu', 'log', '-1', '--format=%cs').decode().strip()},
        'framework': describe(ROOT / 'osu-framework'),
        'realm': describe(ROOT / 'native/realm-dotnet'),
        'trees': trees,
        'upstreamLines': up, 'upstreamKept': up - ch, 'keptPct': round(100 * (up - ch) / up, 2) if up else 0,
        'portLines': ad + xc, 'portInUpstreamTrees': ad, 'portStandalone': xc,
        'upstreamShareOfBuild': round(100 * (up - ch) / (up - ch + ad + xc), 2) if up else 0,
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(data, indent=1))
    print(f"portstats: lazer {data['lazer']['release']}+{data['lazer']['ahead']} · upstream kept {data['keptPct']}% of {up} lines · port code {ad + xc} lines -> {out}")

main()
