# PostgreSQL and hundreds of simultaneous players

## The database leaves SQLite

Everything the server knows (accounts, scores, beatmaps, chat, teams, notifications, telemetry) lived in two SQLite files. SQLite serialises writes at the file level: one writer at a time, everyone else queues behind the lock. That was fine at ten players. With hundreds submitting scores at the same moment, one lock set the pace of the whole server.

It is now PostgreSQL 16, on the same machine, reached over the local Unix socket. The state moved in a single transaction and the row counts were checked on both sides: 36 tables, including 11 accounts, 137 scores, 3,153 beatmapsets, 13,334 beatmaps and 181,083 telemetry events.

## What changes in practice

- Connections are pooled (4 to 120) and statements are prepared automatically after three uses.
- Profile statistics are cached for fifteen seconds instead of being recomputed on every visit.
- When several players open the same beatmap, the mirror is asked once. The others wait for that answer, and the serialised response is kept for ten seconds.
- Rankings, the country map and teams each keep their own cache, dropped as soon as something is written.

## The numbers

300 simultaneous connections for 15 seconds against the real database: 805 requests per second, 228 ms median latency, 1.7 s at the 99th percentile, no errors. With 75 logged-in clients among the 300, 651 requests per second; the only non-200 answers were login attempts rejected by the rate limit on purpose.

All of this depends on the machine, the network and the beatmap mirrors, and a first pass with cold caches stays slower.
