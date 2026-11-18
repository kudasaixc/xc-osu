#!/usr/bin/env python3
# xc-osu!: put a build's engine .wasm files and resource pack on the CDN (Cloudflare R2). xc softworks (kudasaixc)
# usage: r2-sync.py <wwwroot> [--off]
#        r2-sync.py --prune [--yes]
#   Uploads every _framework/*.wasm that is not on the bucket yet (names are content-fingerprinted, so a
#   file already there is the same file), brotli-compressed, then points the build's main.js at the CDN.
#   If anything is missing or fails, main.js is left loading everything from the site and the exit code is 0:
#   a CDN problem must never block a deployment. --off just switches the build back to the site.
#   --prune deletes the bucket's .wasm files that no build on this server references any more (site, its
#   sub-builds, out/ and the 5 newest backups/ rollback copies) and that are older than KEEP_DAYS; without --yes it
#   only prints what it would delete.
# config: ~/.config/xc-osu/cloudflare.env (or $XC_CLOUDFLARE_ENV), never in the repository:
#   CLOUDFLARE_API_TOKEN=...   (Account > Workers R2 Storage > Edit is enough)
#   CLOUDFLARE_ACCOUNT_ID=...
#   XC_R2_BUCKET=xc-osu                          (optional)
#   XC_CDN_BASE=https://cdn.xcsoftworks.com/     (optional)
import concurrent.futures, json, os, pathlib, re, subprocess, sys, tempfile, time, urllib.error, urllib.parse, urllib.request

PRUNE = '--prune' in sys.argv
root = pathlib.Path(sys.argv[1] if not PRUNE else '.')
main = root / 'main.js'
REPO = pathlib.Path(__file__).resolve().parent.parent
KEEP_DAYS = 3
ROLLBACK_KEEP = 5
PATTERN = r"const cdnBase = '[^']*';"

def point(base):
    src = main.read_text()
    if not re.search(PATTERN, src): return False
    out = re.sub(PATTERN, "const cdnBase = '" + base + "';", src)
    if out != src: main.write_text(out)
    return True

def site_only(why):
    point('')
    print('r2-sync: ' + why + '; this build loads everything from the site')
    sys.exit(0)

if not PRUNE:
    if '--off' in sys.argv: site_only('switched off')
    if not re.search(PATTERN, main.read_text()): site_only('main.js has no cdnBase')

cfg = {}
env = pathlib.Path(os.environ.get('XC_CLOUDFLARE_ENV', pathlib.Path.home() / '.config/xc-osu/cloudflare.env'))
if env.exists():
    for line in env.read_text().splitlines():
        if '=' in line and not line.lstrip().startswith('#'):
            k, v = line.split('=', 1)
            cfg[k.strip()] = v.strip().strip('"\'')
token, account = cfg.get('CLOUDFLARE_API_TOKEN'), cfg.get('CLOUDFLARE_ACCOUNT_ID')
if not token or not account: site_only('no Cloudflare credentials in ' + str(env))
bucket = cfg.get('XC_R2_BUCKET', 'xc-osu')
base = cfg.get('XC_CDN_BASE', 'https://cdn.xcsoftworks.com/')
api = 'https://api.cloudflare.com/client/v4/accounts/' + account + '/r2/buckets/' + bucket + '/objects'

def call(method, url, data=None, headers=None, timeout=900):
    req = urllib.request.Request(url, data=data, method=method, headers={'Authorization': 'Bearer ' + token, **(headers or {})})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())

stamps = {}

def listing():
    found, cursor = {}, None
    while True:
        q = {'prefix': '_framework/', 'per_page': '1000'}
        if cursor: q['cursor'] = cursor
        d = call('GET', api + '?' + urllib.parse.urlencode(q), timeout=60)
        for o in d.get('result') or []: found[o['key']] = o['size']; stamps[o['key']] = o.get('last_modified', '')
        info = d.get('result_info') or {}
        cursor = info.get('cursor')
        if not info.get('is_truncated') or not cursor: return found

