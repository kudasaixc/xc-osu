// xc-osu! server — chat channels, messages and the notification websocket. xc softworks (kudasaixc)

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Chat;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed class Chat
    {
        private readonly Db db;
        private readonly Accounts accounts;
        private readonly Users users;

        private readonly ConcurrentDictionary<int, ConcurrentDictionary<WebSocket, SemaphoreSlim>> sockets = new ConcurrentDictionary<int, ConcurrentDictionary<WebSocket, SemaphoreSlim>>();

        public Chat(Db db, Accounts accounts, Users users)
        {
            this.db = db;
            this.accounts = accounts;
            this.users = users;
        }

        private sealed record ChannelRow(int Id, string Name, string Description, string Type, int? A, int? B);

        private static ChannelRow mapChannel(Microsoft.Data.Sqlite.SqliteDataReader r) =>
            new ChannelRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5));

        private Channel toChannel(ChannelRow row, int viewer, Microsoft.Data.Sqlite.SqliteConnection c)
        {
            var ch = new Channel
            {
                Id = row.Id,
                Name = row.Name,
                Topic = row.Description,
                Type = Enum.TryParse(row.Type, true, out ChannelType t) ? t : ChannelType.Public,
                LastMessageId = c.Scalar<long?>("SELECT MAX(id) FROM chat_messages WHERE channel_id = $c", ("$c", row.Id)),
                LastReadId = c.Scalar<long?>("SELECT last_read_id FROM chat_members WHERE channel_id = $c AND user_id = $u", ("$c", row.Id), ("$u", viewer)),
            };

            if (row.Type == "PM")
            {
                int other = row.A == viewer ? row.B ?? 0 : row.A ?? 0;
                var o = accounts.Get(other);
                if (o != null)
                {
                    ch.Name = o.Username;
                    ch.Users.Add(users.Compact(o));
                }
            }

            return ch;
        }

        /// <summary>Channel JSON as osu-web sends it (osu!'s Channel model only reads "users", it never writes it).</summary>
        public static Newtonsoft.Json.Linq.JObject ToJson(Channel ch)
        {
            var j = Newtonsoft.Json.Linq.JObject.FromObject(ch, JsonSerializer.Create(Api.Json.SETTINGS));
            j["users"] = new Newtonsoft.Json.Linq.JArray(ch.Users.Select(u => u.Id));
            j["moderated"] = false;
            j["current_user_attributes"] = new Newtonsoft.Json.Linq.JObject { ["can_message"] = true, ["can_message_error"] = null, ["last_read_id"] = ch.LastReadId };
            return j;
        }

        /// <summary>
        /// Message JSON as osu-web sends it: "sender_id" first, the full "sender" last. osu!'s Message model replaces the
        /// sender by an id-only placeholder when "sender_id" is read after "sender", which blanked usernames in the history.
        /// </summary>
        public static Newtonsoft.Json.Linq.JObject ToJson(Message m)
        {
            var j = Newtonsoft.Json.Linq.JObject.FromObject(m, JsonSerializer.Create(Api.Json.SETTINGS));
            var sender = j["sender"];
            j.Remove("sender");
            j["sender"] = sender;
            return j;
        }

        public static Newtonsoft.Json.Linq.JArray ToJson(IEnumerable<Message> messages) => new Newtonsoft.Json.Linq.JArray(messages.Select(ToJson));

        public static Newtonsoft.Json.Linq.JArray ToJson(IEnumerable<Channel> channels) => new Newtonsoft.Json.Linq.JArray(channels.Select(ToJson));

        public List<Channel> PublicChannels(int viewer)
        {
            using var c = db.Open();
            return c.Query("SELECT id, name, description, type, pm_user_a, pm_user_b FROM chat_channels WHERE type = 'PUBLIC'", mapChannel)
                    .Select(r => toChannel(r, viewer, c)).ToList();
        }

        public List<Channel> JoinedChannels(int viewer)
        {
            using var c = db.Open();
            return c.Query("""
                SELECT c.id, c.name, c.description, c.type, c.pm_user_a, c.pm_user_b FROM chat_channels c
                JOIN chat_members m ON m.channel_id = c.id WHERE m.user_id = $u
                """, mapChannel, ("$u", viewer)).Select(r => toChannel(r, viewer, c)).ToList();
        }

        public Channel? Get(int channelId, int viewer)
        {
            using var c = db.Open();
            var row = c.Query("SELECT id, name, description, type, pm_user_a, pm_user_b FROM chat_channels WHERE id = $i", mapChannel, ("$i", channelId)).FirstOrDefault();
            return row == null ? null : toChannel(row, viewer, c);
        }

        public Channel? Join(int channelId, int userId, bool roomAuthorized = false)
        {
            using var c = db.Open();
            var row = c.Query("SELECT id, name, description, type, pm_user_a, pm_user_b FROM chat_channels WHERE id = $i", mapChannel, ("$i", channelId)).FirstOrDefault();
            if (row == null) return null;
            if (row.Type == "PM" && row.A != userId && row.B != userId) return null;
            if (row.Type == "MULTIPLAYER" && !roomAuthorized)
            {
                long? roomId = c.Scalar<long?>("SELECT room_id FROM chat_channels WHERE id = $c", ("$c", channelId));
                if (roomId == null) return null;
                string? category = c.Scalar<string>("SELECT category FROM rooms WHERE id = $r", ("$r", roomId));
                if (category == "realtime" ? Hubs.MultiplayerHub.RoomOf(userId) != roomId :
                    c.Scalar<long>("SELECT COUNT(*) FROM chat_members WHERE channel_id = $c AND user_id = $u", ("$c", channelId), ("$u", userId)) == 0)
                    return null;
            }
            c.Exec("INSERT OR IGNORE INTO chat_members (channel_id, user_id) VALUES ($c, $u)", ("$c", channelId), ("$u", userId));
            return toChannel(row, userId, c);
        }

        public void Leave(int channelId, int userId)
        {
            using var c = db.Open();
            c.Exec("DELETE FROM chat_members WHERE channel_id = $c AND user_id = $u", ("$c", channelId), ("$u", userId));
        }

        public void MarkRead(int channelId, int userId, long messageId)
        {
            using var c = db.Open();
            c.Exec("UPDATE chat_members SET last_read_id = MAX(last_read_id, $m) WHERE channel_id = $c AND user_id = $u", ("$m", messageId), ("$c", channelId), ("$u", userId));
        }

        public bool CanRead(int channelId, int userId)
        {
            using var c = db.Open();
            long? realtimeRoom = c.Scalar<long?>("SELECT ch.room_id FROM chat_channels ch JOIN rooms r ON r.id = ch.room_id WHERE ch.id = $c AND ch.type = 'MULTIPLAYER' AND r.category = 'realtime'", ("$c", channelId));
            if (realtimeRoom != null && Hubs.MultiplayerHub.RoomOf(userId) != realtimeRoom) return false;
            return c.Scalar<long>("""
                SELECT COUNT(*) FROM chat_channels WHERE id = $c AND (type = 'PUBLIC' OR pm_user_a = $u OR pm_user_b = $u
                    OR EXISTS (SELECT 1 FROM chat_members WHERE channel_id = $c AND user_id = $u))
                """, ("$c", channelId), ("$u", userId)) > 0;
        }

        private Message toMessage((long id, int channel, int sender, string content, bool action, string ts, string uuid) m, Dictionary<int, APIUser> cache)
        {
            if (!cache.TryGetValue(m.sender, out var u))
            {
                var row = accounts.Get(m.sender);
                cache[m.sender] = u = row != null ? users.Compact(row) : new APIUser { Id = m.sender, Username = "?" };
            }

            return new Message(m.id)
            {
                ChannelId = m.channel,
                Content = m.content,
                IsAction = m.action,
                Timestamp = DateTimeOffset.Parse(m.ts),
                Sender = u,
                Uuid = m.uuid,
            };
        }

        public List<Message> Messages(int channelId, long? since = null, int limit = 50)
        {
            using var c = db.Open();
            var rows = c.Query("""
                SELECT id, channel_id, sender_id, content, is_action, timestamp, uuid FROM chat_messages
                WHERE channel_id = $c AND id > $s ORDER BY id DESC LIMIT $l
                """, r => (r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4) != 0, r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6)),
                ("$c", channelId), ("$s", since ?? 0), ("$l", limit));
            rows.Reverse();
            var cache = new Dictionary<int, APIUser>();
            return rows.Select(m => toMessage(m, cache)).ToList();
        }

        public Message Post(int channelId, int senderId, string content, bool isAction, string? uuid)
        {
            content = content.Length > 1024 ? content[..1024] : content;
            string ts = DateTimeOffset.UtcNow.ToString("O");
            long id;
            List<int> recipients;
            using (var c = db.Open())
            {
                id = c.Insert("INSERT INTO chat_messages (channel_id, sender_id, content, is_action, timestamp, uuid) VALUES ($c, $s, $m, $a, $t, $g)",
                    ("$c", channelId), ("$s", senderId), ("$m", content), ("$a", isAction ? 1 : 0), ("$t", ts), ("$g", uuid));
                c.Exec("INSERT OR IGNORE INTO chat_members (channel_id, user_id) VALUES ($c, $u)", ("$c", channelId), ("$u", senderId));
                c.Exec("UPDATE chat_members SET last_read_id = $m WHERE channel_id = $c AND user_id = $u", ("$m", id), ("$c", channelId), ("$u", senderId));
                recipients = c.Query("SELECT user_id FROM chat_members WHERE channel_id = $c", r => r.GetInt32(0), ("$c", channelId));
                // PM: make sure the recipient sees the channel
                var pm = c.Query("SELECT pm_user_a, pm_user_b FROM chat_channels WHERE id = $c AND type = 'PM'", r => (r.GetInt32(0), r.GetInt32(1)), ("$c", channelId)).FirstOrDefault();
                if (pm != default)
                {
                    foreach (int u in new[] { pm.Item1, pm.Item2 })
                    {
                        c.Exec("INSERT OR IGNORE INTO chat_members (channel_id, user_id) VALUES ($c, $u)", ("$c", channelId), ("$u", u));
                        if (!recipients.Contains(u)) recipients.Add(u);
                    }
                }
            }

            var msg = toMessage((id, channelId, senderId, content, isAction, ts, uuid ?? ""), new Dictionary<int, APIUser>());
            recipients.RemoveAll(recipient => !CanRead(channelId, recipient));
            _ = broadcast(recipients, "chat.message.new", new { messages = ToJson(new[] { msg }), users = new[] { msg.Sender } });
            return msg;
        }

        /// <summary>Why a user may not post right now (silence / restriction / PM rules), or null if they can.</summary>
        public string? PostRestriction(int channelId, int senderId)
        {
            if (!CanRead(channelId, senderId)) return "You cannot access this channel.";
            if (!AbuseGuard.Allow($"chat-channel:{senderId}:{channelId}", 20, TimeSpan.FromSeconds(10)))
                return "Too many messages. Please wait.";
            var sender = accounts.Get(senderId);
            if (sender == null) return "unknown user";
            if (sender.Restricted) return "Your account is restricted.";
            if (accounts.NeedsEmailConfirmation(senderId)) return Accounts.CONFIRM_EMAIL_MESSAGE;
            if (sender.SilencedUntil is DateTimeOffset until && until > DateTimeOffset.UtcNow) return $"You are silenced until {until:u}.";

            using var c = db.Open();
            var pm = c.Query("SELECT pm_user_a, pm_user_b FROM chat_channels WHERE id = $c AND type = 'PM'", r => (r.GetInt32(0), r.GetInt32(1)), ("$c", channelId)).FirstOrDefault();
            if (pm != default)
            {
                int other = pm.Item1 == senderId ? pm.Item2 : pm.Item1;
                return PmRestriction(senderId, other);
            }

            if (c.Scalar<long>("SELECT moderated FROM chat_channels WHERE id = $c", ("$c", channelId)) == 1 && !sender.IsAdmin)
                return "This channel is moderated.";
            return null;
        }

        public string? PmRestriction(int sender, int target)
        {
            using var c = db.Open();
            if (c.Scalar<long>("SELECT COUNT(*) FROM blocks WHERE user_id = $t AND target_id = $u", ("$t", target), ("$u", sender)) > 0)
                return "You cannot message this user.";
            if (c.Scalar<long>("SELECT COUNT(*) FROM blocks WHERE user_id = $u AND target_id = $t", ("$t", target), ("$u", sender)) > 0)
                return "You have blocked this user.";
            var t = accounts.Get(target);
            if (t == null) return "User not found.";
            if (Newtonsoft.Json.Linq.JObject.Parse(t.ProfileJson).Value<bool?>("pm_friends_only") == true
                && c.Scalar<long>("SELECT COUNT(*) FROM friends WHERE user_id = $t AND target_id = $u", ("$t", target), ("$u", sender)) == 0)
                return "This user only accepts messages from friends.";
            return null;
        }

        /// <summary>Creates a channel bound to a multiplayer / playlists room.</summary>
        public int CreateRoomChannel(long roomId, string roomName)
        {
            using var c = db.Open();
            return (int)c.Insert("INSERT INTO chat_channels (name, description, type, room_id) VALUES ($n, $d, 'MULTIPLAYER', $r)",
                ("$n", $"#mp_{roomId}"), ("$d", roomName), ("$r", roomId));
        }

        public void RenameRoomChannel(int channelId, string roomName)
        {
            using var c = db.Open();
            c.Exec("UPDATE chat_channels SET description = $d WHERE id = $c", ("$d", roomName), ("$c", channelId));
        }

        /// <summary>Joins a user to a channel and tells their clients (chat.channel.join), like osu-web does for multiplayer rooms.</summary>
        public async Task JoinAndNotify(int channelId, int userId)
        {
            var ch = Join(channelId, userId, roomAuthorized: true);
            if (ch != null) await broadcast(new[] { userId }, "chat.channel.join", ToJson(ch));
        }

        public async Task PartAndNotify(int channelId, int userId)
        {
            var ch = Get(channelId, userId);
            Leave(channelId, userId);
            if (ch != null) await broadcast(new[] { userId }, "chat.channel.part", ToJson(ch));
        }

        public async Task CloseRoomChannel(int channelId)
        {
            List<int> members;
            using (var c = db.Open())
                members = c.Query("SELECT user_id FROM chat_members WHERE channel_id = $c", r => r.GetInt32(0), ("$c", channelId));
            foreach (int u in members)
                await PartAndNotify(channelId, u);
        }

        public int OpenPm(int a, int b)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            using var c = db.Open();
            long? existing = c.Scalar<long?>("SELECT id FROM chat_channels WHERE type = 'PM' AND pm_user_a = $a AND pm_user_b = $b", ("$a", lo), ("$b", hi));
            if (existing != null) return (int)existing;
            return (int)c.Insert("INSERT INTO chat_channels (name, description, type, pm_user_a, pm_user_b) VALUES ($n, '', 'PM', $a, $b)",
                ("$n", $"pm_{lo}-{hi}"), ("$a", lo), ("$b", hi));
        }

        // ------------------------------------------------------------ notification websocket

        public async Task Serve(WebSocket ws, int userId, CancellationToken ct)
        {
            var set = sockets.GetOrAdd(userId, _ => new ConcurrentDictionary<WebSocket, SemaphoreSlim>());
            lock (set)
            {
                if (set.Count >= 16) { ws.Abort(); return; }
                set[ws] = new SemaphoreSlim(1, 1);
            }
            var buffer = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    // chat.start / chat.end — nothing to do: every connected socket receives its user's chat events.
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            finally
            {
                set.TryRemove(ws, out _);
                if (ws.State == WebSocketState.Open)
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                    }
                    catch { ws.Abort(); }
            }
        }

        public Task SendEvent(IEnumerable<int> userIds, string evt, object data) => broadcast(userIds, evt, data);

        private async Task broadcast(IEnumerable<int> userIds, string evt, object data)
        {
            byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { @event = evt, data }, Api.Json.SETTINGS));
            foreach (int u in userIds.Distinct())
            {
                if (!sockets.TryGetValue(u, out var set)) continue;
                foreach (var (ws, gate) in set)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await gate.WaitAsync(timeout.Token);
                        try { await ws.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token); }
                        finally { gate.Release(); }
                    }
                    catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                    {
                        ws.Abort();
                        set.TryRemove(ws, out _);
                    }
                }
            }
        }
    }
}
