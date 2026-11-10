import importlib.util, json, pathlib, sqlite3, tempfile
spec=importlib.util.spec_from_file_location('history',pathlib.Path(__file__).with_name('performance-history.py'));m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
assert m.percentile([0]*26) is None
h=[0]*26;h[14]=990;h[18]=10
assert m.percentile(h)=={'lower':14,'upper':17}
h[14]=989;h[18]=11
assert m.percentile(h)=={'lower':33,'upper':50}
with tempfile.TemporaryDirectory() as tmp:
 p=pathlib.Path(tmp)/'tm.db';c=sqlite3.connect(p)
 c.execute('create table tm_sessions(sid, gpu, build, info)');c.execute('create table tm_windows(sid, at, data)')
 c.execute('insert into tm_sessions values(?,?,?,?)',('private-session','gpu','before',json.dumps({'options':{'workers':4},'hw':{'browser':'Chrome/1'}})))
 def window(n,total,hidden=0):return {'dur':10000,'per':16.67,'js':{'hidden':hidden},'th':{'draw':{'n':n,'is':total,'ih':[0]*14+[n],'im':17,'late':0,'empty':0},'update':{'wh':[n],'wm':.1}}}
 for at,w in enumerate([window(10,160),window(100,1700),window(100,99999,1)]):c.execute('insert into tm_windows values(?,?,?)',('private-session',at,json.dumps(w)))
 c.commit();c.close();r=m.collect(p)
 assert len(r)==1 and r[0]['frames']==110 and r[0]['windows']==2
 assert r[0]['meanMs']==round(1860/110,3),'weighted mean, hidden windows excluded'
 assert 'private-session' not in json.dumps(r),'no session identifiers published'
print('PASS: weighted mean, cumulative P99 buckets, hidden-window exclusion, no session IDs')
