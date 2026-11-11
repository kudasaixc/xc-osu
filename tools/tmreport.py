#!/usr/bin/env python3
"""Read-only performance summary: python3 tools/tmreport.py [/var/lib/xc-osu/telemetry.db]."""
import json, sqlite3, sys
from pathlib import Path
path = Path(sys.argv[1] if len(sys.argv) > 1 else '/var/lib/xc-osu/telemetry.db').resolve()
c = sqlite3.connect(path.as_uri() + '?mode=ro', uri=True)
c.row_factory = sqlite3.Row
for title, query in [
    ('Sessions by GPU/build', 'SELECT gpu, build, count(*) sessions, round(avg(boot_ms)) boot_ms FROM tm_sessions GROUP BY gpu, build ORDER BY sessions DESC LIMIT 30'),
    ('Worst frame windows', 'SELECT s.gpu, s.build, w.scr, w.ov, w.draw_max, w.update_max, w.gc_pause, w.late, w.empty FROM tm_windows w JOIN tm_sessions s USING(sid) ORDER BY draw_max DESC LIMIT 30'),
    ('Stutters by context', "SELECT name, scr, ov, count(*) count, round(avg(ms),2) avg_ms, max(ms) max_ms FROM tm_events WHERE kind='spike' GROUP BY name,scr,ov ORDER BY count DESC LIMIT 30"),
    ('Crashes', "SELECT datetime(e.at/1000,'unixepoch') utc, substr(e.sid,1,8) sid, substr(s.iid,1,8) iid, e.name, e.scr, e.ov, json_extract(e.data,'$.code') code, json_extract(e.data,'$.up') up_s, s.build, substr(s.gpu,1,50) gpu, substr(json_extract(e.data,'$.text'),1,400) text FROM tm_events e JOIN tm_sessions s USING(sid) WHERE e.kind='crash' ORDER BY e.at DESC LIMIT 30"),
    ('Unclean exits (reported by the next launch)', "SELECT datetime(e.at/1000,'unixepoch') reported_utc, substr(json_extract(e.data,'$.prev'),1,8) dead_sid, substr(s.iid,1,8) iid, e.name last_screen, e.ov, json_extract(e.data,'$.up') up_s, json_extract(e.data,'$.hidden') hidden, json_extract(e.data,'$.build') build, json_extract(e.data,'$.fatal') fatal FROM tm_events e JOIN tm_sessions s USING(sid) WHERE e.kind='unclean-exit' ORDER BY e.at DESC LIMIT 40"),
    ('Fatal log blocks', "SELECT datetime(e.at/1000,'unixepoch') utc, substr(e.sid,1,8) sid, e.scr, e.ov, json_extract(e.data,'$.lines') lines, substr(json_extract(e.data,'$.text'),1,1500) text FROM tm_events e WHERE e.kind='log' AND json_extract(e.data,'$.category')='crash' ORDER BY e.at DESC LIMIT 20"),
    ('Sessions per installation', "SELECT substr(iid,1,8) iid, count(*) sessions, min(substr(gpu,1,50)) gpu, min(os) os FROM tm_sessions WHERE iid IS NOT NULL GROUP BY iid ORDER BY sessions DESC LIMIT 30"),
    ('Boot checkpoints reached (sessions)', "SELECT name, count(DISTINCT sid) sessions FROM tm_events WHERE kind='boot-checkpoint' GROUP BY name ORDER BY sessions DESC"),
    ('Error / warning messages', "SELECT name level, json_extract(data,'$.category') category, json_extract(data,'$.text') text, count(*) count, count(DISTINCT sid) sessions FROM tm_events WHERE kind='log' AND json_extract(data,'$.text') IS NOT NULL GROUP BY level, text ORDER BY count DESC LIMIT 40"),
    ('Diagnostic events', 'SELECT kind, name, count(*) count FROM tm_events GROUP BY kind,name ORDER BY count DESC LIMIT 30')]:
    print(title)
    for row in c.execute(query): print(json.dumps(dict(row), ensure_ascii=False))

# Correlations describe captured windows, not proof of causation; show sample sizes.
from collections import defaultdict
captures = {}
for row in c.execute("SELECT s.gpu, e.sid, e.at, e.data FROM tm_events e JOIN tm_sessions s USING(sid) WHERE e.kind='flight'"):
    data = json.loads(row['data'])
    entry = captures.setdefault((row['sid'], row['at']), {'gpu': row['gpu'], 'parts': set(), 'expected': data['parts'], 'gc': False, 'textures': False})
    entry['parts'].add(data['part'])
    for frame in data['frames']:
        # Correlate only +/- 100ms near the trigger, not the whole four-second window.
        if abs(frame[0]) <= 100:
            entry['gc'] |= frame[3] > 0 or frame[4] > 0 or frame[5] > 0
            entry['textures'] |= frame[6] > 0
counts = defaultdict(lambda: {'complete_captures': 0, 'incomplete_captures': 0, 'gc_near_trigger': 0, 'textures_near_trigger': 0})
for entry in captures.values():
    count = counts[entry['gpu']]
    if len(entry['parts']) != entry['expected']:
        count['incomplete_captures'] += 1
        continue
    count['complete_captures'] += 1
    count['gc_near_trigger'] += entry['gc']
    count['textures_near_trigger'] += entry['textures']
print('Capture correlations by GPU (+/-100ms; sampled captures, not causation)')
for gpu, count in counts.items():
    n = count['complete_captures']
    print(json.dumps({'gpu': gpu, **count, 'gc_percent': round(100*count['gc_near_trigger']/n,1) if n else None,
                      'textures_percent': round(100*count['textures_near_trigger']/n,1) if n else None}))
