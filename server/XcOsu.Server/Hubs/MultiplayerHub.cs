// xc-osu! server — realtime multiplayer hub (lazer protocol). xc softworks (kudasaixc)

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Game.Online.Matchmaking.Events;
using osu.Game.Online.Matchmaking.Requests;
using osu.Game.Online.Matchmaking.Responses;
using osu.Game.Online.RankedPlay;
using MatchType = osu.Game.Online.Rooms.MatchType;
using XcOsu.Server.Services;

namespace XcOsu.Server.Hubs
{
    /// <summary>Everything the lazer client listens to on the multiplayer hub (rooms, quick play, ranked play).</summary>
    public interface IXcMultiplayerClient : IMultiplayerClient, IMatchmakingClient, IRankedPlayClient
    {
    }

    [Authorize(AuthenticationSchemes = XcTokenAuthHandler.SCHEME)]
    public sealed class MultiplayerHub : Hub<IXcMultiplayerClient>
    {
        /// <summary>user id → room id</summary>
        private static readonly ConcurrentDictionary<int, long> user_rooms = new ConcurrentDictionary<int, long>();

        /// <summary>user id → connection id</summary>
        internal static readonly ConcurrentDictionary<int, string> user_connections = new ConcurrentDictionary<int, string>();

        internal static long? RoomOf(int userId) => user_rooms.TryGetValue(userId, out long r) ? r : null;

        private readonly RoomStore store;
        private readonly Accounts accounts;
        private readonly Users users;
        private readonly Chat chat;
        private readonly Mirrors mirrors;
        private readonly IHubContext<MultiplayerHub, IXcMultiplayerClient> hubContext;
        private readonly ILogger<MultiplayerHub> log;
        private readonly IServiceProvider services;

        private Matchmaking matchmaking => services.GetRequiredService<Matchmaking>();

        public MultiplayerHub(RoomStore store, Accounts accounts, Users users, Chat chat, Mirrors mirrors, IHubContext<MultiplayerHub, IXcMultiplayerClient> hubContext,
                              ILogger<MultiplayerHub> log, IServiceProvider services)
        {
            this.services = services;
            this.store = store;
            this.accounts = accounts;
            this.users = users;
            this.chat = chat;
            this.mirrors = mirrors;
            this.hubContext = hubContext;
            this.log = log;
        }

        private int me => Context.User!.UserId() ?? throw new HubException("unauthorised");

        private static string group(long roomId) => $"room:{roomId}";

        public override async Task OnConnectedAsync()
        {
            int u = me;
            if (user_connections.TryGetValue(u, out string? old) && old != Context.ConnectionId)
                await Clients.Client(old).DisconnectRequested();
            user_connections[u] = Context.ConnectionId;
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            int u = me;
            if (user_connections.TryGetValue(u, out string? c) && c == Context.ConnectionId)
            {
                user_connections.TryRemove(u, out _);
                await matchmaking.Disconnected(u);
                await leave(u, kicked: false);
            }

            await base.OnDisconnectedAsync(exception);
        }

        // ------------------------------------------------------------------ lounge

        private static void validateSettings(MultiplayerRoomSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Length > 100 || (settings.Password?.Length ?? 0) > 128
                || settings.MaxParticipants is 0 || !Enum.IsDefined(settings.MatchType) || !Enum.IsDefined(settings.QueueMode)
                || settings.AutoStartDuration < TimeSpan.Zero || settings.AutoStartDuration > TimeSpan.FromHours(1))
                throw new HubException("Invalid room settings.");
        }

        public async Task<MultiplayerRoom> CreateRoom(MultiplayerRoom room)
        {
            validateSettings(room.Settings);
            if (room.Playlist.Count is < 1 or > 100) throw new HubException("A room needs 1 to 100 playlist items.");
            if (room.Settings.MatchType.IsMatchmakingType()) throw new HubException("Quick play and ranked play rooms are created by the matchmaker.");
            foreach (var item in room.Playlist) await fillStarRating(item);
            int u = me;
            await leave(u, false);

            if (room.Settings.MatchType.IsMatchmakingType()) throw new HubException("Quick play and ranked play rooms are created by the matchmaker.");
            long id = store.AllocateRoomId(u, "realtime", room.Settings.MatchType.ToString());
            int channelId = chat.CreateRoomChannel(id, room.Settings.Name);
            var live = new MultiplayerRoom(id)
            {
                Settings = room.Settings,
                State = MultiplayerRoomState.Open,
                ChannelID = channelId,
            };

            ushort order = 0;
            foreach (var item in room.Playlist)
            {
                item.ID = store.NextPlaylistItemId();
                item.OwnerID = u;
                item.Expired = false;
                item.PlayedAt = null;
                item.PlaylistOrder = order++;
                live.Playlist.Add(item);
            }

            if (live.Playlist.Count == 0) throw new HubException("A room needs at least one playlist item.");
            live.Settings.PlaylistItemId = live.Playlist[0].ID;
            live.MatchState = createMatchState(live.Settings.MatchType, live.Settings.MaxParticipants);

            var server = new ServerRoom
            {
                Id = id,
                Live = live,
                ChannelId = channelId,
                Json = new JObject
                {
                    ["id"] = id,
                    ["category"] = "realtime",
                    ["starts_at"] = DateTimeOffset.UtcNow,
                    ["ends_at"] = null,
                    ["channel_id"] = channelId,
                },
            };

            store.Rooms[id] = server;
            log.LogInformation("user {User} created multiplayer room {Room}", u, id);
            return await join(server, u, null, isCreator: true);
        }

