#!/usr/bin/env python3
"""Publish anonymous, aggregate frame history. Read-only DB; no session IDs in output.
python3 tools/performance-history.py --output out/performance --public site/launcher/performance.json
"""
import argparse, collections, datetime, hashlib, json, math, pathlib, sqlite3
EDGES = [.25,.5,1,2,3,4,5,6,7,8,9,10,12,14,17,20,25,33,50,75,100,150,250,500,1000]

def percentile(hist, fraction=.99):
    total = sum(hist)
    if not total: return None
    target = math.ceil(total * fraction)
    count = 0
    for i, n in enumerate(hist):
        count += n
        if count >= target:
            return {'lower': EDGES[i-1] if i else 0, 'upper': EDGES[i] if i < len(EDGES) else None}

def collect(db):
    conn = sqlite3.connect(pathlib.Path(db).resolve().as_uri()+'?mode=ro', uri=True)
    conn.row_factory = sqlite3.Row
    groups = {}
    # One consistent read transaction while live ingestion continues in WAL mode.
    conn.execute('BEGIN')
    for row in conn.execute('SELECT w.sid,w.at,w.data,s.gpu,s.build,s.info FROM tm_windows w JOIN tm_sessions s USING(sid) ORDER BY w.at'):
        w = json.loads(row['data'])
        if w.get('js',{}).get('hidden',0): continue
        meta = json.loads(row['info'] or '{}'); opts = meta.get('options', {})
        setup = {k:opts.get(k) for k in ['frameSync','maxUpdateHz','renderScale','workers','iosCompat']}
        hw = meta.get('hw',{}); setup.update({k:hw.get(k) for k in ['cores','width','height','dpr']})
        ua = hw.get('browser',''); setup['browser'] = 'Edge' if 'Edg/' in ua else 'Chrome' if 'Chrome/' in ua else 'Safari' if 'Safari/' in ua else 'Other'
        refresh = round(1000 / w['per']) if w.get('per',0)>0 else 0
        # Round refresh estimates to avoid splitting a nominal 60 Hz screen into 59/60 Hz groups.
        setup['refreshHz'] = min([30,60,75,90,120,144,165,240,360], key=lambda v:abs(v-refresh))
        key = hashlib.sha256(json.dumps([row['build'],row['gpu'],setup],sort_keys=True).encode()).hexdigest()[:16]
        g = groups.setdefault(key,dict(key=key,build=row['build'],gpu=row['gpu'],setup=setup,sessions=set(),windows=0,seconds=0,frames=0,intervalSum=0,hist=[0]*26,updateHist=[0]*26,late=0,repeated=0,maxMs=0,updateMaxMs=0,first=row['at'],last=row['at']))
        d = w['th']['draw']; u=w['th']['update']
        g['sessions'].add(row['sid']);g['windows']+=1;g['seconds']+=w['dur']/1000;g['frames']+=d['n'];g['intervalSum']+=d['is']
        g['late']+=d.get('late',0);g['repeated']+=d.get('empty',0);g['maxMs']=max(g['maxMs'],d['im']);g['updateMaxMs']=max(g['updateMaxMs'],u['wm']);g['last']=row['at']
        for i,n in enumerate(d['ih']):g['hist'][i]+=n
        for i,n in enumerate(u['wh']):g['updateHist'][i]+=n
    conn.close()
    result=[]
    for g in groups.values():
        g['sessions']=len(g['sessions']);g['seconds']=round(g['seconds'],2)
        count=sum(g['hist']);g['intervalSamples']=count
        g['meanMs']=round(g.pop('intervalSum')/count,3) if count else None
        g['p99']=percentile(g.pop('hist'));g['updateP99']=percentile(g.pop('updateHist'))
        g['latePercent']=round(100*g['late']/g['frames'],3) if g['frames'] else 0
        result.append(g)
    return result

def bound(value):
    if not value:return '—'
    return f"({value['lower']}, {value['upper']}]" if value['upper'] is not None else f"> {value['lower']}"

def markdown(report):
    lines=['# Historique des performances xc-osu!', '', 'Mesures réelles reçues, regroupées par version, GPU et configuration. Les fenêtres visibles incluent démarrage et transitions. Le P99 est un intervalle issu des histogrammes cumulés, pas un percentile exact ni une moyenne des P99. Les suspensions > 1 seconde sont exclues des intervalles par le moteur. Frametime ≠ latence réseau ou délai clic-écran. Les tailles d’échantillon et configurations doivent être comparables avant de conclure à une amélioration.', '', 'Actualisé : '+report['updated'], '', '| Version | GPU | Sessions | Durée (s) | Frames | Moyenne (ms) | P99 (ms) | Max (ms) | Retards |', '|---|---|---:|---:|---:|---:|---:|---:|---:|']
    for r in report['rows']:
        lines.append(f"| {r['build']} | {r['gpu'].replace('|','/')} | {r['sessions']} | {r['seconds']} | {r['frames']} | {r['meanMs']} | {bound(r['p99'])} | {r['maxMs']} | {r['latePercent']} % |")
    return '\n'.join(lines)+'\n'

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--db',default='/var/lib/xc-osu/telemetry.db');parser.add_argument('--output',default='out/performance');parser.add_argument('--public');args=parser.parse_args()
    out=pathlib.Path(args.output);out.mkdir(parents=True,exist_ok=True)
    history=out/'history.json'
    previous=json.loads(history.read_text()) if history.exists() else {'rows':[]}
    rows={r['key']:r for r in previous['rows']}
    # Preserve aggregates whose raw records have expired; do not overwrite them with a smaller retained subset.
    for r in collect(args.db):
        if r['key'] not in rows or r['windows']>=rows[r['key']]['windows']:rows[r['key']]=r
    report={'version':1,'updated':datetime.datetime.now(datetime.timezone.utc).isoformat(),'rows':sorted(rows.values(),key=lambda r:r['first'])}
    payload=json.dumps(report,ensure_ascii=False,indent=2)+'\n'
    for path in [history]+([pathlib.Path(args.public)] if args.public else []):
        path.parent.mkdir(parents=True,exist_ok=True);tmp=path.with_suffix('.tmp');tmp.write_text(payload);tmp.replace(path)
    with (out/'snapshots.jsonl').open('a') as f:f.write(json.dumps(report,ensure_ascii=False)+'\n')
    (out/'history.md').write_text(markdown(report))
    print(markdown(report))
if __name__=='__main__':main()
