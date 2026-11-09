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

        public Channel? Join(int channelId, int userId)
        {
            using var c = db.Open();
            var row = c.Query("SELECT id, name, description, type, pm_user_a, pm_user_b FROM chat_channels WHERE id = $i", mapChannel, ("$i", channelId)).FirstOrDefault();
            if (row == null) return null;
            if (row.Type == "PM" && row.A != userId && row.B != userId) return null;
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
                SELECT id, channel_id, sender_id, content, is_action, timestamp FROM chat_messages
                WHERE channel_id = $c AND id > $s ORDER BY id DESC LIMIT $l
                """, r => (r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4) != 0, r.GetString(5), ""),
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
                id = c.Insert("INSERT INTO chat_messages (channel_id, sender_id, content, is_action, timestamp) VALUES ($c, $s, $m, $a, $t)",
                    ("$c", channelId), ("$s", senderId), ("$m", content), ("$a", isAction ? 1 : 0), ("$t", ts));
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
            _ = broadcast(recipients, "chat.message.new", new { messages = new[] { msg }, users = new[] { msg.Sender } });
            return msg;
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
            set[ws] = new SemaphoreSlim(1, 1);
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
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            }
        }

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
                        await gate.WaitAsync();
                        try { await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None); }
                        finally { gate.Release(); }
                    }
                    catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
                    {
                        set.TryRemove(ws, out _);
                    }
                }
            }
        }
    }
}
