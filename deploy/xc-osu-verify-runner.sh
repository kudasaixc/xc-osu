#!/bin/bash
# xc-osu! replay verifier runner. xc softworks (kudasaixc)
# Plays back the replays the server queues in $JOBS (one directory per score: beatmap.osu, replay.osr, timeout, ready).
# Runs as its own user, without network, without access to the server's data or database (see xc-osu-verify.service):
# the gameplay code that parses players' files never runs with the server's rights.
# Writes result.json (from the verifier), stderr (tail) and finally exit (the exit code; 124 = timed out).
set -u
JOBS=${XC_VERIFY_JOBS:-/var/lib/xc-osu-verify}
VERIFIER=${XC_VERIFIER:-/var/www/osu/server/verifier/XcOsu.ReplayVerifier.dll}
DOTNET=${XC_DOTNET:-/opt/xc-osu/dotnet/dotnet}
PARALLEL=${XC_VERIFY_PARALLEL:-2}

run() {
  local d=$1 t
  t=$(head -c 8 "$d/timeout" 2>/dev/null)
  case $t in ''|*[!0-9]*) t=900 ;; esac
  [ "$t" -gt 3600 ] && t=3600
  (
    cd "$d" || exit
    TMPDIR="$d/tmp" OSU_TESTS_NO_TIMEOUT=1 DOTNET_gcServer=0 DOTNET_TieredPGO=0 \
      timeout --kill-after=10 "$t" nice -n 10 "$DOTNET" "$VERIFIER" "$d/beatmap.osu" "$d/replay.osr" "$d/result.json" >/dev/null 2>"$d/stderr.full"
    code=$?
    tail -c 2000 "$d/stderr.full" > "$d/stderr" 2>/dev/null; rm -f "$d/stderr.full"; rm -rf "$d/tmp"
    echo "$code" > "$d/exit.tmp" && mv "$d/exit.tmp" "$d/exit"
  )
}

while true; do
  for d in "$JOBS"/job-*; do
    [ -f "$d/ready" ] && [ ! -e "$d/claimed" ] || continue
    while [ "$(jobs -rp | wc -l)" -ge "$PARALLEL" ]; do wait -n; done
    mkdir -p "$d/tmp" && : > "$d/claimed" || continue
    run "$d" &
  done
  # jobs the server gave up on (it deletes its own) or left behind by a crash: gone after a day.
  find "$JOBS" -mindepth 1 -maxdepth 1 -name 'job-*' -mmin +1440 -exec rm -rf {} + 2>/dev/null
  sleep 1
done
