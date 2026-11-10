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