def upload(path, tmp):
    packed = pathlib.Path(tmp) / (path.name + '.br')
    subprocess.run(['brotli', '-q', '9', '-f', '-o', str(packed), str(path)], check=True)
    body = packed.read_bytes()
    packed.unlink()
    for attempt in range(4):
        try:
            call('PUT', api + '/' + urllib.parse.quote('_framework/' + path.name), body, {
                'Content-Type': 'application/wasm' if path.suffix == '.wasm' else 'application/octet-stream', 'Content-Encoding': 'br',
                'Cache-Control': 'public, max-age=31536000, immutable'})
            return path.name, len(body)
        except Exception as e:
            if attempt == 3: raise
            time.sleep(3 * (attempt + 1))

def prune(apply):
    import datetime
    used = set()
    # the live site and its sub-builds, local publish output, and the ROLLBACK_KEEP newest rollback copies
    rollbacks = sorted((d for d in (REPO / 'backups').iterdir() if (d / '_framework').is_dir()), key=lambda d: d.stat().st_mtime)
    for top in [REPO / 'site', REPO / 'out'] + rollbacks[-ROLLBACK_KEEP:]:
        for f in top.rglob('_framework/*.wasm'): used.add('_framework/' + f.name)
        for f in top.rglob('_framework/*.xcpack'): used.add('_framework/' + f.name)
    if len(used) < 10: sys.exit('r2-prune: only %d referenced files found, refusing to prune' % len(used))
    have = listing()
    cutoff = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=KEEP_DAYS)
    def old(k):
        try: return datetime.datetime.fromisoformat(stamps[k].replace('Z', '+00:00')) < cutoff
        except Exception: return False
    gone = sorted(k for k in have if k.endswith(('.wasm', '.xcpack')) and k not in used and old(k))
    mb = sum(have[k] for k in gone) / 1e6
    print('r2-prune: %d objects on the bucket, %d referenced locally, %d unreferenced older than %d days (%.0f MB)%s'
          % (len(have), len(used), len(gone), KEEP_DAYS, mb, '' if apply else ' [dry run, --yes deletes]'))
    for k in gone:
        if not apply: print('  would delete', k, '%.1f MB' % (have[k] / 1e6)); continue
        try: call('DELETE', api + '/' + urllib.parse.quote(k), timeout=60)
        except Exception as e: print('  failed', k, repr(e))

if PRUNE:
    try: prune('--yes' in sys.argv)
    except urllib.error.HTTPError as e: print('r2-prune: Cloudflare answered %d: %s' % (e.code, e.read()[:300].decode('utf-8', 'replace')))
    except Exception as e: print('r2-prune: failed: ' + repr(e))
    sys.exit(0)

try:
    # engine .wasm files and the resource pack (js/xc-respack.js reads it through the same CDN-first loader)
    files = sorted((root / '_framework').glob('*.wasm')) + sorted((root / '_framework').glob('*.xcpack'))
    if not files: site_only('no .wasm in ' + str(root))
    have = listing()
    todo = [f for f in files if not have.get('_framework/' + f.name)]
    sent = {}
    if todo:
        print('r2-sync: uploading %d of %d files (%.0f MB before compression)' % (len(todo), len(files), sum(f.stat().st_size for f in todo) / 1e6))
        with tempfile.TemporaryDirectory() as tmp, concurrent.futures.ThreadPoolExecutor(4) as pool:
            for name, size in pool.map(lambda f: upload(f, tmp), sorted(todo, key=lambda f: -f.stat().st_size)):
                sent[name] = size
        have = listing()
    bad = [f.name for f in files if not have.get('_framework/' + f.name) or (f.name in sent and have['_framework/' + f.name] != sent[f.name])]
    if bad: site_only('%d files missing or truncated on the bucket (%s)' % (len(bad), ', '.join(bad[:3])))
except SystemExit: raise
except urllib.error.HTTPError as e: site_only('Cloudflare answered %d: %s' % (e.code, e.read()[:300].decode('utf-8', 'replace')))
except Exception as e: site_only('failed: ' + repr(e))

point(base)
print('r2-sync: %d files on %s (%d uploaded, %.0f MB sent)' % (len(files), base, len(sent), sum(sent.values()) / 1e6))