        public Task<MultiplayerRoom> JoinRoom(long roomId) => JoinRoomWithPassword(roomId, string.Empty);

        public async Task<MultiplayerRoom> JoinRoomWithPassword(long roomId, string password)
        {
            if (!AbuseGuard.Allow($"room-password:{me}:{roomId}", 10, TimeSpan.FromMinutes(1)))
                throw new HubException("Too many join attempts.");
            if (!store.Rooms.TryGetValue(roomId, out var room) || room.Live == null)
                throw new HubException("This room no longer exists.");
            await leave(me, false);
            return await join(room, me, password, isCreator: false);
        }

        private async Task<MultiplayerRoom> join(ServerRoom room, int u, string? password, bool isCreator)
        {
            if (room.Live!.Settings.MatchType.IsMatchmakingType() && !matchmaking.IsInvited(room.Id, u))
                throw new HubException("This match is only open to the players the matchmaker picked.");

            var live = await joinCore(room, u, password, isCreator);
            if (room.ChannelId > 0) await chat.JoinAndNotify(room.ChannelId, u);
            if (live.Settings.MatchType.IsMatchmakingType()) await matchmaking.UserJoined(room, u);
            return live;
        }

        private async Task<MultiplayerRoom> joinCore(ServerRoom room, int u, string? password, bool isCreator)
        {
            var account = accounts.Get(u) ?? throw new HubException("unknown user");
            MultiplayerRoomUser user;
            MultiplayerRoom live;

            lock (room.Lock)
            {
                live = room.Live!;
                if (!isCreator && !string.IsNullOrEmpty(live.Settings.Password) && live.Settings.Password != password)
                    throw new InvalidPasswordException();
                if (live.Settings.MaxParticipants is byte max && live.Users.Count >= max)
                    throw new HubException("This room is full.");
                if (live.Users.Any(x => x.UserID == u))
                    throw new HubException("Already in this room.");

                user = new MultiplayerRoomUser(u) { User = users.Compact(account) };
                if (live.MatchState is TeamVersusRoomState tv)
                {
                    int team = tv.Teams.OrderBy(t => live.Users.Count(x => (x.MatchState as TeamVersusUserState)?.TeamID == t.ID)).First().ID;
                    user.MatchState = new TeamVersusUserState { TeamID = team };
                }

                live.Users.Add(user);
                live.Host ??= user;
                room.Participants.Add(u);
            }

            user_rooms[u] = room.Id;
            await Clients.Group(group(room.Id)).UserJoined(user);
            await Groups.AddToGroupAsync(Context.ConnectionId, group(room.Id));
            await store.SyncJson(room, users, accounts);
            return live;
        }

        // ------------------------------------------------------------------ room

        private (ServerRoom room, MultiplayerRoom live, MultiplayerRoomUser user) current()
        {
            int u = me;
            if (!user_rooms.TryGetValue(u, out long id) || !store.Rooms.TryGetValue(id, out var room) || room.Live == null)
                throw new NotJoinedRoomException();
            var user = room.Live.Users.FirstOrDefault(x => x.UserID == u) ?? throw new NotJoinedRoomException();
            return (room, room.Live, user);
        }

        private (ServerRoom room, MultiplayerRoom live, MultiplayerRoomUser user) currentAsHost()
        {
            var c = current();
            if (c.live.Host?.UserID != c.user.UserID) throw new NotHostException();
            return c;
        }

        public Task LeaveRoom() => leave(me, false);

