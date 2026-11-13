#!/usr/bin/env python3
"""Read-only performance summary: python3 tools/tmreport.py ["dbname=xc_osu host=/var/run/postgresql"]."""
import json, sys
import psycopg2, psycopg2.extras
conn = psycopg2.connect(sys.argv[1] if len(sys.argv) > 1 else 'dbname=xc_osu host=/var/run/postgresql')
conn.set_session(readonly=True)
c = conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor)
def rows(query):
    c.execute(query)
    return c.fetchall()
for title, query in [
    ('Sessions by GPU/build', 'SELECT gpu, build, count(*) sessions, round(avg(boot_ms)) boot_ms FROM tm_sessions GROUP BY gpu, build ORDER BY sessions DESC LIMIT 30'),
    ('Worst frame windows', 'SELECT s.gpu, s.build, w.scr, w.ov, w.draw_max, w.update_max, w.gc_pause, w.late, w.empty FROM tm_windows w JOIN tm_sessions s USING(sid) ORDER BY draw_max DESC LIMIT 30'),
    ('Stutters by context', "SELECT name, scr, ov, count(*) count, round(avg(ms)::numeric,2) avg_ms, max(ms) max_ms FROM tm_events WHERE kind='spike' GROUP BY name,scr,ov ORDER BY count DESC LIMIT 30"),
    ('Crashes', "SELECT to_char(to_timestamp(e.at/1000) AT TIME ZONE 'UTC','YYYY-MM-DD HH24:MI:SS') utc, substr(e.sid,1,8) sid, substr(s.iid,1,8) iid, e.name, e.scr, e.ov, e.data::jsonb->'code' code, e.data::jsonb->'up' up_s, s.build, substr(s.gpu,1,50) gpu, substr(e.data::jsonb->>'text',1,400) AS text FROM tm_events e JOIN tm_sessions s USING(sid) WHERE e.kind='crash' ORDER BY e.at DESC LIMIT 30"),
    ('Unclean exits (reported by the next launch)', "SELECT to_char(to_timestamp(e.at/1000) AT TIME ZONE 'UTC','YYYY-MM-DD HH24:MI:SS') reported_utc, substr(e.data::jsonb->>'prev',1,8) dead_sid, substr(s.iid,1,8) iid, e.name last_screen, e.ov, e.data::jsonb->'up' up_s, e.data::jsonb->'hidden' hidden, e.data::jsonb->'build' build, e.data::jsonb->'fatal' fatal FROM tm_events e JOIN tm_sessions s USING(sid) WHERE e.kind='unclean-exit' ORDER BY e.at DESC LIMIT 40"),
    ('Fatal log blocks', "SELECT to_char(to_timestamp(e.at/1000) AT TIME ZONE 'UTC','YYYY-MM-DD HH24:MI:SS') utc, substr(e.sid,1,8) sid, e.scr, e.ov, e.data::jsonb->'lines' lines, substr(e.data::jsonb->>'text',1,1500) AS text FROM tm_events e WHERE e.kind='log' AND e.data::jsonb->>'category'='crash' ORDER BY e.at DESC LIMIT 20"),
    ('Sessions per installation', "SELECT substr(iid,1,8) iid, count(*) sessions, min(substr(gpu,1,50)) gpu, min(os) os FROM tm_sessions WHERE iid IS NOT NULL GROUP BY iid ORDER BY sessions DESC LIMIT 30"),
    ('Boot checkpoints reached (sessions)', "SELECT name, count(DISTINCT sid) sessions FROM tm_events WHERE kind='boot-checkpoint' GROUP BY name ORDER BY sessions DESC"),
    ('Error / warning messages', "SELECT name AS level, min(data::jsonb->>'category') category, data::jsonb->>'text' AS text, count(*) count, count(DISTINCT sid) sessions FROM tm_events WHERE kind='log' AND data::jsonb->>'text' IS NOT NULL GROUP BY name, data::jsonb->>'text' ORDER BY count DESC LIMIT 40"),
    ('Diagnostic events', 'SELECT kind, name, count(*) count FROM tm_events GROUP BY kind,name ORDER BY count DESC LIMIT 30')]:
    print(title)
    for row in rows(query): print(json.dumps(dict(row), ensure_ascii=False, default=float))

# Correlations describe captured windows, not proof of causation; show sample sizes.
from collections import defaultdict
captures = {}
for row in rows("SELECT s.gpu, e.sid, e.at, e.data FROM tm_events e JOIN tm_sessions s USING(sid) WHERE e.kind='flight'"):
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
