// xc-osu! server — beatmap listing search with osu-web semantics on top of the mirrors. xc softworks (kudasaixc)
//
// osu.direct (Meilisearch) is the search engine: good relevance, real sorting, offset paging, but only one status and
// one mode per query and no genre / language / extra / difficulty filters. Everything osu-web supports and the mirror
// doesn't is applied here:
//   * "Has leaderboard" / "Ranked" / "Pending & WIP" (several statuses) = one mirror stream per status, merged on the
//     sort key; the cursor carries every stream's offset.
//   * the osu-web query syntax (ar>9 stars>=6 length<3m keys=7 status=loved creator=xxx "exact title"...) is parsed,
//     the free text goes to the mirror, the filters are applied to each set (beatmap filters must all hold on the same
//     difficulty, like osu-web's nested query).
//   * genre, language, video/storyboard, nsfw, featured artists, spotlights, recommended difficulty, played, rank
//     achieved and favourites are answered locally.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;

namespace XcOsu.Server.Services
{
    public sealed class BeatmapSearch
    {
        public const int PAGE_SIZE = 50;
        private const int mirror_page = 100; // osu.direct's maximum "amount"
        private const int max_mirror_calls = 6; // osu.direct allows 120 requests/min (burst 25/5s)
        private static readonly TimeSpan time_budget = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan cache_ttl = TimeSpan.FromMinutes(5);

        private readonly HttpClient http;
        private readonly Mirrors mirrors;
        private readonly Db db;
        private readonly Users users;
        private readonly ILogger<BeatmapSearch> log;

        private readonly ConcurrentDictionary<string, (DateTime at, JArray sets)> cache = new();
        private DateTime osuDirectBlockedUntil;

        public BeatmapSearch(IHttpClientFactory factory, Mirrors mirrors, Db db, Users users, ILogger<BeatmapSearch> log)
        {
            http = factory.CreateClient("mirrors");
            this.mirrors = mirrors;
            this.db = db;
            this.users = users;
            this.log = log;
        }

        public sealed record Result(List<JObject> Sets, JObject? Cursor, int Total);

        public async Task<Result> Search(IQueryCollection query, int? viewer, CancellationToken ct)
        {
            var q = SearchQuery.Parse(query, viewer);

            if (q.Viewer != null && (q.Played != null || q.Ranks != null || q.Recommended))
                loadPersonal(q);

            if (q.Category == "favourites")
                return await searchFavourites(q, ct);

            // one mirror stream per status. Relevance streams only keep sets matching every word (see fill), in which case the
            // mirror's own tie-break (play count) is the order, so they merge on the play count.
            var streams = q.Statuses == null
                ? new List<Stream> { new Stream(null) }
                : q.Statuses.Select(s => new Stream(s)).ToList();
            var order = q.Sort.Field != null ? q.Sort : SortSpec.Parse("plays_desc", false);

            if (q.Offsets != null && q.Offsets.Length == streams.Count)
            {
                for (int i = 0; i < streams.Count; i++)
                    streams[i].Consumed = streams[i].Fetched = q.Offsets[i];
            }

            var results = new List<JObject>();
            var seen = new HashSet<int>();
            int calls = 0;
            var started = DateTime.UtcNow;

            while (results.Count < PAGE_SIZE)
            {
                var empty = streams.Where(s => s.Buffer.Count == 0 && !s.Exhausted).ToList();

                if (empty.Count > 0)
                {
                    if (calls + empty.Count > max_mirror_calls || DateTime.UtcNow - started > time_budget)
                        break;

                    calls += empty.Count;
                    await Task.WhenAll(empty.Select(s => fill(s, q, ct)));
                }

                var live = streams.Where(s => s.Buffer.Count > 0).ToList();
                if (live.Count == 0)
                    break;

                var next = live.Count == 1 ? live[0] : live.Aggregate((a, b) => order.Compare(a.Buffer.Peek(), b.Buffer.Peek()) <= 0 ? a : b);
                var set = next.Buffer.Dequeue();
                next.Consumed++;

                int id = set.Value<int?>("id") ?? 0;
                if (id != 0 && seen.Add(id) && q.Matches(set))
                    results.Add(set);
            }

            bool more = streams.Any(s => s.Buffer.Count > 0 || !s.Exhausted);
            int page = q.Page;
            var cursor = more
                ? new JObject { ["page"] = page + 1, ["offsets"] = string.Join('.', streams.Select(s => s.Consumed)) }
                : null;

            if (results.Count > 0)
                _ = Task.Run(() => mirrors.CacheSets(results));

            return new Result(results, cursor, page * PAGE_SIZE + results.Count + (more ? PAGE_SIZE : 0));
        }