        private async Task leave(int u, bool kicked, long? expectedRoom = null)
        {
            long id;
            if (expectedRoom is long expected)
            {
                if (!((ICollection<KeyValuePair<int, long>>)user_rooms).Remove(new(u, expected))) return;
                id = expected;
            }
            else if (!user_rooms.TryRemove(u, out id)) return;
            if (!store.Rooms.TryGetValue(id, out var room) || room.Live == null)
                return;

            MultiplayerRoomUser? user;
            bool empty, hostChanged = false;
            lock (room.Lock)
            {
                var live = room.Live;
                user = live.Users.FirstOrDefault(x => x.UserID == u);
                if (user == null) return;
                live.Users.Remove(user);
                empty = live.Users.Count == 0;
                if (!empty && live.Host?.UserID == u)
                {
                    live.Host = live.Users.First();
                    hostChanged = true;
                }
            }

            if (user_connections.TryGetValue(u, out string? conn))
                await Groups.RemoveFromGroupAsync(conn, group(id));

            var clients = hubContext.Clients;
            if (kicked)
            {
                await clients.Group(group(id)).UserKicked(user);
                if (conn != null) await clients.Client(conn).UserKicked(user);
            }
            else
                await clients.Group(group(id)).UserLeft(user);

            if (room.ChannelId > 0) await chat.PartAndNotify(room.ChannelId, u);
            if (room.Live.Settings.MatchType.IsMatchmakingType()) await matchmaking.UserLeft(room, u);

            if (empty)
            {
                room.Countdown?.Cancel();
                store.Close(room);
                if (room.ChannelId > 0) await chat.CloseRoomChannel(room.ChannelId);
                matchmaking.RoomClosed(room);
                log.LogInformation("multiplayer room {Room} closed", id);
                return;
            }

            if (hostChanged)
                await clients.Group(group(id)).HostChanged(room.Live.Host!.UserID);

            await updateGameplayProgress(room);
            await store.SyncJson(room, users, accounts);
        }

        public async Task TransferHost(int userId)
        {
            var (room, live, _) = currentAsHost();
            if (live.Settings.MatchType.IsMatchmakingType()) throw new InvalidStateException("Not available in this room.");
            lock (room.Lock)
                live.Host = live.Users.FirstOrDefault(x => x.UserID == userId) ?? throw new HubException("User not in room.");
            await Clients.Group(group(room.Id)).HostChanged(userId);
            await store.SyncJson(room, users, accounts);
        }

        public async Task KickUser(int userId)
        {
            var (room, live, user) = currentAsHost();
            if (live.Settings.MatchType.IsMatchmakingType()) throw new InvalidStateException("Not available in this room.");
            if (userId == user.UserID) throw new HubException("Can't kick yourself.");
            if (!user_rooms.TryGetValue(userId, out var targetRoom) || targetRoom != room.Id)
                throw new HubException("User not in room.");
            await leave(userId, kicked: true, expectedRoom: room.Id);
        }

        public async Task ChangeSettings(MultiplayerRoomSettings settings)
        {
            validateSettings(settings);
            var (room, live, _) = currentAsHost();
            if (live.State != MultiplayerRoomState.Open) throw new InvalidStateException("Can't change settings while a match is in progress.");
            if (live.Settings.MatchType.IsMatchmakingType() || settings.MatchType.IsMatchmakingType()) throw new InvalidStateException("Not available in this room.");
            bool renamed = live.Settings.Name != settings.Name;

            bool matchTypeChanged;
            lock (room.Lock)
            {
                matchTypeChanged = live.Settings.MatchType != settings.MatchType;
                settings.PlaylistItemId = live.Settings.PlaylistItemId;
                live.Settings = settings;
                if (matchTypeChanged)
                {
                    live.MatchState = createMatchState(settings.MatchType, settings.MaxParticipants);
                    foreach (var u in live.Users)
                        u.MatchState = live.MatchState is TeamVersusRoomState ? new TeamVersusUserState { TeamID = live.Users.IndexOf(u) % 2 } : null;
                }
            }

            await Clients.Group(group(room.Id)).SettingsChanged(settings);
            if (renamed && room.ChannelId > 0) chat.RenameRoomChannel(room.ChannelId, settings.Name);
            if (matchTypeChanged)
            {
                await Clients.Group(group(room.Id)).MatchRoomStateChanged(live.MatchState!);
                foreach (var u in live.Users)
                    await Clients.Group(group(room.Id)).MatchUserStateChanged(u.UserID, u.MatchState!);
            }

            await store.SyncJson(room, users, accounts);
        }

        public async Task ChangeState(MultiplayerUserState newState)
        {
            var (room, live, user) = current();
            if (user.State == newState) return;

            switch (newState)
            {
                case MultiplayerUserState.Idle:
                    if (user.State is MultiplayerUserState.WaitingForLoad or MultiplayerUserState.Loaded or MultiplayerUserState.ReadyForGameplay or MultiplayerUserState.Playing)
                        throw new InvalidStateChangeException(user.State, newState);
                    break;

                case MultiplayerUserState.Ready:
                    if (user.State != MultiplayerUserState.Idle || live.State != MultiplayerRoomState.Open)
                        throw new InvalidStateChangeException(user.State, newState);
                    break;

                case MultiplayerUserState.Loaded:
                    if (user.State != MultiplayerUserState.WaitingForLoad) throw new InvalidStateChangeException(user.State, newState);
                    break;

                case MultiplayerUserState.ReadyForGameplay:
                    if (user.State != MultiplayerUserState.Loaded) throw new InvalidStateChangeException(user.State, newState);
                    break;

                case MultiplayerUserState.FinishedPlay:
                    if (user.State != MultiplayerUserState.Playing) throw new InvalidStateChangeException(user.State, newState);
                    break;

                case MultiplayerUserState.Spectating:
                    if (user.State is not (MultiplayerUserState.Idle or MultiplayerUserState.Ready)) throw new InvalidStateChangeException(user.State, newState);
                    break;

                default:
                    throw new InvalidStateChangeException(user.State, newState);
            }

            await setState(room, user, newState);

            if (newState == MultiplayerUserState.Spectating && live.State is MultiplayerRoomState.WaitingForLoad or MultiplayerRoomState.Playing)
                await Clients.Caller.LoadRequested();

            await updateGameplayProgress(room);
            await maybeAutoStart(room);
        }

