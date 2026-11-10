// xc-osu! server — medals (achievements) and private notifications. xc softworks (kudasaixc)

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Online.API.Requests.Responses;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed class Notifications
    {
        private readonly Db db;
        private readonly Chat chat;

        public Notifications(Db db, Chat chat)
        {
            this.db = db;
            this.chat = chat;
        }

        /// <summary>Stores a notification and pushes it live over the notification websocket ("new" event, osu-web shape).</summary>
        public async Task Push(int userId, string name, string objectType, long objectId, string category, int? sourceUserId, JObject details)
        {
            string now = DateTimeOffset.UtcNow.ToString("O");
            long id;
            using (var c = db.Open())
                id = c.Insert("""
                    INSERT INTO notifications (user_id, name, object_type, object_id, category, source_user_id, details, created_at) VALUES ($u, $n, $ot, $oi, $c, $s, $d, $t)
                    """, ("$u", userId), ("$n", name), ("$ot", objectType), ("$oi", objectId), ("$c", category), ("$s", sourceUserId),
                    ("$d", details.ToString(Formatting.None)), ("$t", now));

            await chat.SendEvent(new[] { userId }, "new", toJson(id, name, objectType, objectId, category, sourceUserId, details, now, false));
        }

        private static JObject toJson(long id, string name, string objectType, long objectId, string category, int? source, JObject details, string createdAt, bool read) => new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["created_at"] = createdAt,
            ["object_type"] = objectType,
            ["object_id"] = objectId,
            ["category"] = category,
            ["source_user_id"] = source,
            ["is_read"] = read,
            ["details"] = details,
        };

        public JObject List(int userId, string endpoint)
        {
            using var c = db.Open();
            var list = c.Query("""
                SELECT id, name, object_type, object_id, category, source_user_id, details, created_at, is_read FROM notifications
                WHERE user_id = $u ORDER BY id DESC LIMIT 50
                """, r => toJson(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5),
                JObject.Parse(r.GetString(6)), r.GetString(7), r.GetInt32(8) != 0), ("$u", userId));

            var stacks = list.GroupBy(n => (n.Value<string>("object_type"), n.Value<long>("object_id"), n.Value<string>("category"))).Select(g => new JObject
            {
                ["object_type"] = g.Key.Item1,
                ["object_id"] = g.Key.Item2,
                ["category"] = g.Key.Item3,
                ["total"] = g.Count(),
                ["cursor"] = null,
            });

            return new JObject
            {
                ["has_more"] = false,
                ["notifications"] = new JArray(list),
                ["unread_count"] = list.Count(n => !n.Value<bool>("is_read")),
                ["stacks"] = new JArray(stacks),
                ["types"] = new JArray(),
                ["timestamp"] = DateTimeOffset.UtcNow,
                ["notification_endpoint"] = endpoint,
            };
        }

        public void MarkRead(int userId, IEnumerable<long> ids)
        {
            using var c = db.Open();
            foreach (long id in ids)
                c.Exec("UPDATE notifications SET is_read = 1 WHERE id = $i AND user_id = $u", ("$i", id), ("$u", userId));
        }

        public void MarkAllRead(int userId)
        {
            using var c = db.Open();
            c.Exec("UPDATE notifications SET is_read = 1 WHERE user_id = $u", ("$u", userId));
        }
    }

    public sealed class Medals
    {
        public sealed record Medal(int Id, string Slug, string Name, string Description, string Grouping, string? Mode, int Ordering, string Instructions = "");

        public static readonly Medal[] ALL = build();

        private static Medal[] build()
        {
            var list = new List<Medal>();
            string[] pass = { "Rising Star", "Constellation Prize", "Building Confidence", "Insanity Approaches", "These Clarion Skies", "Above and Beyond", "Supremacy", "Absolution", "Event Horizon", "Phantasm" };
            string[] fc = { "Totality", "Business As Usual", "Building Steam", "Moving Forward", "Paradigm Shift", "Anguish Quelled", "Never Give Up", "Aberration", "Chosen", "Unfathomable" };
            for (int i = 0; i < 10; i++)
            {
                list.Add(new Medal(1 + i, $"osu-skill-pass-{i + 1}", pass[i], $"Pass a {i + 1}* beatmap.", "Skill", "osu", i));
                list.Add(new Medal(11 + i, $"osu-skill-fc-{i + 1}", fc[i], $"Full combo a {i + 1}* beatmap.", "Skill", "osu", 10 + i));
            }

            int[] plays = { 5000, 15000, 25000, 50000 };
            string[] playNames = { "5,000 Plays", "15,000 Plays", "25,000 Plays", "50,000 Plays" };
            for (int i = 0; i < plays.Length; i++)
                list.Add(new Medal(21 + i, $"osu-plays-{plays[i]}", playNames[i], $"{plays[i]:N0} plays and counting.", "Dedication", "osu", 20 + i));

            int[] combos = { 500, 750, 1000, 2000 };
            for (int i = 0; i < combos.Length; i++)
                list.Add(new Medal(25 + i, $"osu-combo-{combos[i]}", $"{combos[i]:N0} Combo", $"{combos[i]:N0} big ones! You're moving up in the world!", "Skill", "osu", 24 + i));

            list.Add(new Medal(29, "all-intro-doubletime", "Time And A Half", "Having a right ol' time. One and a half of them, almost.", "Mod Introduction", null, 40));
            list.Add(new Medal(30, "all-intro-hidden", "Blindsight", "I can see just perfectly.", "Mod Introduction", null, 41));
            list.Add(new Medal(31, "all-intro-hardrock", "Rock Around The Clock", "You can't stop the rock.", "Mod Introduction", null, 42));
            list.Add(new Medal(32, "all-intro-flashlight", "Are You Afraid Of The Dark?", "Harder than it looks, probably because it's hard to look.", "Mod Introduction", null, 43));
            list.Add(new Medal(33, "all-intro-easy", "Dial It Right Back", "Sometimes you just want to take it easy.", "Mod Introduction", null, 44));
            list.Add(new Medal(34, "all-intro-nofail", "Risk Averse", "Safety nets are fun!", "Mod Introduction", null, 45));
            list.Add(new Medal(35, "all-intro-halftime", "Slowboat", "You got there. Eventually.", "Mod Introduction", null, 46));
            list.Add(new Medal(36, "all-intro-suddendeath", "Finality", "High stakes, no regrets.", "Mod Introduction", null, 47));
            list.Add(new Medal(37, "all-intro-perfect", "Perfectionist", "Accept nothing but the best.", "Mod Introduction", null, 48));
            list.Add(new Medal(38, "all-secret-dailychallenge", "Daily Grind", "Play your first daily challenge.", "Hush-Hush", null, 60));
            list.Add(new Medal(39, "all-secret-multiplayer", "Better Together", "Finish a multiplayer match.", "Hush-Hush", null, 61));
            return list.ToArray();
        }

        private static readonly Dictionary<string, int> mod_intro = new Dictionary<string, int>
        {
            ["DT"] = 29, ["NC"] = 29, ["HD"] = 30, ["HR"] = 31, ["FL"] = 32, ["EZ"] = 33, ["NF"] = 34, ["HT"] = 35, ["DC"] = 35, ["SD"] = 36, ["PF"] = 37,
        };

        private readonly Db db;
        private readonly Accounts accounts;
        private readonly Notifications notifications;
        private readonly Scores scores;

        public Medals(Db db, Accounts accounts, Notifications notifications, Scores scores)
        {
            this.db = db;
            this.accounts = accounts;
            this.notifications = notifications;
            this.scores = scores;
        }

        public static string IconUrl(Medal m) => $"https://assets.ppy.sh/medals/web/{m.Slug}.png";

        /// <summary>Checks a freshly stored score for medal unlocks.</summary>
        public async Task Check(SoloScoreInfo score, double? starRating, int? beatmapMaxCombo, bool isDailyChallenge, bool isMultiplayer)
        {
            if (!score.Passed) return;
            var unlocked = new List<int>();
            bool ranked = score.Ranked;
            bool hasDifficultyReduction = score.Mods.Any(m => m.Acronym is "EZ" or "NF" or "HT" or "DC");

            if (ranked && starRating is double sr && score.RulesetID == 0 && !hasDifficultyReduction)
            {
                for (int i = 1; i <= 10; i++)
                {
                    if (sr < i || sr >= i + 1) continue;
                    unlocked.Add(i); // pass
                    if (beatmapMaxCombo != null && score.MaxCombo >= beatmapMaxCombo) unlocked.Add(10 + i); // full combo
                }
            }

            if (score.RulesetID == 0)
            {
                foreach ((int combo, int id) in new[] { (500, 25), (750, 26), (1000, 27), (2000, 28) })
                    if (score.MaxCombo >= combo) unlocked.Add(id);

                using var c = db.Open();
                long plays = c.Scalar<long>("SELECT COUNT(*) FROM scores WHERE user_id = $u AND ruleset_id = 0", ("$u", score.UserID));
                foreach ((int n, int id) in new[] { (5000, 21), (15000, 22), (25000, 23), (50000, 24) })
                    if (plays >= n) unlocked.Add(id);
            }

            if (ranked)
            {
                foreach (var mod in score.Mods)
                    if (mod_intro.TryGetValue(mod.Acronym, out int id))
                        unlocked.Add(id);
            }

            if (isDailyChallenge) unlocked.Add(38);
            if (isMultiplayer) unlocked.Add(39);

            foreach (int id in unlocked.Distinct())
                await Award(score.UserID, id, score.RulesetID);
        }

        public async Task<bool> Award(int userId, int medalId, int rulesetId)
        {
            var medal = ALL.FirstOrDefault(m => m.Id == medalId);
            if (medal == null) return false;

            using (var c = db.Open())
            {
                if (c.Exec("INSERT OR IGNORE INTO user_achievements (user_id, achievement_id, achieved_at) VALUES ($u, $a, $t)",
                        ("$u", userId), ("$a", medalId), ("$t", DateTimeOffset.UtcNow.ToString("O"))) == 0)
                    return false;

                scores.AddEvent(c, userId, "achievement", new JObject
                {
                    ["achievement"] = new JObject { ["slug"] = medal.Slug, ["name"] = medal.Name },
                    ["mode"] = medal.Mode,
                });
            }

            await notifications.Push(userId, "user_achievement_unlock", "achievement", medalId, "achievement", null, new JObject
            {
                ["achievement_id"] = medalId,
                ["achievement_mode"] = medal.Mode ?? Users.RULESET_NAMES[Math.Clamp(rulesetId, 0, 3)],
                ["cover_url"] = IconUrl(medal),
                ["slug"] = medal.Slug,
                ["title"] = medal.Name,
                ["description"] = medal.Description,
                ["user_id"] = userId,
            });

            return true;
        }
    }
}