        private sealed class Stream
        {
            public readonly int? Status;
            public readonly Queue<JObject> Buffer = new();
            public int Consumed; // sets handed out (persisted in the cursor)
            public int Fetched; // mirror offset of the next fetch
            public bool Exhausted;

            public Stream(int? status) => Status = status;
        }

        private async Task fill(Stream s, SearchQuery q, CancellationToken ct)
        {
            var batch = await fetch(q, s.Status, s.Fetched, ct);
            if (batch == null)
            {
                s.Exhausted = true;
                return;
            }

            s.Fetched += batch.Count;
            if (batch.Count < mirror_page)
                s.Exhausted = true;

            // Meilisearch drops words from the end of the query until it finds something, and ranks sets matching every
            // word first: once a whole page has no full match, the rest is only ever partial matches ("ultra far" results).
            var sets = batch.OfType<JObject>().ToList();
            if (q.KeywordTerms.Count > 0)
            {
                sets = sets.Where(q.MatchesKeywords).ToList();
                if (sets.Count < 5)
                    s.Exhausted = true; // the tail of the mirror's ranking is partial matches only
            }

            foreach (var set in sets)
                s.Buffer.Enqueue(set);
        }

        private async Task<JArray?> fetch(SearchQuery q, int? status, int offset, CancellationToken ct)
        {
            var qs = new List<string>
            {
                "q=" + Uri.EscapeDataString(q.Keywords),
                $"amount={mirror_page}",
                $"offset={offset}",
            };
            if (q.Sort.MirrorSort != null) qs.Add("sort=" + Uri.EscapeDataString(q.Sort.MirrorSort));
            if (status != null) qs.Add($"status={status}");
            if (q.Mode != null && !q.Converts) qs.Add($"mode={q.Mode}");

            string path = "/search?" + string.Join('&', qs);
            if (cache.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.at < cache_ttl)
                return hit.sets;

            foreach (string b in new[] { "https://osu.direct/api/v2", "https://catboy.best/api/v2" })
            {
                bool osuDirect = b.Contains("osu.direct");
                if (osuDirect && DateTime.UtcNow < osuDirectBlockedUntil)
                    continue;

                try
                {
                    using var res = await http.GetAsync(b + path, ct);

                    if (osuDirect)
                    {
                        // stay under the limit instead of hammering it into 429s (catboy ignores the sort order)
                        int? remaining = header(res, "x-ratelimit-burst-remaining");
                        int? reset = header(res, "x-ratelimit-reset") ?? header(res, "ratelimit-reset");
                        if ((int)res.StatusCode == 429 || header(res, "x-ratelimit-remaining") == 0)
                            osuDirectBlockedUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(reset ?? 10, 1, 60));
                        else if (remaining == 0)
                            osuDirectBlockedUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(header(res, "x-ratelimit-burst-reset") ?? 5, 1, 10));
                    }

                    if (!res.IsSuccessStatusCode)
                    {
                        log.LogWarning("search mirror {Base} answered {Status} for {Path}", b, (int)res.StatusCode, path);
                        continue;
                    }

                    var json = JToken.Parse(await res.Content.ReadAsStringAsync(ct));
                    var sets = json switch
                    {
                        JArray a => a,
                        JObject o when o["beatmapsets"] is JArray a => a,
                        _ => null,
                    };
                    if (sets == null) continue;

                    if (cache.Count > 400)
                    {
                        foreach (var k in cache.Where(kv => DateTime.UtcNow - kv.Value.at > cache_ttl).Select(kv => kv.Key).ToList())
                            cache.TryRemove(k, out _);
                        if (cache.Count > 400) cache.Clear();
                    }

                    cache[path] = (DateTime.UtcNow, sets);
                    return sets;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
                {
                    if (ct.IsCancellationRequested) throw;
                    log.LogWarning("search mirror {Base} failed: {Msg}", b, e.Message);
                }
            }