        internal async Task setState(ServerRoom room, MultiplayerRoomUser user, MultiplayerUserState state)
        {
            lock (room.Lock) user.State = state;
            await hubContext.Clients.Group(group(room.Id)).UserStateChanged(user.UserID, state);
        }

        internal async Task setRoomState(ServerRoom room, MultiplayerRoomState state)
        {
            lock (room.Lock) room.Live!.State = state;
            await hubContext.Clients.Group(group(room.Id)).RoomStateChanged(state);
        }

        /// <summary>Advances WaitingForLoad → Playing → Results as users progress.</summary>
        /// <remarks>
        /// The decision and the state change happen atomically under the room lock: several users change state at the same moment,
        /// and each of those calls lands here — only one of them may start (or finish) the gameplay.
        /// </remarks>
        internal async Task updateGameplayProgress(ServerRoom room)
        {
            var live = room.Live!;
            var clients = hubContext.Clients.Group(group(room.Id));
            List<MultiplayerRoomUser> changed;
            MultiplayerUserState userState;
            bool started = false, finished = false;

            lock (room.Lock)
            {
                if (live.State == MultiplayerRoomState.WaitingForLoad)
                {
                    if (live.Users.Any(u => u.State is MultiplayerUserState.WaitingForLoad or MultiplayerUserState.Loaded)) return;

                    changed = live.Users.Where(u => u.State == MultiplayerUserState.ReadyForGameplay).ToList();
                    userState = MultiplayerUserState.Playing;
                    foreach (var u in changed) u.State = userState;
                    started = changed.Count > 0;
                    live.State = started ? MultiplayerRoomState.Playing : MultiplayerRoomState.Open;
                }
                else if (live.State == MultiplayerRoomState.Playing && !live.Users.Any(u => u.State == MultiplayerUserState.Playing))
                {
                    changed = live.Users.Where(u => u.State == MultiplayerUserState.FinishedPlay).ToList();
                    userState = MultiplayerUserState.Results;
                    foreach (var u in changed) u.State = userState;
                    live.State = MultiplayerRoomState.Open;
                    finished = true;
                }
                else
                    return;
            }

            foreach (var u in changed) await clients.UserStateChanged(u.UserID, userState);
            await clients.RoomStateChanged(live.State);

            if (started)
                await clients.GameplayStarted();

            if (finished)
            {
                await clients.ResultsReady();
                await finishCurrentItem(room);
                if (live.Settings.MatchType.IsMatchmakingType()) await services.GetRequiredService<Matchmaking>().GameplayFinished(room);
            }
        }

        internal async Task finishCurrentItem(ServerRoom room)
        {
            var live = room.Live!;
            MultiplayerPlaylistItem? next;
            MultiplayerPlaylistItem? duplicate = null;
            var current = live.Playlist.FirstOrDefault(p => p.ID == live.Settings.PlaylistItemId);

            lock (room.Lock)
            {
                if (current != null)
                {
                    current.Expired = true;
                    current.PlayedAt = DateTimeOffset.UtcNow;
                }

                if (live.Settings.QueueMode == QueueMode.HostOnly && !live.Settings.MatchType.IsMatchmakingType() && current != null && live.Playlist.All(p => p.Expired))
                {
                    duplicate = new MultiplayerPlaylistItem
                    {
                        ID = store.NextPlaylistItemId(),
                        OwnerID = current.OwnerID,
                        BeatmapID = current.BeatmapID,
                        BeatmapChecksum = current.BeatmapChecksum,
                        RulesetID = current.RulesetID,
                        RequiredMods = current.RequiredMods,
                        AllowedMods = current.AllowedMods,
                        StarRating = current.StarRating,
                        Freestyle = current.Freestyle,
                        PlaylistOrder = (ushort)(live.Playlist.Max(p => p.PlaylistOrder) + 1),
                    };
                    live.Playlist.Add(duplicate);
                }

                next = live.Settings.MatchType.IsMatchmakingType() ? null : nextItem(live);
                if (next != null) live.Settings.PlaylistItemId = next.ID;
                foreach (var u in live.Users) u.VotedToSkipIntro = false;
            }

            var clients = hubContext.Clients.Group(group(room.Id));
            if (current != null) await clients.PlaylistItemChanged(current);
            if (duplicate != null) await clients.PlaylistItemAdded(duplicate);
            await clients.SettingsChanged(live.Settings);
            await store.SyncJson(room, users, accounts);
        }

