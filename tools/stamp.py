#!/usr/bin/env python3
# xc-osu! — cache-busting for the non-fingerprinted page scripts (a CDN in front may override no-cache).
# usage: stamp.py <wwwroot>   rewrites index.html / main.js / js/*.js references to ?v=<content hash>
import hashlib, pathlib, re, sys

root = pathlib.Path(sys.argv[1])
h = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()[:10]
js = root / 'js'

# leaves first: keys.js is imported by xc-ui.js
ui = js / 'xc-ui.js'
ui.write_text(re.sub(r"\./keys\.js(\?v=[0-9a-f]+)?", "./keys.js?v=" + h(js / 'keys.js'), ui.read_text()))

main = root / 'main.js'
src = main.read_text()
for f in sorted(js.glob('*.js')):
    src = re.sub(r"(\./js/" + re.escape(f.name) + r")(\?v=[0-9a-f]+)?", r"\1?v=" + h(f), src)
main.write_text(src)

idx = root / 'index.html'
html = idx.read_text()
html = re.sub(r'src="main\.js(\?v=[0-9a-f]+)?"', 'src="main.js?v=' + h(main) + '"', html)
idx.write_text(html)
print('stamped', root)
