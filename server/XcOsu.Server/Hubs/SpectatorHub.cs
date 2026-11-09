// xc-osu! server — spectator hub + server-side replay recording. xc softworks (kudasaixc)

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
            public SpectatorState State = new SpectatorState();
            public readonly List<LegacyReplayFrame> Frames = new List<LegacyReplayFrame>();
        }

        private static readonly ConcurrentDictionary<int, Session> sessions = new ConcurrentDictionary<int, Session>();

        /// <summary>watcher user id → watched user ids</summary>
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> watching = new ConcurrentDictionary<int, ConcurrentDictionary<int, byte>>();

        private readonly Accounts accounts;
        private readonly Db db;
        private readonly Scores scores;
        private readonly Users users;
        private readonly IHubContext<SpectatorHub, ISpectatorClient> hubContext;
        private readonly ILogger<SpectatorHub> log;

        public SpectatorHub(Accounts accounts, Db db, Scores scores, Users users, IHubContext<SpectatorHub, ISpectatorClient> hubContext, ILogger<SpectatorHub> log)
        {
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
            if (sessions.TryRemove(u, out var s))
            {
                s.State.State = SpectatedUserState.Quit;
                await Clients.Group(watchers(u)).UserFinishedPlaying(u, s.State);
            }

            if (watching.TryRemove(u, out var list))
                foreach (int target in list.Keys)
                    await notifyEndWatching(u, target);

            await base.OnDisconnectedAsync(exception);
        }

        public Task BeginPlaySession(long? scoreToken, SpectatorState state) => BeginPlaySessionV2(scoreToken, state);

        public async Task BeginPlaySessionV2(long? scoreToken, SpectatorState state)
        {
            int u = me;
            sessions[u] = new Session { Token = scoreToken, State = state };
            await Clients.Group(watchers(u)).UserBeganPlaying(u, state);
        }

        public Task SendFrameData(FrameDataBundle data) => SendFrameDataV2(null, data);

        public async Task SendFrameDataV2(long? scoreToken, FrameDataBundle data)
        {
            int u = me;
            if (sessions.TryGetValue(u, out var s))
            {
                lock (s.Frames)
                    if (s.Frames.Count < 200_000)
                        s.Frames.AddRange(data.Frames);
            }

            await Clients.Group(watchers(u)).UserSentFrames(u, data);
        }

        public Task EndPlaySession(SpectatorState state) => EndPlaySessionV2(null, state.State);

        public async Task EndPlaySessionV2(long? scoreToken, SpectatedUserState finalState)
        {
            int u = me;
            if (!sessions.TryRemove(u, out var s)) return;

            s.State.State = finalState;
            await Clients.Group(watchers(u)).UserFinishedPlaying(u, s.State);

            if (s.Token is long token && finalState == SpectatedUserState.Passed && s.Frames.Count > 0)
                _ = Task.Run(() => recordReplay(u, token, s));
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
                        scoreId = c.Scalar<long?>("SELECT score_id FROM score_tokens WHERE id = $t AND user_id = $u", ("$t", token), ("$u", userId));
                    if (scoreId == null) await Task.Delay(1000);
                }

                if (scoreId == null) return;

                SoloScoreInfo solo;
                string? checksum;
                using (var c = db.Open())
                {
                    solo = Scores.Load(c, new[] { scoreId.Value }, accounts, users, withBeatmap: false).Single();
                    checksum = c.Scalar<string>("SELECT checksum FROM beatmaps WHERE id = $b", ("$b", solo.BeatmapID));
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
            watching.GetOrAdd(u, _ => new ConcurrentDictionary<int, byte>())[userId] = 0;
            await Groups.AddToGroupAsync(Context.ConnectionId, watchers(userId));

            if (sessions.TryGetValue(userId, out var s))
                await Clients.Caller.UserBeganPlaying(userId, s.State);

            var row = accounts.Get(u);
            await Clients.User(userId.ToString()).UserStartedWatching(new[] { new SpectatorUser { OnlineID = u, Username = row?.Username ?? "?" } });
        }

        public async Task EndWatchingUser(int userId)
        {
            int u = me;
            if (watching.TryGetValue(u, out var list)) list.TryRemove(userId, out _);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, watchers(userId));
            await notifyEndWatching(u, userId);
        }

        private Task notifyEndWatching(int watcher, int target) => hubContext.Clients.User(target.ToString()).UserEndedWatching(watcher);
    }
}