        private static MultiplayerPlaylistItem? nextItem(MultiplayerRoom live)
        {
            var pending = live.Playlist.Where(p => !p.Expired).ToList();
            if (pending.Count == 0) return live.Playlist.OrderByDescending(p => p.PlayedAt).FirstOrDefault();

            if (live.Settings.QueueMode == QueueMode.AllPlayersRoundRobin)
            {
                var played = live.Playlist.Where(p => p.Expired).GroupBy(p => p.OwnerID).ToDictionary(g => g.Key, g => g.Count());
                return pending.OrderBy(p => played.GetValueOrDefault(p.OwnerID)).ThenBy(p => p.ID).First();
            }

            return pending.OrderBy(p => p.PlaylistOrder).ThenBy(p => p.ID).First();
        }

        public async Task ChangeBeatmapAvailability(BeatmapAvailability availability)
        {
            var (room, _, user) = current();
            lock (room.Lock) user.BeatmapAvailability = availability;
            await Clients.Group(group(room.Id)).UserBeatmapAvailabilityChanged(user.UserID, availability);
        }

        public async Task ChangeUserStyle(int? beatmapId, int? rulesetId)
        {
            var (room, _, user) = current();
            lock (room.Lock)
            {
                user.BeatmapId = beatmapId;
                user.RulesetId = rulesetId;
            }

            await Clients.Group(group(room.Id)).UserStyleChanged(user.UserID, beatmapId, rulesetId);
        }

        public async Task ChangeUserMods(IEnumerable<APIMod> newMods)
        {
            var (room, live, user) = current();
            var item = live.Playlist.FirstOrDefault(p => p.ID == live.Settings.PlaylistItemId);
            var allowed = item?.AllowedMods.Select(m => m.Acronym).ToHashSet() ?? new HashSet<string>();
            var mods = newMods.Where(m => item?.Freestyle == true || allowed.Contains(m.Acronym)).ToList();
            lock (room.Lock) user.Mods = mods;
            await Clients.Group(group(room.Id)).UserModsChanged(user.UserID, mods);
        }

        public async Task SendMatchRequest(MatchUserRequest request)
        {
            var (room, live, user) = current();

            switch (request)
            {
                case ChangeTeamRequest changeTeam when live.MatchState is TeamVersusRoomState tv:
                    if (tv.Teams.All(t => t.ID != changeTeam.TeamID)) throw new InvalidStateException("Invalid team.");
                    var state = new TeamVersusUserState { TeamID = changeTeam.TeamID };
                    lock (room.Lock) user.MatchState = state;
                    await Clients.Group(group(room.Id)).MatchUserStateChanged(user.UserID, state);
                    break;

                case StartMatchCountdownRequest countdown:
                    if (live.Host?.UserID != user.UserID) throw new NotHostException();
                    await startCountdown(room, countdown.Duration);
                    break;

                case StopCountdownRequest stop:
                    if (live.Host?.UserID != user.UserID) throw new NotHostException();
                    await stopCountdown(room, stop.ID);
                    break;

                case MatchmakingAvatarActionRequest avatar when live.Settings.MatchType == MatchType.Matchmaking:
                    await Clients.Group(group(room.Id)).MatchEvent(new MatchmakingAvatarActionEvent { UserId = user.UserID, Action = avatar.Action });
                    break;

                case RankedPlayCardHandReplayRequest replay when live.Settings.MatchType == MatchType.RankedPlay:
                    await Clients.OthersInGroup(group(room.Id)).MatchEvent(new RankedPlayCardHandReplayEvent { UserId = user.UserID, Frames = replay.Frames });
                    break;

                case RollRequest roll:
                    uint max = Math.Clamp(roll.Max ?? 100, 1u, 100_000_000u);
                    await Clients.Group(group(room.Id)).MatchEvent(new RollEvent { UserID = user.UserID, Max = max, Result = (uint)Random.Shared.NextInt64(1, max + 1L) });
                    break;

                case SetLockStateRequest lockState when live.MatchState is StandardMatchRoomState standard:
                    if (live.Host?.UserID != user.UserID) throw new NotHostException();
                    lock (room.Lock) standard.Locked = lockState.Locked;
                    await Clients.Group(group(room.Id)).MatchRoomStateChanged(standard);
                    break;
            }
        }

        private static int countdownId;

        private async Task startCountdown(ServerRoom room, TimeSpan duration)
        {
            room.Countdown?.Cancel();
            var cts = room.Countdown = new CancellationTokenSource();
            var countdown = new MatchStartCountdown { ID = Interlocked.Increment(ref countdownId), TimeRemaining = duration };

            lock (room.Lock)
            {
                room.Live!.ActiveCountdowns.Clear();
                room.Live.ActiveCountdowns.Add(countdown);
            }

            await Clients.Group(group(room.Id)).MatchEvent(new CountdownStartedEvent(countdown));

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(duration, cts.Token); }
                catch (TaskCanceledException) { return; }

