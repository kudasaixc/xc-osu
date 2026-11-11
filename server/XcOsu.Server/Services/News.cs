// xc-osu! — xc softworks (kudasaixc)
// Written content shipped with the server: news posts, wiki pages and the changelog (Content/ next to the binary).
using System.Text.RegularExpressions;
using Markdig;
using Newtonsoft.Json.Linq;

namespace XcOsu.Server.Services
{
    /// <summary>
    /// Content/news/2026-11-09-some-slug.en.md (+ .fr.md): first line "# Title", then Markdown. An optional "T1830" after the date orders posts of one day.
    /// Content/wiki/{en,fr}/Some/Page.md: Markdown, first heading is the title. Main_page.md holds the panels the game's wiki overlay expects.
    /// Content/changelog.json: builds, newest first.
    /// Files are read on every request: there are a few dozen of them and editing one must not need a restart.
    /// </summary>
    public static class News
    {
        private static readonly string root = Path.Combine(AppContext.BaseDirectory, "Content");
        private static readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseAutoLinks().DisableHtml().Build();

        /// <summary>"fr" or "en": an explicit ?locale= wins, then the browser's first language.</summary>
        public static string Locale(HttpContext ctx)
        {
            string wanted = ctx.Request.Query["locale"].ToString();
            if (wanted.Length == 0) wanted = ctx.Request.Headers.AcceptLanguage.ToString();
            return wanted.TrimStart().StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
        }

        // ------------------------------------------------------------------ news

        private sealed record Post(int Id, string Slug, string Title, string Preview, string Body, DateTimeOffset PublishedAt);

        private static readonly Regex post_name = new Regex(@"^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2})(\d{2}))?-([a-z0-9-]+)\.(en|fr)$", RegexOptions.Compiled);

        private static string plain(string markdown) => Regex.Replace(Regex.Replace(markdown, @"!?\[([^\]]*)\]\([^)]*\)", "$1"), @"[*_`#>]", "").Trim();

        private static List<Post> posts(IConfiguration config, string locale)
        {
            var found = new Dictionary<string, (Post post, string locale)>();
            string dir = Path.Combine(root, "news");
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.md"))
                {
                    var m = post_name.Match(Path.GetFileNameWithoutExtension(file));
                    if (!m.Success) continue;
                    string slug = $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}-{m.Groups[6].Value}", lang = m.Groups[7].Value;
                    if (found.TryGetValue(slug, out var existing) && (existing.locale == locale || lang != locale)) continue;

                    var lines = File.ReadAllLines(file);
                    string body = string.Join('\n', lines.Skip(1)).Trim();
                    var at = new DateTimeOffset(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                        m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 12, m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0, 0, TimeSpan.Zero);
                    string preview = plain(body.Split("\n\n").FirstOrDefault() ?? "");
                    found[slug] = (new Post(0, slug, lines.FirstOrDefault()?.TrimStart('#', ' ') ?? slug, preview.Length > 280 ? preview[..280].TrimEnd() + "…" : preview, body, at), lang);
                }
            }

            // posts dropped on the machine by hand (one language, dated by the file)
            string extra = config["NewsDirectory"] ?? Path.Combine(config["DataDirectory"] ?? "/var/lib/xc-osu", "news");
            if (Directory.Exists(extra))
            {
                foreach (string file in Directory.EnumerateFiles(extra, "*.md"))
                {
                    var lines = File.ReadAllLines(file);
                    string body = string.Join('\n', lines.Skip(1)).Trim(), slug = Path.GetFileNameWithoutExtension(file);
                    string preview = plain(body.Split("\n\n").FirstOrDefault() ?? "");
                    found.TryAdd(slug, (new Post(0, slug, lines.FirstOrDefault()?.TrimStart('#', ' ') ?? slug, preview.Length > 280 ? preview[..280].TrimEnd() + "…" : preview, body, File.GetLastWriteTimeUtc(file)), "en"));
                }
            }

