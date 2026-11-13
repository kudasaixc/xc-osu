# xc-osu! — Deployment & Operations

## Repository ownership
Canonical repository: `kudasaixc/xc-osu`. The SSH-authenticated checkout is `/home/admin/xc-osu-repo`. The production worktree `/var/www/osu` is **not** a Git repository; `osu/` and `osu-framework/` contain independent upstream Git histories. Never push their upstream `origin` remotes to publish xc-osu!.

Update the canonical checkout with `git fetch origin main && git merge --ff-only origin/main` and explicitly copy only reviewed project sources there. Never commit secrets, logs, databases, or generated `out/` files.

## Local deployment
Local live releases through `tools/deploy.sh dev|aot` now delegate to `tools/deploy-guarded.sh`. Remote-SSH and `aot test` deployments keep the original path.

The guarded script:
1. Refuses unexpected target directories and missing build artifacts.
2. Acquires a process lock and requires at least 1 GiB free disk.
3. Backs up the live root to `/var/www/osu/backups/site-pre-<mode>-<UTC>-<pid>/`.
4. Stamps the engine build ID and uses rsync with delayed updates and delayed deletes.
5. Verifies the resulting index/main JS SHA-256 digests and automatically restores the previous root on failure.

`/dev/`, `/aot/`, `/backgrounds/`, and `/launcher/performance.json` are preserved. Neither compilation nor gameplay tests are automatically run.

### Manual static-site rollback
Find the previous snapshot in `/var/www/osu/backups/last-xc-osu-deployment.txt`. Restore with:

```bash
rsync -a --delete-delay \
  --exclude=/dev/ --exclude=/aot/ --exclude=/backgrounds/ \
  --exclude=/launcher/performance.json \
  /var/www/osu/backups/site-pre-<mode>-<UTC>-<pid>/ /var/www/osu/site/
```

Only replace placeholders with an existing snapshot path. These backups are on the **same disk** and do not protect against disk loss. Server databases and telemetry archives require separate backups.

## Database
The server keeps all state in PostgreSQL 16+, reached over the local Unix socket at `/var/run/postgresql`. The
`xc-osu` service account authenticates by peer credentials, so its PostgreSQL role carries the same name and owns the
`xc_osu` database; `deploy/setup-isolation.sh` creates both on a fresh machine and `deploy/restart-server.sh` refuses
to restart the service when PostgreSQL is down. `Database:ConnectionString` overrides the default
(`Host=/var/run/postgresql;Database=xc_osu`); pool size, timeouts and auto-prepare are derived from it.

The one-time move from the former SQLite files is `dotnet xc-osu-server.dll import-sqlite /var/lib/xc-osu` (or a list
of `*.db` paths) with the service stopped. It replaces every matching table inside a single transaction and verifies
row counts, so a failed import leaves the database untouched. Keep the SQLite files until the import has been
validated, then remove them from backups deliberately.

## Known limits — November 10, 2026
- Root disk is about 93% full; inspect generated output and caches carefully. Never delete live data or rollback snapshots to make room without a retention plan.
- A WebAssembly update-pacing assertion targeting ~240 Hz failed during the earlier Codex run. Browser validation is pending at the owner's request.
- Delayed rsync reduces inconsistency but is **not atomic**. A future versioned-release layout and Apache document-root migration should be designed, tested and rolled back independently.
- Historic telemetry should be preserved and grouped by engine version, browser, GPU and runtime settings. Do not fabricate baselines.
- Offsite database and telemetry backups, monitoring alerts, and a reversible full release are still outstanding.

## Release checklist
- [ ] Review source diff and exact revision.
- [ ] Verify disk capacity and current backup.
- [ ] Deploy existing completed artifacts through the guarded local script.
- [ ] Inspect digests and snapshot path.
- [ ] Validate boot, gameplay, latency and telemetry manually in a browser.
- [ ] Push reviewed sources to GitHub and record any known failures.

## Read-only operator overview
Run `/var/www/osu/tools/ops-status.sh` for disk, running build fingerprint, backup metadata, performance-history freshness, and canonical source state. It makes no network calls or changes.
