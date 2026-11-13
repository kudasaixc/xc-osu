// xc-osu! server — avatars, covers, badges, screenshots and account settings. Served by xc-osu! itself, never by the official CDN. xc softworks (kudasaixc)

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XcOsu.Server.Data;
using XcOsu.Server.Services;

namespace XcOsu.Server.Api
{
    public static class Media
    {
        private const long max_avatar_bytes = 5 << 20;
        private const long max_cover_bytes = 10 << 20;

        private static readonly string assets = Path.Combine(AppContext.BaseDirectory, "Assets");

        private static string? imageType(ReadOnlySpan<byte> b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image/png";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
            if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
            if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
            return null;
        }

        public static bool IsImage(ReadOnlySpan<byte> b) => imageType(b) != null;

        public static IResult Serve(string path, string fallback) => serve(path, fallback);

        private static IResult serve(string path, string fallback, int cacheSeconds = 3600)
        {
            string file = File.Exists(path) ? path : Path.Combine(assets, fallback);
            if (!File.Exists(file)) return Results.NotFound();
            var head = new byte[12];
            using (var fs = File.OpenRead(file)) fs.ReadExactly(head, 0, (int)Math.Min(12, fs.Length));
            return Results.File(file, imageType(head) ?? "application/octet-stream", lastModified: File.GetLastWriteTimeUtc(file), enableRangeProcessing: false);
        }

        public static void Map(WebApplication app)
        {
            app.MapGet("/api/xc/avatar/{id}", (string id, Db db) =>
                serve(int.TryParse(id, out int u) ? Path.Combine(db.DataDirectory, "avatars", u.ToString()) : "", "avatar-default.png"));

            app.MapGet("/api/xc/cover/{id}", (string id, Db db) =>
                serve(int.TryParse(id, out int u) ? Path.Combine(db.DataDirectory, "covers", u.ToString()) : "", "cover-default.jpg"));

            // banners of the game's main menu ({ image, url, begins, expires } entries); none for now
            app.MapGet("/api/xc/menu-content", () => Json.Ok(new { images = Array.Empty<object>() }));

            app.MapGet("/api/xc/settings", () => Results.File(Path.Combine(assets, "settings.html"), "text/html; charset=utf-8"));

            app.MapGet("/api/xc/badge/{name}", (string name) => serve(Path.Combine(assets, "badge-" + Path.GetFileName(name)), "badge-xc.png"));

            app.MapGet("/api/xc/ss/{hash}", (string hash, Db db) =>
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-f]{24}$")) return Results.NotFound();
                string path = Path.Combine(db.DataDirectory, "screenshots", hash + ".jpg");
                return File.Exists(path) ? serve(path, "") : Results.NotFound();
            });

            app.MapPost("/api/xc/account/avatar", (HttpContext ctx, Db db) => upload(ctx, db, "avatars", "avatar_version", max_avatar_bytes)).DisableAntiforgery();
            app.MapPost("/api/xc/account/cover", (HttpContext ctx, Db db) => upload(ctx, db, "covers", "cover_version", max_cover_bytes)).DisableAntiforgery();
            app.MapDelete("/api/xc/account/cover", (HttpContext ctx, Db db) =>
            {
                if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
                File.Delete(Path.Combine(db.DataDirectory, "covers", u.ToString()));
                using var c = db.Open();
                c.Exec("UPDATE users SET cover_version = 0 WHERE id = $u", ("$u", u));
                return Results.NoContent();
            });

            // profile settings (osu-web's account settings page equivalent)
            app.MapGet("/api/xc/account", (HttpContext ctx, Accounts accounts, Users users) =>
            {
                if (ctx.User.UserId() is not int u || accounts.Get(u) is not AccountRow row) return Json.Error("unauthorized", 401);
                return Json.Ok(new JObject
                {
                    ["id"] = row.Id, ["username"] = row.Username, ["email"] = row.Email, ["country"] = row.Country, ["playmode"] = row.Playmode,
                    ["avatar_url"] = users.AvatarUrl(row), ["cover_url"] = users.CoverUrl(row), ["profile"] = JObject.Parse(row.ProfileJson),
                    ["team_id"] = users.Team(row.Id)?.Id,
                    ["email_verified"] = accounts.EmailVerified(row.Id), ["email_confirmation_required"] = accounts.NeedsEmailConfirmation(row.Id),
                });
            });

