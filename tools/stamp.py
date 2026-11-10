#!/usr/bin/env python3
# xc-osu! — cache-busting for the non-fingerprinted page scripts (a CDN in front may override no-cache).
# usage: stamp.py <wwwroot>   rewrites every ./x.js reference between js/*.js, main.js and index.html to ?v=<content hash>
import hashlib, pathlib, re, sys

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
idx.write_text(html)
print('stamped', root)
