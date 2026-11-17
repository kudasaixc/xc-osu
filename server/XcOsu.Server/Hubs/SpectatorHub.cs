// xc-osu! server: spectator hub + server-side replay recording. xc softworks (kudasaixc)

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Spectator;
using osu.Game.Replays;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Replays;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using XcOsu.Server.Data;
using XcOsu.Server.Services;

namespace XcOsu.Server.Hubs
{
    [Authorize(AuthenticationSchemes = XcTokenAuthHandler.SCHEME)]
    public sealed class SpectatorHub : Hub<ISpectatorClient>
    {
        private sealed class Session
        {
            public long? Token;
            public int UserId;
            public SpectatorState State = new SpectatorState();
            public readonly List<LegacyReplayFrame> Frames = new List<LegacyReplayFrame>();
            /// <summary>Server clock when the play began and ended: a replay cannot contain more game time than really passed.</summary>
            public readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
            public DateTimeOffset? EndedAt;
        }

        // frames kept for server-side replays, across every play in progress: bounded so a few accounts cannot exhaust the heap.
        private const int max_frames_per_play = 120_000;
        private const long max_frames_total = 2_000_000;
        private static long framesHeld;

        private static readonly ConcurrentDictionary<int, string> owners = new();
        private static readonly ConcurrentDictionary<string, Session> sessions = new ConcurrentDictionary<string, Session>();

        /// <summary>watcher user id → watched user ids</summary>
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> watching = new ConcurrentDictionary<string, ConcurrentDictionary<int, byte>>();

        private readonly Accounts accounts;
        private readonly Db db;
        private readonly Scores scores;
        private readonly Users users;
        private readonly IHubContext<SpectatorHub, ISpectatorClient> hubContext;
        private readonly ILogger<SpectatorHub> log;
        private readonly IServiceProvider services;
        private readonly Presence presence;

        public SpectatorHub(Accounts accounts, Db db, Scores scores, Users users, IHubContext<SpectatorHub, ISpectatorClient> hubContext, ILogger<SpectatorHub> log,
                            IServiceProvider services, Presence presence)
        {
            this.services = services;
            this.presence = presence;
            this.accounts = accounts;
            this.db = db;
            this.scores = scores;
            this.users = users;
            this.hubContext = hubContext;
            this.log = log;
        }

        private int me => Context.User!.UserId() ?? throw new HubException("unauthorised");

        private static string watchers(int userId) => $"watch:{userId}";

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            int u = me;
            if (sessions.TryRemove(Context.ConnectionId, out var s))
            {
                release(s);
                ((ICollection<KeyValuePair<int, string>>)owners).Remove(new(u, Context.ConnectionId));
                s.State.State = SpectatedUserState.Quit;
                await Clients.Group(watchers(u)).UserFinishedPlaying(u, s.State);
            }

            if (watching.TryRemove(Context.ConnectionId, out var list))
                foreach (int target in list.Keys)
                    await notifyEndWatching(u, target);

            await base.OnDisconnectedAsync(exception);
        }

        public Task BeginPlaySession(long? scoreToken, SpectatorState state) => BeginPlaySessionV2(scoreToken, state);

        public async Task BeginPlaySessionV2(long? scoreToken, SpectatorState state)
        {
            int u = me;
            if (state.Mods.Count() > 32 || state.MaximumStatistics.Count > 64)
                throw new HubException("Invalid spectator state.");
            if (scoreToken is long token)
            {
                using var c = db.Open();
                // only ownership is enforced: the spectator state may legitimately lack a beatmap / ruleset id.
                if (c.Scalar<long>("SELECT COUNT(*) FROM score_tokens WHERE id = @t AND user_id = @u", ("@t", token), ("@u", u)) != 1)
                    throw new HubException("Invalid score token.");
            }
            // a newer connection (reconnect, second tab) takes the play session over, as it always did.
            string? previousOwner = null;
            owners.AddOrUpdate(u, Context.ConnectionId, (_, old) => { previousOwner = old; return Context.ConnectionId; });
            if (previousOwner != null && previousOwner != Context.ConnectionId && sessions.TryRemove(previousOwner, out var replaced))
                release(replaced);
            sessions[Context.ConnectionId] = new Session { UserId = u, Token = scoreToken, State = state };
            await Clients.Group(watchers(u)).UserBeganPlaying(u, state);
        }

        public Task SendFrameData(FrameDataBundle data) => SendFrameDataV2(null, data);

        public async Task SendFrameDataV2(long? scoreToken, FrameDataBundle data)
        {
            if (data.Frames.Count > 4096) throw new HubException("Replay packet too large.");
            int u = me;
            if (sessions.TryGetValue(Context.ConnectionId, out var s))
            {
                if (scoreToken != null && scoreToken != s.Token) throw new HubException("Invalid score token.");
                lock (s.Frames)
                {
                    // past the caps the server-side copy stops growing (the play then has no replay), but watchers keep receiving frames.
                    if (data.Frames.Count <= max_frames_per_play - s.Frames.Count && Interlocked.Add(ref framesHeld, data.Frames.Count) <= max_frames_total)
                        s.Frames.AddRange(data.Frames);
                    else if (data.Frames.Count <= max_frames_per_play - s.Frames.Count)
                        Interlocked.Add(ref framesHeld, -data.Frames.Count);
                }
            }

            await Clients.Group(watchers(u)).UserSentFrames(u, data);
        }

        public Task EndPlaySession(SpectatorState state) => EndPlaySessionV2(null, state.State);

