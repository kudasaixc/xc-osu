# PostgreSQL and hundreds of simultaneous players

## The server database leaves SQLite

All server state (accounts, scores, beatmaps, chat, teams, notifications, telemetry) used to live in two SQLite files. SQLite serialises writes at the file level: when hundreds of players play and submit scores at the same time, requests queue behind a single write lock. The database is now served by PostgreSQL 16 on the same machine, reached over the local Unix socket, with a role dedicated to the service account.

The import ran in a single transaction: every existing table was replaced with its SQLite contents, and row counts were verified: 36 tables, including 11 accounts, 137 scores, 3,153 beatmapsets, 13,334 beatmaps and 181,083 telemetry events. The SQLite files are kept until the migration has been validated; the server no longer opens them.

## What changed for performance

- database connections are pooled (4 to 120 connections), with statements prepared automatically after three uses;
- profile statistics are cached for fifteen seconds instead of being recomputed on every visit;
- concurrent beatmap searches no longer download the same mirror page once per player: one request is made, the others wait for its result, and the serialised response is kept for ten seconds;
- rankings, the country map and teams have their own caches, invalidated on writes.

## The numbers

On the production machine, against the real PostgreSQL database with 300 simultaneous connections for 15 seconds: 805 requests per second, 228 ms median latency, 1.7 s at the 99th percentile, no errors, every response HTTP 200. A run with authenticated connections (75 logged-in clients among 300) sustained 651 requests per second; the only non-200 responses were the deliberate login-attempt limits.

These numbers depend on the machine, the network and the beatmap mirrors, and the caches account for much of the gain: a first cold pass remains slower.

## Operations

The service now depends on PostgreSQL: the setup script creates the `xc-osu` role and the `xc_osu` database when needed, and the restart script refuses to start the server while PostgreSQL is down. The import command (`import-sqlite`) remains available to restore an older SQLite backup.