            return null;
        }

        private static int? header(HttpResponseMessage res, string name) =>
            res.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out int i) ? i : null;

        private async Task<Result> searchFavourites(SearchQuery q, CancellationToken ct)
        {
            if (q.Viewer == null)
                return new Result(new List<JObject>(), null, 0);

            List<int> ids;
            using (var c = db.Open())
                ids = c.Query("SELECT beatmapset_id FROM favourites WHERE user_id = $u ORDER BY rowid DESC", r => r.GetInt32(0), ("$u", q.Viewer));

            var sets = new JObject?[ids.Count];
            using (var gate = new SemaphoreSlim(8))
            {
                await Task.WhenAll(ids.Select(async (id, i) =>
                {
                    await gate.WaitAsync(ct);
                    try { sets[i] = await mirrors.GetBeatmapset(id, ct); }
                    finally { gate.Release(); }
                }));
            }

            var matching = sets.OfType<JObject>().Where(s => q.Matches(s, ignoreStatus: true)).ToList();
            if (q.Sort.Field != null)
                matching.Sort(q.Sort.Compare); // List.Sort isn't stable, but ties don't matter here

            var page = matching.Skip(q.Page * PAGE_SIZE).Take(PAGE_SIZE).ToList();
            bool more = matching.Count > (q.Page + 1) * PAGE_SIZE;
            return new Result(page, more ? new JObject { ["page"] = q.Page + 1 } : null, matching.Count);
        }

        private void loadPersonal(SearchQuery q)
        {
            using var c = db.Open();
            int viewer = q.Viewer!.Value;

            if (q.Played != null)
                q.PlayedIds = c.Query("SELECT DISTINCT beatmap_id FROM beatmap_playcounts WHERE user_id = $u", r => r.GetInt32(0), ("$u", viewer)).ToHashSet();

            if (q.Ranks != null)
            {
                // best rank per beatmap (by score), like osu-web's "rank achieved"
                q.RankByBeatmap = new Dictionary<int, string>();
                foreach (var (beatmap, rank) in c.Query("SELECT beatmap_id, rank, MAX(total_score) FROM scores WHERE user_id = $u AND passed = 1" + (q.Mode != null ? " AND ruleset_id = $r" : "") + " GROUP BY beatmap_id",
                             r => (r.GetInt32(0), r.GetString(1)), ("$u", viewer), ("$r", q.Mode ?? 0)))
                    q.RankByBeatmap[beatmap] = rank;
            }

            if (q.Recommended)
            {
                double pp = (double)(users.Statistics(viewer, q.Mode ?? 0).PP ?? 0);
                q.RecommendedStars = pp > 0 ? Math.Pow(pp, 0.4) * 0.195 : 1.0;
            }
        }
    }

    /// <summary>A parsed beatmap listing request.</summary>
    public sealed class SearchQuery
    {
        public string Keywords = string.Empty;
        public int? Mode;
        public bool Converts;
        public string Category = "leaderboard";
        public int[]? Statuses;
        public SortSpec Sort = null!;
        public int? Genre, Language;
        public bool Video, Storyboard, FeaturedArtists, Spotlights, Recommended, Nsfw;
        public string? Played;
        public HashSet<string>? Ranks;
        public int Page;
        public int[]? Offsets;
        public int? Viewer;

        public HashSet<int>? PlayedIds;
        public Dictionary<int, string>? RankByBeatmap;
        public double? RecommendedStars;

        private readonly List<Func<JObject, bool>> setFilters = new();
        private readonly List<Func<JObject, bool>> beatmapFilters = new();

        // osu-web BeatmapsetQueryParser: key, operator (: = > < >= <= >: <:), value (quoted or not)
        private static readonly Regex filter_regex = new(@"(?<=^|\s)(?<key>[a-z_]+)(?<op>>=|<=|>:|<:|:|=|>|<)(?<value>""[^""]*""|\S*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static SearchQuery Parse(IQueryCollection query, int? viewer)
        {
            var q = new SearchQuery { Viewer = viewer };

            if (int.TryParse(query["m"], out int m) && m is >= 0 and <= 3) q.Mode = m;

            foreach (string c in query["c"].ToString().Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (c)
                {
                    case "converts": q.Converts = true; break;
                    case "featured_artists": q.FeaturedArtists = true; break;
                    case "spotlights": q.Spotlights = true; break;
                    case "recommended": q.Recommended = viewer != null; break;
                    // "follows" (subscribed mappers): xc-osu! has no mapper subscriptions.
                }
            }

            foreach (string e in query["e"].ToString().Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (e == "video") q.Video = true;
                else if (e == "storyboard") q.Storyboard = true;
            }

            if (int.TryParse(query["g"], out int g) && g > 0) q.Genre = g;
            if (int.TryParse(query["l"], out int l) && l > 0) q.Language = l;
            q.Nsfw = query["nsfw"].ToString() is "true" or "1";

            if (query["played"].ToString() is "played" or "unplayed" && viewer != null) q.Played = query["played"].ToString();

            string r = query["r"].ToString();
            if (r.Length > 0 && viewer != null) q.Ranks = r.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToUpperInvariant()).ToHashSet();

            string s = query["s"].ToString();
            q.Category = s.Length > 0 ? s : "leaderboard";
            q.Statuses = statusesFor(q.Category);

            if (int.TryParse(query["cursor[page]"], out int page) || int.TryParse(query["cursor_string"], out page)) q.Page = Math.Max(0, page);
            string offsets = query["cursor[offsets]"].ToString();
            if (offsets.Length > 0)
            {
                var parts = offsets.Split('.').Select(x => int.TryParse(x, out int v) ? v : -1).ToArray();
                if (parts.All(v => v >= 0)) q.Offsets = parts;
            }

            q.parseText(query["q"].ToString());

            string sort = query["sort"].ToString();
            if (sort.Length == 0) sort = q.Keywords.Length > 0 ? "relevance_desc" : q.Category is "pending" or "wip" or "graveyard" or "any" ? "updated_desc" : "ranked_desc";
            q.Sort = SortSpec.Parse(sort, q.Keywords.Length > 0);

            return q;
        }

        private static int[]? statusesFor(string category) => category switch
        {
            "any" => null,
            "ranked" => new[] { 1, 2 },
            "qualified" => new[] { 3 },
            "loved" => new[] { 4 },
            "pending" => new[] { 0, -1 },
            "wip" => new[] { -1 },
            "graveyard" => new[] { -2 },
            "favourites" or "mine" => null,
            _ => new[] { 1, 2, 3, 4 }, // leaderboard (default)
        };

        private void parseText(string text)
        {
            string rest = filter_regex.Replace(text, match =>
            {
                string key = match.Groups["key"].Value.ToLowerInvariant();
                string op = match.Groups["op"].Value.Replace(':', '='); // ">:" -> ">=", "<:" -> "<=", ":" -> "="
                string raw = match.Groups["value"].Value;
                bool quoted = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"';
                string value = quoted ? raw[1..^1] : raw;
                return addFilter(key, op, value, quoted) ? " " : match.Value;
            });

            Keywords = Regex.Replace(rest, @"\s+", " ").Trim();

            foreach (Match m in Regex.Matches(Keywords, @"""(?<p>[^""]+)""|(?<w>\S+)"))
            {
                string term = (m.Groups["p"].Success ? m.Groups["p"].Value : m.Groups["w"].Value).Trim().ToLowerInvariant();
                term = Regex.Replace(term, @"^[\p{P}\p{S}]+|[\p{P}\p{S}]+$", "");
                if (term.Length > 0) KeywordTerms.Add((term, m.Groups["p"].Success));
            }
        }

        public readonly List<(string term, bool phrase)> KeywordTerms = new();

        /// <summary>Every query word appears in the set's text (Meilisearch-like typo tolerance: 1 typo from 5 letters, 2 from 9).</summary>
        public bool MatchesKeywords(JObject set)
        {
            if (KeywordTerms.Count == 0) return true;
            string text = TextOf(set).ToLowerInvariant();
            string[]? tokens = null;

            foreach (var (term, phrase) in KeywordTerms)
            {
                if (text.Contains(term)) continue;
                if (phrase || term.Length < 5) return false;

                tokens ??= Regex.Split(text, @"[^\p{L}\p{N}]+").Where(t => t.Length > 0).ToArray();
                int typos = term.Length >= 9 ? 2 : 1;
                if (!tokens.Any(t => Math.Abs(t.Length - term.Length) <= typos && levenshtein(t, term, typos) <= typos)
                    && !tokens.Any(t => t.Length > term.Length && levenshtein(t[..term.Length], term, typos) <= typos)) // prefix ("freedo" ~ "freedom")
                    return false;
            }

            return true;
        }

        private static int levenshtein(string a, string b, int max)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                int best = cur[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    best = Math.Min(best, cur[j]);
                }

                if (best > max) return max + 1;
                (prev, cur) = (cur, prev);
            }

            return prev[b.Length];
        }

        private bool addFilter(string key, string op, string value, bool quoted)
        {
            switch (key)
            {
                case "star":
                case "stars":
                case "sr":
                    return number(value, out double sr) && beatmap("difficulty_rating", op, sr, 0.01);

                case "ar":
                    return number(value, out double ar) && beatmap("ar", op, ar, 0.1);

                case "cs":
                    return number(value, out double cs) && beatmap("cs", op, cs, 0.1);

                case "od":
                case "accuracy":
                    return number(value, out double od) && beatmap("accuracy", op, od, 0.1);

                case "hp":
                case "drain":
                    return number(value, out double hp) && beatmap("drain", op, hp, 0.1);

                case "bpm":
                    return number(value, out double bpm) && beatmap("bpm", op, bpm, 0.01);

                case "keys":
                    if (!number(value, out double keys)) return false;
                    beatmapFilters.Add(b => b.Value<int?>("mode_int") == 3 && compare(b.Value<double?>("cs"), op, keys, 0.5));
                    return true;

                case "length":
                    return length(value, out double seconds, out double unit) && beatmap("total_length", op, seconds, unit);

                case "status":
                    int[]? st = value.ToLowerInvariant() switch
                    {
                        "r" or "ranked" => new[] { 1, 2 },
                        "a" or "approved" => new[] { 2 },
                        "q" or "qualified" => new[] { 3 },
                        "l" or "loved" => new[] { 4 },
                        "p" or "pending" => new[] { 0 },
                        "w" or "wip" => new[] { -1 },
                        "g" or "graveyard" => new[] { -2 },
                        _ => null,
                    };
                    if (st == null) return false;
                    if (Category != "favourites") Statuses = st;
                    else setFilters.Add(s => st.Contains(Scores.BeatmapStatus(s)));
                    return true;

                case "creator":
                    setFilters.Add(s => text(s, quoted, value, "creator"));
                    return true;

                case "artist":
                    setFilters.Add(s => text(s, quoted, value, "artist", "artist_unicode"));
                    return true;

                case "title":
                    setFilters.Add(s => text(s, quoted, value, "title", "title_unicode"));
                    return true;

                case "source":
                    setFilters.Add(s => text(s, quoted, value, "source"));
                    return true;

                case "tag":
                    setFilters.Add(s => text(s, quoted, value, "tags"));
                    return true;

                case "featured_artist":
                    setFilters.Add(s => s["track_id"] is { Type: not JTokenType.Null });
                    return true;

                case "ranked":
                case "created":
                case "updated":
                    if (!dateRange(value, out var from, out var to)) return false;
                    string field = key switch { "ranked" => "ranked_date", "created" => "submitted_date", _ => "last_updated" };
                    setFilters.Add(s => s.Value<string>(field) is string d && DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) && op switch
                    {
                        ">" => dt >= to,
                        ">=" => dt >= from,
                        "<" => dt < from,
                        "<=" => dt < to,
                        _ => dt >= from && dt < to,
                    });
                    return true;

                default:
                    return false;
            }
        }

        private bool beatmap(string field, string op, double value, double tolerance)
        {
            beatmapFilters.Add(b => compare(b.Value<double?>(field), op, value, tolerance));
            return true;
        }

        private static bool compare(double? actual, string op, double value, double tolerance)
        {
            if (actual == null) return false;
            double a = actual.Value;
            return op switch
            {
                ">" => a > value,
                ">=" => a >= value - 1e-9,
                "<" => a < value,
                "<=" => a <= value + 1e-9,
                _ => Math.Abs(a - value) < tolerance / 2,
            };
        }

        private static bool number(string value, out double result) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        private static bool length(string value, out double seconds, out double unit)
        {
            seconds = 0;
            unit = 1;
            var m = Regex.Match(value, @"^(\d+(?:\.\d+)?)(ms|s|m|h)?$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            (double scale, unit) = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => (0.001, 1.0),
                "m" => (60.0, 60.0),
                "h" => (3600.0, 3600.0),
                _ => (1.0, 1.0),
            };
            seconds = v * scale;
            return true;
        }

        private static bool dateRange(string value, out DateTime from, out DateTime to)
        {
            from = to = default;
            var m = Regex.Match(value, @"^(\d{4})(?:[-/.](\d{1,2}))?(?:[-/.](\d{1,2}))?$");
            if (!m.Success) return false;
            int y = int.Parse(m.Groups[1].Value);
            if (!m.Groups[2].Success)
            {
                from = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                to = from.AddYears(1);
                return true;
            }

            int mo = int.Parse(m.Groups[2].Value);
            if (mo is < 1 or > 12) return false;
            if (!m.Groups[3].Success)
            {
                from = new DateTime(y, mo, 1, 0, 0, 0, DateTimeKind.Utc);
                to = from.AddMonths(1);
                return true;
            }

            int d = int.Parse(m.Groups[3].Value);
            if (d < 1 || d > DateTime.DaysInMonth(y, mo)) return false;
            from = new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc);
            to = from.AddDays(1);
            return true;
        }

        private static bool text(JObject set, bool exact, string value, params string[] fields)
        {
            foreach (string f in fields)
            {
                string? v = set.Value<string>(f);
                if (string.IsNullOrEmpty(v)) continue;
                if (exact ? string.Equals(v, value, StringComparison.OrdinalIgnoreCase) || (f == "tags" && v.Split(' ').Contains(value, StringComparer.OrdinalIgnoreCase))
                        : v.Contains(value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static string TextOf(JObject s) => string.Join(' ', new[] { "artist", "artist_unicode", "title", "title_unicode", "creator", "source", "tags" }.Select(f => s.Value<string>(f)));

        /// <summary>Whether a set passes every filter the mirror couldn't apply.</summary>
        public bool Matches(JObject set, bool ignoreStatus = false)
        {
            if (!ignoreStatus && Statuses != null && !Statuses.Contains(Scores.BeatmapStatus(set))) return false;
            if (!MatchesKeywords(set)) return false;
            if (!Nsfw && set.Value<bool?>("nsfw") == true) return false;
            if (Genre != null && genreOf(set) != Genre) return false;
            if (Language != null && languageOf(set) != Language) return false;
            if (Video && set.Value<bool?>("video") != true) return false;
            if (Storyboard && set.Value<bool?>("storyboard") != true) return false;
            if (FeaturedArtists && set["track_id"] is null or { Type: JTokenType.Null }) return false;
            if (Spotlights && set.Value<bool?>("spotlight") != true) return false;

            foreach (var f in setFilters)
                if (!f(set)) return false;

            var maps = (set["beatmaps"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();

            if (Played != null && PlayedIds != null)
            {
                bool played = maps.Any(b => PlayedIds.Contains(b.Value<int>("id")));
                if (played != (Played == "played")) return false;
            }

            if (Ranks != null)
            {
                if (RankByBeatmap == null || !maps.Any(b => RankByBeatmap.TryGetValue(b.Value<int>("id"), out string? rank) && Ranks.Contains(rank)))
                    return false;
            }

            // difficulty-level filters: all must hold on one difficulty of the selected mode
            var candidates = Mode == null ? maps : maps.Where(b => b.Value<int?>("mode_int") == Mode || (Converts && Mode != 0 && b.Value<int?>("mode_int") == 0)).ToList();
            if (Mode != null && candidates.Count == 0) return false;

            if (beatmapFilters.Count > 0 || RecommendedStars != null)
            {
                return candidates.Any(b =>
                {
                    if (RecommendedStars is double rec && !compare(b.Value<double?>("difficulty_rating"), "=", rec, 1.0)) return false;
                    foreach (var f in beatmapFilters)
                        if (!f(b)) return false;
                    return true;
                });
            }

            return true;
        }

        private static int? genreOf(JObject s) => s.Value<int?>("genre_id") ?? (s["genre"] as JObject)?.Value<int?>("id");
        private static int? languageOf(JObject s) => s.Value<int?>("language_id") ?? (s["language"] as JObject)?.Value<int?>("id");
    }

    /// <summary>A listing sort ("plays_desc"), as the mirror's Meilisearch sort and as a local comparison (for merging streams).</summary>
    public sealed class SortSpec
    {
        public string? Field; // null = relevance
        public bool Descending;
        public string? MirrorSort;

        public static SortSpec Parse(string sort, bool hasKeywords)
        {
            int us = sort.LastIndexOf('_');
            string criteria = us > 0 ? sort[..us] : sort;
            bool desc = us <= 0 || sort[(us + 1)..] != "asc";

            string? field = criteria switch
            {
                "title" => "title",
                "artist" => "artist",
                "difficulty" => "beatmaps.difficulty_rating",
                "updated" => "last_updated",
                "ranked" => "ranked_date",
                "plays" => "play_count",
                "favourites" or "rating" or "nominations" => "favourite_count",
                "relevance" => null,
                _ => "ranked_date",
            };

            if (field == null && !hasKeywords)
            {
                field = "ranked_date";
                desc = true;
            }

            return new SortSpec
            {
                Field = field,
                Descending = desc,
                // relevance: Meilisearch's ranking rules come first, the play count only breaks ties -> popular maps first
                MirrorSort = field == null ? "play_count:desc" : $"{field}:{(desc ? "desc" : "asc")}",
            };
        }

        /// <summary>Negative when <paramref name="a"/> comes first. Missing values sort last in both directions (like Meilisearch).</summary>
        public int Compare(JObject a, JObject b)
        {
            if (Field == null) return 0;
            var ka = key(a);
            var kb = key(b);
            if (ka == null && kb == null) return 0;
            if (ka == null) return 1;
            if (kb == null) return -1;
            int c = ka is string sa && kb is string sb ? string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase) : ((IComparable)ka).CompareTo(kb);
            return Descending ? -c : c;
        }

        private object? key(JObject s)
        {
            switch (Field)
            {
                case "title":
                case "artist":
                    return s.Value<string>(Field);

                case "last_updated":
                case "ranked_date":
                    return s.Value<string>(Field) is string d && DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;

                case "beatmaps.difficulty_rating":
                    var stars = (s["beatmaps"] as JArray)?.Select(b => b.Value<double?>("difficulty_rating")).OfType<double>().ToList();
                    if (stars == null || stars.Count == 0) return null;
                    return Descending ? stars.Max() : stars.Min();

                default:
                    return s.Value<double?>(Field);
            }
        }
    }
}
