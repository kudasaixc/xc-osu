#!/usr/bin/env python3
# xc-osu!: cache-busting for the non-fingerprinted page scripts (a CDN in front may override no-cache).
# usage: stamp.py <wwwroot>   rewrites every ./x.js reference between js/*.js, main.js and index.html to ?v=<content hash>
import hashlib, pathlib, re, sys

# The page's Content-Security-Policy. The build writes an inline import map into index.html (it names the fingerprinted
# dotnet.js), so script-src can only be enforced with that script's hash, recomputed here at every deploy. The policy goes in a
# <meta> (Apache's header keeps the directives a <meta> cannot carry: frame-ancestors, and stays a stricter floor).
CSP_MARK = 'data-xc-csp'
def with_csp(html):
    import base64
    html = re.sub(r'\s*<meta http-equiv="Content-Security-Policy"[^>]*' + CSP_MARK + r'[^>]*>', '', html)
    inline = re.findall(r'<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>', html, re.S)
    hashes = ' '.join("'sha256-" + base64.b64encode(hashlib.sha256(body.encode()).digest()).decode() + "'" for body in inline)
    policy = ("default-src 'self'; script-src 'self' 'wasm-unsafe-eval' " + hashes + "; style-src 'self' 'unsafe-inline'; "
              "img-src 'self' data: blob: https:; connect-src 'self' https: wss:; worker-src 'self' blob:; media-src 'self' blob: https:; "
              "font-src 'self' data:; object-src 'none'; base-uri 'self'; form-action 'self'")
    meta = '<meta http-equiv="Content-Security-Policy" ' + CSP_MARK + ' content="' + policy + '">'
    return re.sub(r'(<meta charset="[^"]*">)', lambda m: m.group(1) + '\n  ' + meta, html, count=1)

if __name__ != '__main__': raise SystemExit(0)  # imported for with_csp only


root = pathlib.Path(sys.argv[1])
h = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()[:10]
js = root / 'js'
mods = sorted(js.glob('*.js'))

def stamp(path, prefix, targets):
    src = path.read_text()
    out = src
    for f in targets:
        out = re.sub(r"(" + re.escape(prefix + f.name) + r")(\?v=[0-9a-f]+)?(?=['\"])", r"\1?v=" + h(f), out)
    if out != src: path.write_text(out)
    return out != src

# modules importing each other: repeat until stable so a leaf change propagates to its importers
for _ in range(len(mods) + 1):
    if not any([stamp(m, './', [f for f in mods if f != m]) for m in mods]): break

# Engine-only changes must also create a new telemetry build id. Published .wasm filenames
# carry content fingerprints; sorting them keeps repeated stamping stable.
framework = root / '_framework'
if framework.exists():
    assets = '\n'.join(sorted(p.name for p in framework.glob('*.wasm')))
    if assets:
        engine = hashlib.sha256(assets.encode()).hexdigest()[:10]
        main = root / 'main.js'
        src = main.read_text()
        main.write_text(re.sub(r"const engineBuild = '[^']*';", "const engineBuild = '" + engine + "';", src))

stamp(root / 'main.js', './js/', mods)
idx = root / 'index.html'
html = idx.read_text()
html = re.sub(r'src="main\.js(\?v=[0-9a-f]+)?"', 'src="main.js?v=' + h(root / 'main.js') + '"', html)
html = with_csp(html)
idx.write_text(html)
print('stamped', root)