                lock (room.Lock) room.Live!.ActiveCountdowns.Remove(countdown);
                await hubContext.Clients.Group(group(room.Id)).MatchEvent(new CountdownStoppedEvent(countdown.ID));
                await startMatch(room, force: true);
            });
        }

        private async Task stopCountdown(ServerRoom room, int id)
        {
            room.Countdown?.Cancel();
            lock (room.Lock) room.Live!.ActiveCountdowns.Clear();
            await hubContext.Clients.Group(group(room.Id)).MatchEvent(new CountdownStoppedEvent(id));
        }

        private async Task maybeAutoStart(ServerRoom room)
        {
            var live = room.Live!;
            if (!live.Settings.AutoStartEnabled || live.State != MultiplayerRoomState.Open) return;
            if (live.ActiveCountdowns.Any()) return;
            if (live.Users.Any(u => u.State == MultiplayerUserState.Ready))
                await startCountdown(room, live.Settings.AutoStartDuration);
        }

        public async Task StartMatch()
        {
            var (room, live, _) = currentAsHost();
            if (live.Settings.MatchType.IsMatchmakingType()) throw new InvalidStateException("The matchmaker starts this match.");
            if (live.State != MultiplayerRoomState.Open) throw new InvalidStateException("Can't start match when already in a running state.");
            if (live.Host!.State != MultiplayerUserState.Ready && live.Host.State != MultiplayerUserState.Spectating)
                throw new InvalidStateException("Can't start match when the host is not ready.");
            if (live.Users.All(u => u.State != MultiplayerUserState.Ready))
                throw new InvalidStateException("Can't start match when no users are ready.");
            await startMatch(room, force: false);
        }

        internal async Task startMatch(ServerRoom room, bool force)
        {
            var live = room.Live!;
            var clients = hubContext.Clients;
            List<MultiplayerRoomUser> players;

            lock (room.Lock)
            {
                if (live.State != MultiplayerRoomState.Open) return;
                players = live.Users.Where(u => u.State == MultiplayerUserState.Ready).ToList();
                if (players.Count == 0) return;

                // claim the start atomically so concurrent callers (countdown, matchmaker, host) can't start twice.
                foreach (var u in players) u.State = MultiplayerUserState.WaitingForLoad;
                live.State = MultiplayerRoomState.WaitingForLoad;
            }

            room.Countdown?.Cancel();
            var roomClients = hubContext.Clients.Group(group(room.Id));
            foreach (var u in players)
                await roomClients.UserStateChanged(u.UserID, MultiplayerUserState.WaitingForLoad);
            await roomClients.RoomStateChanged(MultiplayerRoomState.WaitingForLoad);

            foreach (var u in live.Users.Where(u => u.State is MultiplayerUserState.WaitingForLoad or MultiplayerUserState.Spectating))
                if (user_connections.TryGetValue(u.UserID, out string? conn))
                    await clients.Client(conn).LoadRequested();

            // don't let one stuck client block everyone: force-start after 30 seconds.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                if (live.State != MultiplayerRoomState.WaitingForLoad) return;
                foreach (var u in live.Users.Where(u => u.State is MultiplayerUserState.WaitingForLoad or MultiplayerUserState.Loaded).ToList())
                {
                    await setState(room, u, MultiplayerUserState.Idle);
                    if (user_connections.TryGetValue(u.UserID, out string? conn))
                        await clients.Client(conn).GameplayAborted(GameplayAbortReason.LoadTookTooLong);
                }

                await updateGameplayProgress(room);
            });

            await store.SyncJson(room, users, accounts);
        }

        public async Task AbortMatch()
        {
            var (room, live, _) = currentAsHost();
            if (live.Settings.MatchType.IsMatchmakingType()) throw new InvalidStateException("Not available in this room.");
            if (live.State is not (MultiplayerRoomState.WaitingForLoad or MultiplayerRoomState.Playing))
                throw new InvalidStateException("Can't abort a match that hasn't started.");

            foreach (var u in live.Users.Where(u => u.State is MultiplayerUserState.WaitingForLoad or MultiplayerUserState.Loaded or MultiplayerUserState.ReadyForGameplay
                                                        or MultiplayerUserState.Playing or MultiplayerUserState.FinishedPlay).ToList())
            {
                await setState(room, u, MultiplayerUserState.Idle);
                if (user_connections.TryGetValue(u.UserID, out string? conn))
                    await Clients.Client(conn).GameplayAborted(GameplayAbortReason.HostAbortedTheMatch);
            }

            await setRoomState(room, MultiplayerRoomState.Open);
            await store.SyncJson(room, users, accounts);
        }

        public async Task AbortGameplay()
        {
            var (room, _, user) = current();
            if (user.State is not (MultiplayerUserState.WaitingForLoad or MultiplayerUserState.Loaded or MultiplayerUserState.ReadyForGameplay or MultiplayerUserState.Playing))
                throw new InvalidStateException("Cannot abort gameplay while not in a gameplay state");

            await setState(room, user, MultiplayerUserState.Idle);
            await updateGameplayProgress(room);
        }

        public async Task VoteToSkipIntro()
        {
            var (room, live, user) = current();
            if (user.State != MultiplayerUserState.Playing || user.VotedToSkipIntro) return;
            lock (room.Lock) user.VotedToSkipIntro = true;
            await Clients.Group(group(room.Id)).UserVotedToSkipIntro(user.UserID, true);

            var playing = live.Users.Where(u => u.State == MultiplayerUserState.Playing).ToList();
            if (playing.Count(u => u.VotedToSkipIntro) * 2 > playing.Count || playing.All(u => u.VotedToSkipIntro))
                await Clients.Group(group(room.Id)).VoteToSkipIntroPassed();
        }

        // ------------------------------------------------------------------ playlist

        private async Task fillStarRating(MultiplayerPlaylistItem item)
        {
            if (item.BeatmapID <= 0 || Performance.Ruleset(item.RulesetID) == null
                || item.BeatmapChecksum.Length > 128 || item.RequiredMods.Take(33).Count() > 32 || item.AllowedMods.Take(65).Count() > 64)
                throw new HubException("Invalid playlist item.");
            var map = await mirrors.GetBeatmap(item.BeatmapID, null, null, Context.ConnectionAborted);
            if (map == null) throw new HubException("Beatmap not found on the mirrors.");
            item.StarRating = map.Value<double?>("difficulty_rating") ?? 0;
            if (string.IsNullOrEmpty(item.BeatmapChecksum)) item.BeatmapChecksum = map.Value<string>("checksum") ?? "";
        }

        public async Task AddPlaylistItem(MultiplayerPlaylistItem item)
        {
            var (room, live, user) = current();
            if (live.Settings.MatchType.IsMatchmakingType()) throw new InvalidStateException("Not available in this room.");
            if (live.Settings.QueueMode == QueueMode.HostOnly)
            {
                if (live.Host?.UserID != user.UserID) throw new NotHostException();
                throw new InvalidStateException("Host-only rooms edit the current item instead of adding new ones.");
            }

            if (live.Playlist.Count(p => !p.Expired && p.OwnerID == user.UserID) >= 3 && live.Host?.UserID != user.UserID)
                throw new InvalidStateException("You can only queue three items at a time.");

            if (live.Playlist.Count(p => !p.Expired) >= 100) throw new HubException("Playlist too large.");
            await fillStarRating(item);
            lock (room.Lock)
            {
                if (live.Playlist.Count(p => !p.Expired) >= 100) throw new HubException("Playlist too large.");
                if (live.Host?.UserID != user.UserID && live.Playlist.Count(p => !p.Expired && p.OwnerID == user.UserID) >= 3)
                    throw new InvalidStateException("You can only queue three items at a time.");
                item.ID = store.NextPlaylistItemId();
                item.OwnerID = user.UserID;
                item.Expired = false;
                item.PlayedAt = null;
                item.PlaylistOrder = (ushort)(live.Playlist.Select(p => (int)p.PlaylistOrder).DefaultIfEmpty(-1).Max() + 1);
                live.Playlist.Add(item);
            }

            await Clients.Group(group(room.Id)).PlaylistItemAdded(item);

            if (live.Playlist.FirstOrDefault(p => p.ID == live.Settings.PlaylistItemId) is { Expired: true })
            {
                lock (room.Lock) live.Settings.PlaylistItemId = nextItem(live)!.ID;
                await Clients.Group(group(room.Id)).SettingsChanged(live.Settings);
            }

            await store.SyncJson(room, users, accounts);
        }

        public async Task EditPlaylistItem(MultiplayerPlaylistItem item)
        {
            var (room, live, user) = current();
            if (live.Settings.MatchType.IsMatchmakingType()) throw new InvalidStateException("Not available in this room.");
            var existing = live.Playlist.FirstOrDefault(p => p.ID == item.ID) ?? throw new InvalidStateException("Item not found.");
            if (existing.Expired) throw new InvalidStateException("Can't edit an already played item.");
            if (existing.OwnerID != user.UserID && live.Host?.UserID != user.UserID) throw new NotHostException();

            await fillStarRating(item);
            lock (room.Lock)
            {
                existing.BeatmapID = item.BeatmapID;
                existing.BeatmapChecksum = item.BeatmapChecksum;
                existing.RulesetID = item.RulesetID;
                existing.RequiredMods = item.RequiredMods;
                existing.AllowedMods = item.AllowedMods;
                existing.StarRating = item.StarRating;
                existing.Freestyle = item.Freestyle;
                foreach (var u in live.Users) u.BeatmapAvailability = BeatmapAvailability.Unknown();
            }

            await Clients.Group(group(room.Id)).PlaylistItemChanged(existing);

            // reset readiness: the beatmap changed under everyone's feet
            foreach (var u in live.Users.Where(u => u.State == MultiplayerUserState.Ready).ToList())
                await setState(room, u, MultiplayerUserState.Idle);

            await store.SyncJson(room, users, accounts);
        }

        public async Task RemovePlaylistItem(long playlistItemId)
        {
            var (room, live, user) = current();
            if (live.Settings.MatchType.IsMatchmakingType()) throw new InvalidStateException("Not available in this room.");
            var existing = live.Playlist.FirstOrDefault(p => p.ID == playlistItemId) ?? throw new InvalidStateException("Item not found.");
            if (existing.OwnerID != user.UserID && live.Host?.UserID != user.UserID) throw new NotHostException();
            if (existing.Expired) throw new InvalidStateException("Can't remove an already played item.");
            if (existing.ID == live.Settings.PlaylistItemId && live.State != MultiplayerRoomState.Open)
                throw new InvalidStateException("Can't remove the current item during gameplay.");
            if (live.Playlist.Count(p => !p.Expired) <= 1) throw new InvalidStateException("The playlist can't be empty.");

            lock (room.Lock) live.Playlist.Remove(existing);
            await Clients.Group(group(room.Id)).PlaylistItemRemoved(playlistItemId);

            if (existing.ID == live.Settings.PlaylistItemId)
            {
                lock (room.Lock) live.Settings.PlaylistItemId = nextItem(live)!.ID;
                await Clients.Group(group(room.Id)).SettingsChanged(live.Settings);
            }

            await store.SyncJson(room, users, accounts);
        }

        public async Task InvitePlayer(int userId)
        {
            var (room, live, user) = current();
            if (!user_connections.TryGetValue(userId, out string? conn))
                throw new HubException("That user isn't online in multiplayer right now.");
            await Clients.Client(conn).Invited(user.UserID, room.Id, live.Settings.Password);
        }

        // ------------------------------------------------------------------ quick play / ranked play (see Services/Matchmaking.cs)

        public Task<MatchmakingPool[]> GetMatchmakingPoolsOfType(MatchmakingPoolType type) => Task.FromResult(Matchmaking.POOLS.Where(p => p.Type == type).ToArray());

        public async Task<MatchmakingJoinLobbyResponse> MatchmakingJoinLobbyWithParams(MatchmakingJoinLobbyRequest request)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Matchmaking.LobbyGroup(request.PoolId));
            await matchmaking.JoinLobby(me, request.PoolId);
            return new MatchmakingJoinLobbyResponse();
        }

        public async Task MatchmakingJoinLobby()
        {
            foreach (var pool in Matchmaking.POOLS)
                await Groups.AddToGroupAsync(Context.ConnectionId, Matchmaking.LobbyGroup(pool.Id));
            await matchmaking.JoinLobby(me, Matchmaking.POOLS[0].Id);
        }

        public async Task MatchmakingLeaveLobby()
        {
            foreach (var pool in Matchmaking.POOLS)
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, Matchmaking.LobbyGroup(pool.Id));
        }

        public async Task MatchmakingJoinQueue(int poolId)
        {
            int u = me;
            if (user_rooms.ContainsKey(u)) await leave(u, false);
            await matchmaking.JoinQueue(u, poolId);
        }

        public Task MatchmakingLeaveQueue() => matchmaking.LeaveQueue(me);
        public Task MatchmakingAcceptInvitation() => matchmaking.RespondToInvitation(me, true);
        public Task MatchmakingDeclineInvitation() => matchmaking.RespondToInvitation(me, false);

        public async Task<MatchmakingIssueDuelResponse> MatchmakingIssueDuel(MatchmakingIssueDuelRequest request)
        {
            await matchmaking.IssueDuel(me, request.UserId, request.PoolId);
            return new MatchmakingIssueDuelResponse();
        }

        public async Task<MatchmakingAcceptDuelResponse> MatchmakingAcceptDuel(MatchmakingAcceptDuelRequest request)
        {
            await matchmaking.AcceptDuel(me, request.Id);
            return new MatchmakingAcceptDuelResponse();
        }

        public Task MatchmakingToggleSelection(long playlistItemId)
        {
            var (room, _, user) = current();
            return matchmaking.ToggleSelection(room, user.UserID, playlistItemId);
        }

        public Task MatchmakingSkipToNextStage()
        {
            var (room, _, user) = current();
            return accounts.Get(user.UserID)?.IsAdmin == true ? matchmaking.SkipStage(room) : Task.CompletedTask;
        }

        public Task DiscardCards(RankedPlayCardItem[] cards)
        {
            var (room, _, user) = current();
            return matchmaking.DiscardCards(room, user.UserID, cards);
        }

        public Task PlayCard(RankedPlayCardItem card)
        {
            var (room, _, user) = current();
            return matchmaking.PlayCard(room, user.UserID, card);
        }

        private static MatchRoomState createMatchState(MatchType type, byte? maxParticipants) => type == MatchType.TeamVersus
            ? TeamVersusRoomState.CreateDefault(maxParticipants)
            : StandardMatchRoomState.Create(maxParticipants);
    }
}
