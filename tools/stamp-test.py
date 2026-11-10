import pathlib, subprocess, tempfile
script=pathlib.Path(__file__).with_name('stamp.py').resolve()
with tempfile.TemporaryDirectory() as tmp:
 p=pathlib.Path(tmp);(p/'js').mkdir();(p/'_framework').mkdir();(p/'index.html').write_text('<script src="main.js"></script>');(p/'main.js').write_text("const engineBuild = 'source';")
 (p/'_framework/engine.first.wasm').touch()
 def stamp():
  subprocess.run(['python3',str(script),str(p)],check=True,capture_output=True)
  return (p/'main.js').read_text(),(p/'index.html').read_text()
 first=stamp();assert stamp()==first,'stamping must be idempotent'
 (p/'_framework/engine.first.wasm').rename(p/'_framework/engine.second.wasm')
 assert stamp()!=first,'engine changes must change build id even with unchanged JS'
print('PASS: engine-aware build fingerprint, stable repeated stamping')
