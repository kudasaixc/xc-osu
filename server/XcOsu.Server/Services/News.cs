// xc-osu! server — news posts. Markdown files dropped into NewsDirectory (default /var/lib/xc-osu/news/<slug>.md, first line "# Title") show up in the client. xc softworks (kudasaixc)

using Newtonsoft.Json.Linq;

namespace XcOsu.Server.Services
{
    public static class News
    {
        private static readonly DateTimeOffset launch = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

        private sealed record Post(int Id, string Slug, string Title, string Preview, string Body, DateTimeOffset PublishedAt);

        private static List<Post> posts(IConfiguration config)
        {
            var list = new List<Post>
            {
                new Post(1, "welcome-to-xc-osu", "Welcome to xc-osu!",
                    "xc-osu! runs the full lazer client in your browser, with its own servers: scores, pp, leaderboards, multiplayer, daily challenge and more. Brought to you by xc softworks.",
                    "xc-osu! runs the full lazer client in your browser, with its own servers. Everything online (scores, pp, rankings, multiplayer, chat, medals, daily challenge, quick play) is handled by xc-osu! and never touches the official osu! servers.",
                    launch),
            };

            string dir = config["NewsDirectory"] ?? Path.Combine(config["DataDirectory"] ?? "/var/lib/xc-osu", "news");
            if (Directory.Exists(dir))
            {
                int id = 100;
                foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(f => File.GetLastWriteTimeUtc(f)))
                {
                    var lines = File.ReadAllLines(file);
                    string title = lines.FirstOrDefault()?.TrimStart('#', ' ') ?? Path.GetFileNameWithoutExtension(file);
                    string body = string.Join('\n', lines.Skip(1)).Trim();
                    string preview = body.Split("\n\n").FirstOrDefault() ?? "";
                    list.Add(new Post(id++, Path.GetFileNameWithoutExtension(file), title, preview.Length > 300 ? preview[..300] + "…" : preview, body, File.GetLastWriteTimeUtc(file)));
                }
            }

            return list.OrderByDescending(p => p.PublishedAt).ToList();
        }

        private static JObject json(Post p, bool withContent)
        {
            var o = new JObject
            {
                ["id"] = p.Id, ["author"] = "xc softworks", ["slug"] = p.Slug, ["title"] = p.Title, ["preview"] = p.Preview,
                ["published_at"] = p.PublishedAt, ["updated_at"] = p.PublishedAt, ["first_image"] = null, ["edit_url"] = null,
            };
            if (withContent)
                o["content"] = string.Join("", p.Body.Split("\n\n").Select(par => $"<p>{System.Net.WebUtility.HtmlEncode(par)}</p>"));
            return o;
        }

        public static JObject Index(IConfiguration config)
        {
            var all = posts(config);
            var years = all.Select(p => p.PublishedAt.Year).Distinct().OrderByDescending(y => y).ToArray();
            return new JObject
            {
                ["news_posts"] = new JArray(all.Take(12).Select(p => json(p, false))),
                ["news_sidebar"] = new JObject { ["current_year"] = years.FirstOrDefault(2026), ["years"] = new JArray(years), ["news_posts"] = new JArray(all.Select(p => json(p, false))) },
                ["cursor"] = null,
                ["search"] = new JObject { ["limit"] = 12, ["sort"] = "published_desc" },
            };
        }

        public static JObject? Show(IConfiguration config, string slug) => posts(config).FirstOrDefault(p => p.Slug == slug) is Post p ? json(p, true) : null;
    }
}