            app.MapPut("/api/xc/account", async (HttpContext ctx, Db db, Accounts accounts) =>
            {
                if (ctx.User.UserId() is not int u || accounts.Get(u) is not AccountRow row) return Json.Error("unauthorized", 401);
                var body = await Json.Body<JObject>(ctx.Request);
                if (body == null) return Json.Error("invalid body");
                // preferences stay editable; text shown to other players waits for a confirmed address.
                if (new[] { "location", "interests", "occupation", "twitter", "discord", "website", "page" }.Any(k => body[k] is JValue { Type: not JTokenType.Null })
                    && accounts.NeedsEmailConfirmation(u))
                    return Json.Error(Accounts.CONFIRM_EMAIL_MESSAGE, 403);
                var profile = JObject.Parse(row.ProfileJson);
                foreach (string key in new[] { "location", "interests", "occupation", "twitter", "discord", "website" })
                    if (body[key] is JValue v)
                        profile[key] = v.Type == JTokenType.Null ? null : (v.ToString().Trim() is { Length: > 0 } s ? (s.Length > 100 ? s[..100] : s) : null);
                if (body["playstyle"] is JArray styles)
                    profile["playstyle"] = new JArray(styles.Select(s => s.ToString()).Where(s => s is "mouse" or "keyboard" or "tablet" or "touch").Distinct());
                if (body["profile_hue"] is JValue hue)
                    profile["profile_hue"] = hue.Type == JTokenType.Null ? null : Math.Clamp(hue.Value<int>(), 0, 360);
                if (body["pm_friends_only"] is JValue pm) profile["pm_friends_only"] = pm.Value<bool>();
                if (body["page"] is JValue page)
                {
                    string text = page.Type == JTokenType.Null ? "" : page.ToString().Replace("\r\n", "\n");
                    if (text.Length > 60_000) return Json.Error("Your me! page is too long (60,000 characters max).", 422);
                    profile["page"] = text.Trim().Length == 0 ? null : text;
                }
                if (body["profile_order"] is JArray order) profile["profile_order"] = order;

                using var c = db.Open();
                c.Exec("UPDATE users SET profile_json = $p WHERE id = $u", ("$p", profile.ToString(Formatting.None)), ("$u", u));
                if (body.Value<string>("playmode") is "osu" or "taiko" or "fruits" or "mania")
                    c.Exec("UPDATE users SET playmode = $m WHERE id = $u", ("$m", body.Value<string>("playmode")), ("$u", u));
                // the country is set from the connection when the account is created and is not editable: rankings by country rely on it.
                return Results.NoContent();
            });

            app.MapPost("/api/xc/account/password", async (HttpContext ctx, Db db, Accounts accounts) =>
            {
                if (ctx.User.UserId() is not int u || accounts.Get(u) is not AccountRow row) return Json.Error("unauthorized", 401);
                var p = await Json.Params(ctx.Request);
                if (!Accounts.VerifyPassword(p("current_password") ?? "", row.PasswordHash)) return Json.Error("Current password is incorrect.", 403);
                string next = p("new_password") ?? "";
                if (next.Length is < Accounts.PASSWORD_MIN or > Accounts.PASSWORD_MAX) return Json.Error(Accounts.PASSWORD_RULE, 422);
                using var c = db.Open();
                c.Exec("BEGIN IMMEDIATE");
                c.Exec("UPDATE users SET password_hash = $h WHERE id = $u", ("$h", Accounts.HashPassword(next)), ("$u", u));
                c.Exec("DELETE FROM password_resets WHERE user_id = $u", ("$u", u));
                c.Exec("DELETE FROM tokens WHERE user_id = $u", ("$u", u));
                c.Exec("DELETE FROM account_challenges WHERE user_id = $u AND purpose = 'reset'", ("$u", u));
                c.Exec("COMMIT");
                accounts.NotifyRevoked(u);
                return Results.NoContent();
            });
        }

        private static async Task<IResult> upload(HttpContext ctx, Db db, string folder, string versionColumn, long maxBytes)
        {
            if (ctx.User.UserId() is not int u) return Json.Error("unauthorized", 401);
            if (ctx.RequestServices.GetRequiredService<Accounts>().NeedsEmailConfirmation(u)) return Json.Error(Accounts.CONFIRM_EMAIL_MESSAGE, 403);
            if (!ctx.Request.HasFormContentType) return Json.Error("Send the image as multipart form field \"file\".", 422);
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return Json.Error("No image.", 422);
            if (file.Length > maxBytes) return Json.Error($"Image too large (max {maxBytes >> 20} MB).", 413);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = await SafeImages.Reencode(ms.ToArray(), false, ctx.RequestAborted);
            if (imageType(bytes) == null) return Json.Error("Only PNG, JPEG, GIF or WebP images.", 415);

            string dir = Path.Combine(db.DataDirectory, folder);
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, $"{u}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, Path.Combine(dir, u.ToString()), true);

            using var c = db.Open();
            c.Exec($"UPDATE users SET {versionColumn} = {versionColumn} + 1 WHERE id = $u", ("$u", u));
            return Results.NoContent();
        }
    }
}
