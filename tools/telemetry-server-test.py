#!/usr/bin/env python3
"""Run against an isolated server: python3 tools/telemetry-server-test.py URL DATA_DIR."""
import json, urllib.request, urllib.error, uuid, sqlite3, sys
base, directory = sys.argv[1:]
sid = str(uuid.uuid4())
def post(body, expected=200):
    req=urllib.request.Request(base+'/api/xc/tm',data=(body if isinstance(body,str) else json.dumps(body)).encode(),headers={'Content-Type':'text/plain'})
    try:
        with urllib.request.urlopen(req) as r: status, data = r.status, r.read()
    except urllib.error.HTTPError as e: status,data=e.code,e.read()
    assert status==expected,(status,data[:200])
    return json.loads(data) if status==200 else None
batch={'sid':sid,'meta':{'build':'test','hw':{'gpu':'test GPU'}},'items':[{'q':2,'k':'e','n':'boot','ms':100},{'q':1,'k':'w','th':{'draw':{'im':25}}}]}
assert post(batch)['seq']==2
assert post(batch)['seq']==2
post('not JSON',400)
post({'sid':{'invalid':True}},400)
post({'sid':sid,'items':[{'q':{}}]},400)
post('x'*524289,413)
c=sqlite3.connect(directory+'/telemetry.db')
assert c.execute('select count(*) from tm_windows where sid=?',(sid,)).fetchone()[0]==1
assert c.execute('select count(*) from tm_events where sid=?',(sid,)).fetchone()[0]==1
assert c.execute('select last_seq from tm_sessions where sid=?',(sid,)).fetchone()[0]==2
print('PASS: ingestion, ordering, duplicate retry, malformed payloads, size limit, persisted rows')
