// xc-osu! load test — hundreds of simultaneous clients, each with its own forwarded address, against a running server.
// Usage: xc-load-test [--base http://127.0.0.1:5081] [--connections 300] [--seconds 30] [--mode read|mixed] [--path /api/v2/...]
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

string baseUrl = "http://127.0.0.1:5081";
int connections = 300, seconds = 30;
string mode = "read", only = "";
for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--base": baseUrl = args[i + 1]; break;
        case "--connections": connections = int.Parse(args[i + 1]); break;
        case "--seconds": seconds = int.Parse(args[i + 1]); break;
        case "--mode": mode = args[i + 1]; break;
        case "--path": only = args[i + 1]; break;
    }
}

string[] readPaths = only.Length > 0 ? new[] { only } : new[]
{
    "/api/xc/health", "/api/xc/teams", "/api/xc/menu-content",
    "/api/v2/rankings/osu/performance", "/api/v2/rankings/taiko/score?page=2",
    "/api/v2/beatmapsets/search?q=test&m=0", "/api/v2/users/1/scores/best",
    "/api/v2/users/1/recent_activity", "/api/v2/changelog", "/api/v2/news",
};

using var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = connections,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    AutomaticDecompression = DecompressionMethods.All,
};
using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

// write mode: a pool of accounts so logins spend their own per-account budget.
var accounts = new ConcurrentQueue<string>();
if (mode == "mixed")
{
    for (int i = 0; i < Math.Min(connections / 4, 100); i++)
    {
        string name = "load_" + Guid.NewGuid().ToString("N")[..8]; // usernames are capped at 15 characters
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users")
        {
            Content = JsonContent.Create(new Dictionary<string, string>
            {
                ["user[username]"] = name, ["user[user_email]"] = name + "@example.invalid", ["user[password]"] = "a-good-password-123"
            })
        };
        request.Headers.Add("X-Forwarded-For", $"10.43.0.{i + 1}");
        using var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode) accounts.Enqueue(name);
    }
    Console.WriteLine($"prepared {accounts.Count} accounts");
}

var latencies = new ConcurrentQueue<double>();
var statuses = new ConcurrentDictionary<int, int>();
var errors = new ConcurrentQueue<string>();
var ready = new ManualResetEventSlim(false);
long deadline = 0;

var clients = Enumerable.Range(0, connections).Select(index => Task.Run(async () =>
{
    string forwarded = $"10.42.{index / 250}.{index % 250 + 1}";
    var random = new Random(index);
    ready.Wait();
    string? token = null;
    while (Volatile.Read(ref deadline) - Stopwatch.GetTimestamp() > 0)
    {
        string path;
        HttpRequestMessage request;
        if (mode == "mixed" && index % 4 == 0 && accounts.TryPeek(out string? account))
        {
            request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
            {
                Content = JsonContent.Create(new Dictionary<string, string>
                {
                    ["grant_type"] = "password", ["username"] = account, ["password"] = "a-good-password-123"
                })
            };
            path = "/oauth/token";
        }
        else
        {
            path = readPaths[(index + random.Next(readPaths.Length)) % readPaths.Length];
            request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token != null) request.Headers.Authorization = new("Bearer", token);
        }
        request.Headers.Add("X-Forwarded-For", forwarded);
        long started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await http.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
            latencies.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            statuses.AddOrUpdate((int)response.StatusCode, 1, (_, n) => n + 1);
            if ((int)response.StatusCode >= 500) errors.Enqueue((int)response.StatusCode + " " + path);
            if (path == "/oauth/token" && response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                token = json.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            }
        }
        catch (Exception e)
        {
            errors.Enqueue(e.GetType().Name + ": " + e.Message);
            latencies.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
})).ToArray();

Thread.Sleep(500);
deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
var wall = Stopwatch.StartNew();
ready.Set();
await Task.WhenAll(clients);
wall.Stop();

double[] sorted = latencies.ToArray();
Array.Sort(sorted);
double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
long total = sorted.LongLength;
Console.WriteLine(JsonSerializer.Serialize(new
{
    requests = total,
    per_second = Math.Round(total / wall.Elapsed.TotalSeconds, 1),
    connections,
    requests_per_connection = Math.Round((double)total / connections, 1),
    latency_ms = new
    {
        p50 = Math.Round(Percentile(.50), 1), p95 = Math.Round(Percentile(.95), 1), p99 = Math.Round(Percentile(.99), 1),
        max = sorted.Length == 0 ? 0 : Math.Round(sorted[^1], 1), mean = sorted.Length == 0 ? 0 : Math.Round(sorted.Average(), 1)
    },
    status = statuses.OrderBy(k => k.Key).ToDictionary(k => k.Key.ToString(), k => k.Value),
    errors = errors.Count,
    error_samples = errors.Take(8).ToArray()
}, new JsonSerializerOptions { WriteIndented = true }));