            // ids follow publication order so they stay stable when a post is added later
            var ordered = found.Values.Select(v => v.post).OrderBy(p => p.PublishedAt).ThenBy(p => p.Slug, StringComparer.Ordinal).Select((p, i) => p with { Id = i + 1 }).ToList();
            ordered.Reverse();
            return ordered;
        }

        private static JObject json(Post p, bool withContent)
        {
            var o = new JObject
            {
                ["id"] = p.Id, ["author"] = "xc softworks", ["slug"] = p.Slug, ["title"] = p.Title, ["preview"] = p.Preview,
                ["published_at"] = p.PublishedAt, ["updated_at"] = p.PublishedAt, ["first_image"] = null, ["edit_url"] = null,
            };
            if (withContent) o["content"] = Markdown.ToHtml(p.Body, pipeline);
            return o;
        }

        public static JObject Index(IConfiguration config, string locale = "en", int? year = null)
        {
            var all = posts(config, locale);
            var years = all.Select(p => p.PublishedAt.Year).Distinct().OrderByDescending(y => y).ToArray();
            var shown = year == null ? all : all.Where(p => p.PublishedAt.Year == year).ToList();
            return new JObject
            {
                ["news_posts"] = new JArray(shown.Take(24).Select(p => json(p, false))),
                ["news_sidebar"] = new JObject { ["current_year"] = year ?? years.FirstOrDefault(DateTimeOffset.UtcNow.Year), ["years"] = new JArray(years), ["news_posts"] = new JArray(all.Select(p => json(p, false))) },
                ["cursor"] = null,
                ["search"] = new JObject { ["limit"] = 24, ["sort"] = "published_desc" },
            };
        }

        public static JObject? Show(IConfiguration config, string slug, string locale = "en")
        {
            var all = posts(config, locale);
            int i = all.FindIndex(p => p.Slug == slug);
            if (i < 0) return null;
            var o = json(all[i], true);
            o["navigation"] = new JObject { ["newer"] = i > 0 ? json(all[i - 1], false) : null, ["older"] = i + 1 < all.Count ? json(all[i + 1], false) : null };
            return o;
        }

        // ------------------------------------------------------------------ wiki

        private static readonly Regex wiki_path = new Regex(@"^[A-Za-z0-9_!'-]+(/[A-Za-z0-9_!'-]+)*$", RegexOptions.Compiled);

        private static string? wikiFile(string locale, string path)
        {
            string dir = Path.Combine(root, "wiki", locale);
            if (!Directory.Exists(dir)) return null;
            // the game and people typing addresses do not agree on capitals
            return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                            .FirstOrDefault(f => string.Equals(Path.GetRelativePath(dir, f)[..^3].Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
        }

        public static JObject? Wiki(string locale, string? path)
        {
            path = (path ?? "").Trim('/');
            if (path.Length == 0) path = "Main_page";
            if (!wiki_path.IsMatch(path)) return null;

            string used = locale;
            string? file = wikiFile(locale, path);
            if (file == null && locale != "en") { file = wikiFile("en", path); used = "en"; }
            if (file == null) return null;

            string markdown = File.ReadAllText(file).Replace("\r\n", "\n");
            string dir = Path.Combine(root, "wiki", used);
            string canonical = Path.GetRelativePath(dir, file)[..^3].Replace('\\', '/');
            bool main = canonical == "Main_page";
            string? title = main ? "xc-osu! wiki" : markdown.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim();
            string[] parts = canonical.Split('/');

            return new JObject
            {
                ["available_locales"] = new JArray(new[] { "en", "fr" }.Where(l => wikiFile(l, canonical) != null)),
                ["layout"] = main ? "main_page" : "markdown_page",
                ["locale"] = used,
                ["markdown"] = markdown,
                ["html"] = main ? null : Markdown.ToHtml(markdown, pipeline), // for the site; the game renders the Markdown itself
                ["path"] = canonical,
                ["subtitle"] = parts.Length > 1 ? parts[^2].Replace('_', ' ') : null,
                ["tags"] = new JArray(),
                ["title"] = title ?? parts[^1].Replace('_', ' '),
            };
        }

        /// <summary>Every page of one language (path + title), for the site's index and search.</summary>
        public static JArray WikiIndex(string locale)
        {
            var list = new JArray();
            string dir = Path.Combine(root, "wiki", "en");
            if (!Directory.Exists(dir)) return list;
            foreach (string file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string path = Path.GetRelativePath(dir, file)[..^3].Replace('\\', '/');
                if (path == "Main_page" || Wiki(locale, path) is not JObject page) continue;
                list.Add(new JObject { ["path"] = path, ["title"] = page["title"], ["locale"] = page["locale"] });
            }
            return list;
        }

        // ------------------------------------------------------------------ changelog

        /// <summary>Players connected right now; set by the endpoint from <see cref="Presence"/>.</summary>
        [ThreadStatic] private static int online;

        private static JObject stream(JObject? latest) => new JObject
        {
            ["id"] = 1, ["name"] = "lazer", ["display_name"] = "xc-osu!", ["is_featured"] = true, ["user_count"] = online,
            ["latest_build"] = latest,
        };

        private static List<JObject> builds(string locale)
        {
            string file = Path.Combine(root, "changelog.json");
            var list = new List<JObject>();
            if (!File.Exists(file)) return list;

            var source = JArray.Parse(File.ReadAllText(file));
            int entryId = source.Sum(b => ((JArray?)b["entries"])?.Count ?? 0);
            for (int i = 0; i < source.Count; i++)
            {
                var b = (JObject)source[i];
                var created = DateTimeOffset.Parse(b.Value<string>("date")!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
                var entries = new JArray();
                foreach (var e in (JArray?)b["entries"] ?? new JArray())
                {
                    string? message = (locale == "fr" ? e.Value<string>("message_fr") : null) ?? e.Value<string>("message");
                    entries.Add(new JObject
                    {
                        ["id"] = entryId--, ["repository"] = null, ["github_pull_request_id"] = null, ["github_url"] = null, ["url"] = null,
                        ["type"] = e.Value<string>("type") ?? "misc", ["category"] = (locale == "fr" ? e.Value<string>("category_fr") : null) ?? e.Value<string>("category") ?? "General",
                        ["title"] = (locale == "fr" ? e.Value<string>("title_fr") : null) ?? e.Value<string>("title"),
                        ["message"] = message, ["message_html"] = message == null ? null : Markdown.ToHtml(message, pipeline),
                        ["major"] = e.Value<bool?>("major") ?? false, ["created_at"] = created,
                        ["github_user"] = new JObject { ["id"] = 1, ["display_name"] = "kudasaixc", ["github_url"] = null, ["osu_username"] = null, ["user_id"] = null, ["user_url"] = null },
                    });
                }
                list.Add(new JObject
                {
                    ["id"] = source.Count - i, ["version"] = b.Value<string>("version"), ["display_version"] = b.Value<string>("version"),
                    ["users"] = 0, ["created_at"] = created, ["changelog_entries"] = entries,
                });
            }
            foreach (var b in list) b["update_stream"] = stream(null);
            return list;
        }

        private static JObject brief(JObject build) => new JObject
        {
            ["id"] = build["id"], ["version"] = build["version"], ["display_version"] = build["display_version"], ["users"] = 0,
            ["created_at"] = build["created_at"], ["update_stream"] = stream(null),
        };

        public static JObject Changelog(string locale, int playersOnline = 0)
        {
            online = playersOnline;
            var all = builds(locale);
            return new JObject
            {
                ["builds"] = new JArray(all),
                ["streams"] = new JArray(all.Count > 0 ? new[] { stream(brief(all[0])) } : Array.Empty<JObject>()),
                ["search"] = new JObject { ["stream"] = null, ["from"] = null, ["to"] = null, ["max_id"] = null, ["limit"] = 21 },
            };
        }

        public static JObject? ChangelogBuild(string locale, string version, int playersOnline = 0)
        {
            online = playersOnline;
            var all = builds(locale);
            int i = all.FindIndex(b => b.Value<string>("version") == version || (version == "latest" && b == all[0]));
            if (i < 0) return null;
            all[i]["versions"] = new JObject { ["next"] = i > 0 ? brief(all[i - 1]) : null, ["previous"] = i + 1 < all.Count ? brief(all[i + 1]) : null };
            return all[i];
        }
    }
}
