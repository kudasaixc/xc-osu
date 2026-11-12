#!/usr/bin/env python3
# xc-osu! — put a build's engine .wasm files on the CDN (Cloudflare R2). xc softworks (kudasaixc)
# usage: r2-sync.py <wwwroot> [--off]
#   Uploads every _framework/*.wasm that is not on the bucket yet (names are content-fingerprinted, so a
#   file already there is the same file), brotli-compressed, then points the build's main.js at the CDN.
#   If anything is missing or fails, main.js is left loading everything from the site and the exit code is 0:
#   a CDN problem must never block a deployment. --off just switches the build back to the site.
# config: ~/.config/xc-osu/cloudflare.env (or $XC_CLOUDFLARE_ENV), never in the repository:
#   CLOUDFLARE_API_TOKEN=...   (Account > Workers R2 Storage > Edit is enough)
#   CLOUDFLARE_ACCOUNT_ID=...
#   XC_R2_BUCKET=xc-osu                          (optional)
#   XC_CDN_BASE=https://cdn.xcsoftworks.com/     (optional)
import concurrent.futures, json, os, pathlib, re, subprocess, sys, tempfile, time, urllib.error, urllib.parse, urllib.request

root = pathlib.Path(sys.argv[1])
main = root / 'main.js'
PATTERN = r"const cdnBase = '[^']*';"

def point(base):
    src = main.read_text()
    if not re.search(PATTERN, src): return False
    out = re.sub(PATTERN, "const cdnBase = '" + base + "';", src)
    if out != src: main.write_text(out)
    return True

def site_only(why):
    point('')
    print('r2-sync: ' + why + ' — this build loads everything from the site')
    sys.exit(0)

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

def listing():
    found, cursor = {}, None
    while True:
        q = {'prefix': '_framework/', 'per_page': '1000'}
        if cursor: q['cursor'] = cursor
        d = call('GET', api + '?' + urllib.parse.urlencode(q), timeout=60)
        for o in d.get('result') or []: found[o['key']] = o['size']
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
                'Content-Type': 'application/wasm', 'Content-Encoding': 'br',
                'Cache-Control': 'public, max-age=31536000, immutable'})
            return path.name, len(body)
        except Exception as e:
            if attempt == 3: raise
            time.sleep(3 * (attempt + 1))

try:
    files = sorted((root / '_framework').glob('*.wasm'))
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