        public async Task EndPlaySessionV2(long? scoreToken, SpectatedUserState finalState)
        {
            int u = me;
            if (sessions.TryGetValue(Context.ConnectionId, out var active) && scoreToken != null && active.Token != scoreToken)
                throw new HubException("Invalid score token.");
            if (!sessions.TryRemove(Context.ConnectionId, out var s)) return;
            ((ICollection<KeyValuePair<int, string>>)owners).Remove(new(u, Context.ConnectionId));

            s.State.State = finalState;
            s.EndedAt = DateTimeOffset.UtcNow;
            await Clients.Group(watchers(u)).UserFinishedPlaying(u, s.State);

            if (s.Token is long token && finalState == SpectatedUserState.Passed && s.Frames.Count > 0)
                _ = Task.Run(async () => { try { await recordReplay(u, token, s); } finally { release(s); } });
            else
                release(s);
        }

        private static void release(Session s)
        {
            int count;
            lock (s.Frames) { count = s.Frames.Count; s.Frames.Clear(); }
            Interlocked.Add(ref framesHeld, -count);
        }

        private async Task recordReplay(int userId, long token, Session session)
        {
            try
            {
                // the score is submitted through the API shortly after gameplay ends.
                long? scoreId = null;
                for (int i = 0; i < 60 && scoreId == null; i++)
                {
                    using (var c = db.Open())
                        scoreId = c.Scalar<long?>("SELECT score_id FROM score_tokens WHERE id = @t AND user_id = @u", ("@t", token), ("@u", userId));
                    if (scoreId == null) await Task.Delay(1000);
                }

                if (scoreId == null) return;

                SoloScoreInfo solo;
                string? checksum;
                using (var c = db.Open())
                {
                    solo = Scores.Load(c, new[] { scoreId.Value }, accounts, users, withBeatmap: false).Single();
                    checksum = c.Scalar<string>("SELECT checksum FROM beatmaps WHERE id = @b", ("@b", solo.BeatmapID));
                }

                var ruleset = Performance.Ruleset(solo.RulesetID);
                if (ruleset == null) return;

                var beatmapInfo = new BeatmapInfo { OnlineID = solo.BeatmapID, MD5Hash = checksum ?? string.Empty, Ruleset = ruleset.RulesetInfo };
                var scoreInfo = solo.ToScoreInfo(solo.Mods.Select(m => m.ToMod(ruleset)).ToArray(), beatmapInfo);
                scoreInfo.Ruleset = ruleset.RulesetInfo;
                scoreInfo.BeatmapInfo = beatmapInfo;
                scoreInfo.User = solo.User!;

                List<ReplayFrame> frames;
                lock (session.Frames) frames = session.Frames.Cast<ReplayFrame>().ToList();

                // game time in the replay against time that really passed on the server: a replay that runs faster than the clock
                // (timewarp, a game speed hack) is refused here, before any simulation.
                double rate = osu.Game.Utils.ModUtils.CalculateRateWithMods(solo.Mods.Select(m => m.ToMod(ruleset)).ToArray());
                double replaySeconds = frames.Count > 1 ? (frames[^1].Time - frames.Where(f => f.Time > 0).Select(f => f.Time).DefaultIfEmpty(0).Min()) / 1000 / rate : 0;
                double wallSeconds = ((session.EndedAt ?? DateTimeOffset.UtcNow) - session.StartedAt).TotalSeconds;
                if (replaySeconds > 20 && replaySeconds > wallSeconds * 1.08 + 3)
                    scores.Flag(scoreId.Value, $"replay holds {replaySeconds:F0}s of play but only {wallSeconds:F0}s passed on the server", unrank: true);

                var score = new Score { ScoreInfo = scoreInfo, Replay = new Replay { Frames = frames } };
                using var ms = new MemoryStream();
                new LegacyScoreEncoder(score, null).Encode(ms, leaveOpen: true);
                scores.AttachReplay(token, ms.ToArray());

                await hubContext.Clients.Group(watchers(userId)).UserScoreProcessed(userId, scoreId.Value);
                log.LogInformation("recorded replay for score {Score} ({Frames} frames)", scoreId, frames.Count);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "replay recording failed for token {Token}", token);
            }
        }

        public async Task StartWatchingUser(int userId)
        {
            int u = me;
            // a player who blocked you, or who appears offline to you, is not watchable (and is not told you tried).
            if (services.GetRequiredService<Chat>().Blocked(u, userId) || !presence.VisibleTo(userId, u, db))
                return;
            if (!AbuseGuard.Allow("spectator-watch:" + Context.ConnectionId, 30, TimeSpan.FromMinutes(1)))
                throw new HubException("Too many watch requests.");
            var targets = watching.GetOrAdd(Context.ConnectionId, _ => new ConcurrentDictionary<int, byte>());
            if (targets.Count >= 16 && !targets.ContainsKey(userId)) throw new HubException("Too many watched users.");
            targets[userId] = 0;
            await Groups.AddToGroupAsync(Context.ConnectionId, watchers(userId));

            if (sessions.Values.FirstOrDefault(s => s.UserId == userId) is { } s)
                await Clients.Caller.UserBeganPlaying(userId, s.State);

            var row = accounts.Get(u);
            await Clients.User(userId.ToString()).UserStartedWatching(new[] { new SpectatorUser { OnlineID = u, Username = row?.Username ?? "?" } });
        }

        public async Task EndWatchingUser(int userId)
        {
            int u = me;
            if (watching.TryGetValue(Context.ConnectionId, out var list)) list.TryRemove(userId, out _);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, watchers(userId));
            await notifyEndWatching(u, userId);
        }

        private Task notifyEndWatching(int watcher, int target) => hubContext.Clients.User(target.ToString()).UserEndedWatching(watcher);
    }
}
