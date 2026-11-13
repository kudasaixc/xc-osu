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
            mode = File.Exists(verifier) ? Mode(config) : "off";
            timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue<int?>("ReplayVerification:TimeoutSeconds") ?? 900, 30, 3600));
            if (mode != "off") scores.ReplayAttached += id => queue.Writer.TryWrite(id);
        }

        /// <summary>
        /// What the simulation must reproduce. The same replay does not always land on the exact same number (spinner bonus and
        /// slider tracking depend on frame timing: measured drift on real plays is at most 0.7% of score, 0.2% of accuracy and one
        /// combo), so only a claim clearly above what the replay achieves is refused. Doing worse than the replay is never punished.
        /// </summary>
        public static bool Reproduces(long claimedScore, double claimedAccuracy, int claimedCombo, bool simulatedPass, long simulatedScore, double simulatedAccuracy, int simulatedCombo) =>
            simulatedPass
            && claimedScore <= simulatedScore * 1.03 + 2000
            && claimedAccuracy <= simulatedAccuracy + 0.015
            && claimedCombo <= simulatedCombo * 1.1 + 5;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (mode == "off")
            {
                log.LogWarning("replay verification is off ({Reason})", File.Exists(verifier) ? "configuration" : "verifier not installed at " + verifier);
                return;
            }

            log.LogInformation("replay verification: {Mode}, replay {Required}", mode, ReplayGraceSeconds is int g ? $"required within {g}s" : "optional");
            try
            {
                // replays recorded before this service existed, or while it was down.
                using (var c = db.Open())
                    foreach (long id in c.Query("SELECT id FROM scores WHERE has_replay = 1 AND verification = 0 AND ruleset_id = 0 ORDER BY id", r => r.GetInt64(0)))
                        queue.Writer.TryWrite(id);

                var sweep = sweepMissing(stoppingToken);
                await foreach (long id in queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try { await verify(id, stoppingToken); }
                    catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "replay verification of score {Score} did not complete", id); }
                }
                await sweep;
            }
            catch (OperationCanceledException) { }
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
                    var late = c.Query("SELECT id FROM scores WHERE ranked = 1 AND has_replay = 0 AND replay_due IS NOT NULL AND replay_due < @n",
                        r => r.GetInt64(0), ("@n", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    foreach (long id in late)
                    {
                        c.Exec("UPDATE scores SET ranked = 0, pp = NULL, verification = @v WHERE id = @i AND has_replay = 0", ("@v", MISSING_REPLAY), ("@i", id));
                        log.LogWarning("score {Score} unranked: no replay was delivered", id);
                    }
                    if (late.Count > 0) users.InvalidateLeaderboard();
                }
                catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning(e, "missing-replay sweep failed"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }

        private async Task verify(long scoreId, CancellationToken ct)
        {
            (int beatmap, int ruleset, long total, double accuracy, int combo, int state) row;
            using (var c = db.Open())
                row = c.Query("SELECT beatmap_id, ruleset_id, total_score, accuracy, max_combo, verification FROM scores WHERE id = @i",
                    r => (r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetDouble(3), r.GetInt32(4), r.GetInt32(5)), ("@i", scoreId)).FirstOrDefault();
            if (row.beatmap == 0 || row.state is VERIFIED or MISMATCH or UNVERIFIABLE) return;

            string? replay = scores.ReplayPath(scoreId);
            if (replay == null) return;
            if (row.ruleset != 0) { conclude(scoreId, UNVERIFIABLE, "ruleset is not simulated"); return; }
            // a beatmap the mirrors cannot serve right now is tried again after the next restart.
            if (await mirrors.GetOsuFile(row.beatmap, ct) == null) { log.LogWarning("score {Score}: beatmap {Beatmap} unavailable, verification postponed", scoreId, row.beatmap); return; }

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
                limit.CancelAfter(timeout);
                try { await process.WaitForExitAsync(limit.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    if (ct.IsCancellationRequested) throw;
                    conclude(scoreId, UNVERIFIABLE, "simulation timed out");
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
                    conclude(scoreId, UNVERIFIABLE, $"verifier exited with {process.ExitCode}: {errors}");
                    return;
                }

                var sim = JObject.Parse(await File.ReadAllTextAsync(result, ct));
                bool pass = sim.Value<bool>("Passed");
                long simScore = sim.Value<long>("TotalScore");
                double simAccuracy = sim.Value<double>("Accuracy");
                int simCombo = sim.Value<int>("MaxCombo");
                bool ok = Reproduces(row.total, row.accuracy, row.combo, pass, simScore, simAccuracy, simCombo);
                conclude(scoreId, ok ? VERIFIED : MISMATCH,
                    $"claimed {row.total} / {row.accuracy:P2} / x{row.combo}, replay gives {(pass ? "" : "FAIL ")}{simScore} / {simAccuracy:P2} / x{simCombo} in {watch.Elapsed.TotalSeconds:F0}s");
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
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
                c.Exec("UPDATE scores SET verification = @v, ranked = 0, pp = NULL WHERE id = @i", ("@v", state), ("@i", scoreId));
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
