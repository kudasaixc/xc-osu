// xc-osu! server: independent replay simulation for ranked scores. xc softworks (kudasaixc)

using System.Diagnostics;
using System.Threading.Channels;
using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    /// <summary>
    /// A ranked score is only as trustworthy as the numbers the client sent. Every recorded replay is therefore played back by a
    /// separate headless process running the real gameplay code, and the result of that simulation is compared with the claim.
    /// A score whose replay does not reproduce it loses its ranking and pp; so does a ranked score that never delivers a replay.
    /// Anything that is the server's own fault (verifier missing, crash, timeout, beatmap unavailable) never costs a player a score.
    /// </summary>
    public sealed class ReplayVerification : BackgroundService
    {
        public const int PENDING = 0, VERIFIED = 1, MISMATCH = 2, UNVERIFIABLE = 3, MISSING_REPLAY = 4;

        /// <summary>Seconds a ranked score has to deliver its replay, or null when replays are not a condition of ranking.</summary>
        public static int? ReplayGraceSeconds { get; private set; }

        private readonly Db db;
        private readonly Scores scores;
        private readonly Users users;
        private readonly Mirrors mirrors;
        private readonly ILogger<ReplayVerification> log;
        private readonly Channel<long> queue = Channel.CreateBounded<long>(new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly string mode;
        private readonly string verifier;
        private readonly TimeSpan timeout;
        private readonly string? jobDirectory;
        private readonly int workers;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> queued = new();

        /// <summary>A replay is tried this many times when the failure may be ours (crash, timeout); after that a person decides.</summary>
        private const int max_attempts = 3;

        public static string Mode(IConfiguration config) => (config["ReplayVerification:Mode"] ?? "enforce").ToLowerInvariant() switch
        {
            "off" => "off", "observe" => "observe", _ => "enforce",
        };

        public static string VerifierPath(IConfiguration config) =>
            config["ReplayVerification:Path"] ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "verifier", "XcOsu.ReplayVerifier.dll"));

        /// <summary>Called once at startup, before any score can be submitted.</summary>
        public static void Configure(IConfiguration config)
        {
            bool enforced = Mode(config) == "enforce" && File.Exists(VerifierPath(config));
            ReplayGraceSeconds = enforced && config.GetValue<bool?>("ReplayVerification:RequireReplay") != false
                ? Math.Max(60, config.GetValue<int?>("ReplayVerification:ReplayGraceSeconds") ?? 900)
                : null;
        }

        public ReplayVerification(Db db, Scores scores, Users users, Mirrors mirrors, IConfiguration config, ILogger<ReplayVerification> log)
        {
            this.db = db;
            this.scores = scores;
            this.users = users;
            this.mirrors = mirrors;
            this.log = log;
            verifier = VerifierPath(config);
            // "queue": replays are played back by a separate, isolated service (deploy/xc-osu-verify.service) reading jobs from this
            // directory: the gameplay code that parses players' files never runs with the server's rights or its database.
            jobDirectory = config["ReplayVerification:Runner"] == "queue" ? config["ReplayVerification:JobDirectory"] ?? "/var/lib/xc-osu-verify" : null;
            mode = jobDirectory != null ? (Directory.Exists(jobDirectory) ? Mode(config) : "off") : File.Exists(verifier) ? Mode(config) : "off";
            timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue<int?>("ReplayVerification:TimeoutSeconds") ?? 900, 30, 3600));
            workers = Math.Clamp(config.GetValue<int?>("ReplayVerification:Workers") ?? 2, 1, 8);
            if (mode != "off") scores.ReplayAttached += id => enqueue(id);
        }

        /// <summary>
        /// What the simulation must reproduce. The same replay does not always land on the exact same number (spinner bonus and
        /// slider tracking depend on frame timing: measured drift on real plays is at most 0.7% of score, 0.2% of accuracy and one
        /// combo), so only a claim clearly above what the replay achieves is refused. Doing worse than the replay is never punished.
        /// </summary>
        public static bool Reproduces(long claimedScore, double claimedAccuracy, int claimedCombo, bool simulatedPass, long simulatedScore, double simulatedAccuracy, int simulatedCombo) =>
            simulatedPass
            // a little above the largest drift measured on real plays, no more: the old margins let every score be inflated by 3 %.
            && claimedScore <= simulatedScore * 1.01 + 1000
            && claimedAccuracy <= simulatedAccuracy + 0.004
            && claimedCombo <= simulatedCombo * 1.02 + 2;

        private void enqueue(long id)
        {
            if (queued.TryAdd(id, 0) && !queue.Writer.TryWrite(id)) queued.TryRemove(id, out _);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (mode == "off")
            {
                log.LogWarning("replay verification is off ({Reason})", File.Exists(verifier) ? "configuration" : "verifier not installed at " + verifier);
                return;
            }

            log.LogInformation("replay verification: {Mode} ({Runner}, {Workers} at a time), replay {Required}", mode, jobDirectory != null ? "isolated runner" : "in-process",
                workers, ReplayGraceSeconds is int g ? $"required within {g}s" : "optional");
            try
            {
                var sweep = sweepMissing(stoppingToken);
                var requeue = requeueWaiting(stoppingToken);
                var running = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
                {
                    await foreach (long id in queue.Reader.ReadAllAsync(stoppingToken))
                    {
                        try { await verify(id, stoppingToken); }
                        catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "replay verification of score {Score} did not complete", id); }
                        finally { queued.TryRemove(id, out _); }
                    }
                }, stoppingToken)).ToList();
                await Task.WhenAll(running.Append(sweep).Append(requeue));
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Every ten minutes (and at start), anything with a replay that is still waiting goes back in the queue: nothing dropped from a
        /// full queue, or interrupted by a restart, stays unverified for good.
        /// </summary>
        private async Task requeueWaiting(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            do
            {
                try
                {
                    using var c = db.Open();
                    foreach (long id in c.Query($"SELECT id FROM scores WHERE has_replay = 1 AND verification = 0 AND verify_attempts < {max_attempts} AND ruleset_id BETWEEN 0 AND 3 ORDER BY id LIMIT 5000", r => r.GetInt64(0)))
                        enqueue(id);
                }
                catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning(e, "verification requeue failed"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }

        private async Task sweepMissing(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            do
            {
                try
                {
                    if (ReplayGraceSeconds == null) continue;
                    using var c = db.Open();
                    var late = c.Query("SELECT id FROM scores WHERE verification = 0 AND has_replay = 0 AND replay_due IS NOT NULL AND replay_due < @n",
                        r => r.GetInt64(0), ("@n", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    foreach (long id in late)
                    {
                        c.Exec("UPDATE scores SET ranked = 0, pp = NULL, pending_ranked = 0, verification = @v WHERE id = @i AND has_replay = 0", ("@v", MISSING_REPLAY), ("@i", id));
                        log.LogWarning("score {Score} not ranked: no replay was delivered", id);
                    }
                    if (late.Count > 0) users.InvalidateLeaderboard();
                }
                catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning(e, "missing-replay sweep failed"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }

        private async Task verify(long scoreId, CancellationToken ct)
        {
            (int beatmap, int ruleset, long total, double accuracy, int combo, int state) row;
            int? mapSeconds;
            using (var c = db.Open())
            {
                row = c.Query("SELECT beatmap_id, ruleset_id, total_score, accuracy, max_combo, verification FROM scores WHERE id = @i",
                    r => (r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetDouble(3), r.GetInt32(4), r.GetInt32(5)), ("@i", scoreId)).FirstOrDefault();
                if (row.beatmap == 0 || row.state is VERIFIED or MISMATCH or UNVERIFIABLE) return;
                if (c.Scalar<long>("UPDATE scores SET verify_attempts = verify_attempts + 1 WHERE id = @i RETURNING verify_attempts", ("@i", scoreId)) > max_attempts) return;
                string? mapJson = c.Scalar<string>("SELECT json FROM beatmaps WHERE id = @b", ("@b", row.beatmap));
                mapSeconds = mapJson == null ? null : JObject.Parse(mapJson).Value<int?>("total_length");
            }
            // a simulation runs about as long as the map at worst: a long timeout for a short map only let a crafted replay stall the queue.
            var limitFor = mapSeconds is int len ? TimeSpan.FromSeconds(Math.Clamp(len * 2 + 120, 60, timeout.TotalSeconds)) : timeout;

            string? replay = scores.ReplayPath(scoreId);
            if (replay == null) return;
            if (Performance.Ruleset(row.ruleset) == null)
            {
                conclude(scoreId, UNVERIFIABLE, "ruleset is not simulated");
                scores.Flag(scoreId, "ruleset is not simulated", unrank: false);
                return;
            }
            // a beatmap the mirrors cannot serve right now is tried again after the next restart.
            if (await mirrors.GetOsuFile(row.beatmap, ct) == null) { log.LogWarning("score {Score}: beatmap {Beatmap} unavailable, verification postponed", scoreId, row.beatmap); return; }

            if (jobDirectory != null)
            {
                await verifyIsolated(scoreId, row, replay, limitFor, ct);
                return;
            }

            string work = Path.Combine(db.DataDirectory, "verify-tmp", scoreId.ToString());
            Directory.CreateDirectory(work);
            try
            {
                string result = Path.Combine(work, "result.json");
                var start = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work,
                };
                foreach (string a in new[] { verifier, mirrors.OsuFilePath(row.beatmap), replay, result }) start.ArgumentList.Add(a);
                // the simulated game writes its scratch storage under the temporary directory: keep it per job and remove it afterwards.
                start.Environment["TMPDIR"] = work;
                start.Environment["OSU_TESTS_NO_TIMEOUT"] = "1";
                start.Environment["DOTNET_gcServer"] = "0";
                start.Environment["DOTNET_TieredPGO"] = "0";
                start.Environment.Remove("ASPNETCORE_URLS");

                var watch = Stopwatch.StartNew();
                using var process = Process.Start(start) ?? throw new InvalidOperationException("verifier did not start");
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                // drained so a chatty child can never block on a full pipe; only the tail of stderr is kept for the log.
                var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
                var stderr = tail(process.StandardError);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(limitFor);
                try { await process.WaitForExitAsync(limit.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    if (ct.IsCancellationRequested) throw;
                    unverifiable(scoreId, "simulation timed out");
                    return;
                }
                await stdout;
                string errors = await stderr;

                // killed from outside (service stopping, out of memory): not a verdict on the replay, it is tried again after a restart.
                if (ct.IsCancellationRequested || process.ExitCode is 137 or 143)
                {
                    log.LogWarning("score {Score}: simulation interrupted, verification postponed", scoreId);
                    return;
                }

                if (process.ExitCode != 0 || !File.Exists(result))
                {
                    unverifiable(scoreId, $"verifier exited with {process.ExitCode}: {errors}");
                    return;
                }

                await judge(scoreId, row, await File.ReadAllTextAsync(result, ct), watch.Elapsed);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        private async Task judge(long scoreId, (int beatmap, int ruleset, long total, double accuracy, int combo, int state) row, string resultJson, TimeSpan took)
        {
            var sim = JObject.Parse(resultJson);
            bool pass = sim.Value<bool>("Passed");
            long simScore = sim.Value<long>("TotalScore");
            double simAccuracy = sim.Value<double>("Accuracy");
            int simCombo = sim.Value<int>("MaxCombo");
            bool ok = Reproduces(row.total, row.accuracy, row.combo, pass, simScore, simAccuracy, simCombo);
            conclude(scoreId, ok ? VERIFIED : MISMATCH,
                $"claimed {row.total} / {row.accuracy:P2} / x{row.combo}, replay gives {(pass ? "" : "FAIL ")}{simScore} / {simAccuracy:P2} / x{simCombo} in {took.TotalSeconds:F0}s");
            if (ok && mode == "enforce") await scores.ConfirmVerified(scoreId);
        }

        /// <summary>
        /// The simulation could not give a verdict. It may be our fault, so the score is tried again later; but it never gets its pp
        /// meanwhile, and after the last attempt a person decides (moderation queue). A crafted replay that crashes or stalls the
        /// simulation therefore earns nothing.
        /// </summary>
        private void unverifiable(long scoreId, string detail)
        {
            long attempts;
            using (var c = db.Open()) attempts = c.Scalar<long>("SELECT verify_attempts FROM scores WHERE id = @i", ("@i", scoreId));
            if (attempts < max_attempts)
            {
                log.LogWarning("score {Score}: verification attempt {Attempt} failed, retried later: {Detail}", scoreId, attempts, detail);
                return;
            }
            conclude(scoreId, UNVERIFIABLE, detail);
            scores.Flag(scoreId, "replay could not be verified: " + (detail.Length > 200 ? detail[..200] : detail), unrank: mode == "enforce");
        }

        /// <summary>Plays the replay back through the isolated runner (deploy/xc-osu-verify.service): a job directory in, a result file out.</summary>
        private async Task verifyIsolated(long scoreId, (int beatmap, int ruleset, long total, double accuracy, int combo, int state) row, string replay, TimeSpan limit, CancellationToken ct)
        {
            string job = Path.Combine(jobDirectory!, $"job-{scoreId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(job);
            var watch = Stopwatch.StartNew();
            try
            {
                File.Copy(mirrors.OsuFilePath(row.beatmap), Path.Combine(job, "beatmap.osu"));
                File.Copy(replay, Path.Combine(job, "replay.osr"));
                await File.WriteAllTextAsync(Path.Combine(job, "timeout"), ((int)limit.TotalSeconds).ToString(), ct);
                await File.WriteAllTextAsync(Path.Combine(job, "ready"), "", ct);

                string exitFile = Path.Combine(job, "exit");
                // the runner may be busy with other jobs: wait for this one's turn as well as its run.
                var deadline = DateTime.UtcNow + limit + TimeSpan.FromMinutes(30);
                while (!File.Exists(exitFile))
                {
                    if (DateTime.UtcNow > deadline) { log.LogWarning("score {Score}: the verifier runner did not pick the job up, retried later", scoreId); return; }
                    await Task.Delay(500, ct);
                }
                int exit = int.TryParse((await File.ReadAllTextAsync(exitFile, ct)).Trim(), out int e) ? e : -1;
                string result = Path.Combine(job, "result.json");
                if (exit is 137 or 143 && !File.Exists(result)) { log.LogWarning("score {Score}: simulation interrupted, retried later", scoreId); return; }
                if (exit == 124) { unverifiable(scoreId, "simulation timed out"); return; }
                if (exit != 0 || !File.Exists(result))
                {
                    string errors = File.Exists(Path.Combine(job, "stderr")) ? (await File.ReadAllTextAsync(Path.Combine(job, "stderr"), ct)).Trim().Split('\n').LastOrDefault() ?? "" : "";
                    unverifiable(scoreId, $"verifier exited with {exit}: {(errors.Length > 300 ? errors[..300] : errors)}");
                    return;
                }
                await judge(scoreId, row, await File.ReadAllTextAsync(result, ct), watch.Elapsed);
            }
            finally
            {
                try { Directory.Delete(job, true); } catch { }
            }
        }

        private static async Task<string> tail(StreamReader reader)
        {
            string last = "";
            while (await reader.ReadLineAsync() is string line)
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])) last = line.Length > 300 ? line[..300] : line;
            return last;
        }

        private void conclude(long scoreId, int state, string detail)
        {
            using var c = db.Open();
            if (state == MISMATCH && mode == "enforce")
            {
                c.Exec("UPDATE scores SET verification = @v, ranked = 0, pp = NULL, pending_ranked = 0 WHERE id = @i", ("@v", state), ("@i", scoreId));
                users.InvalidateLeaderboard();
                log.LogWarning("score {Score} unranked, its replay does not reproduce it: {Detail}", scoreId, detail);
                return;
            }

            // in observe mode a mismatch is only reported, so the score stays as it was and is looked at again once enforcement is on.
            if (state != MISMATCH) c.Exec("UPDATE scores SET verification = @v WHERE id = @i", ("@v", state), ("@i", scoreId));
            if (state == VERIFIED) log.LogInformation("score {Score} verified: {Detail}", scoreId, detail);
            else log.LogWarning("score {Score} {State}: {Detail}", scoreId, state == MISMATCH ? "would be unranked (observe mode)" : "could not be verified", detail);
        }
    }
}
