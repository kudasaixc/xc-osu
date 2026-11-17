// xc-osu! server: Cloudflare Turnstile (anti-robot check) on account creation. xc softworks (kudasaixc)

namespace XcOsu.Server.Services;

/// <summary>
/// Enabled once <c>Turnstile:Secret</c> and <c>Turnstile:SiteKey</c> are configured (a free widget from the Cloudflare dashboard).
/// Without them every sign-up passes and only the per-address and per-site budgets apply.
/// <c>Turnstile:RequireForGame</c> (default false) also demands it from the game's own sign-up form, which cannot show the widget.
/// </summary>
public static class Turnstile
{
    public static string? SiteKey(IConfiguration config) => config["Turnstile:SiteKey"] is { Length: > 0 } key && config["Turnstile:Secret"] is { Length: > 0 } ? key : null;

    public static async Task<bool> Passes(IConfiguration config, IHttpClientFactory http, string? response, string address)
    {
        if (config["Turnstile:Secret"] is not { Length: > 0 } secret) return true;
        // the game's form has no widget: only the website's sign-up carries a response, unless the operator decides otherwise.
        if (string.IsNullOrEmpty(response)) return config.GetValue<bool?>("Turnstile:RequireForGame") != true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var result = await http.CreateClient("pwned").PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["secret"] = secret, ["response"] = response }), timeout.Token);
            var json = Newtonsoft.Json.Linq.JObject.Parse(await result.Content.ReadAsStringAsync(timeout.Token));
            return json.Value<bool>("success");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or Newtonsoft.Json.JsonException)
        {
            return true; // Cloudflare unreachable: the budgets still apply.
        }
    }
}
