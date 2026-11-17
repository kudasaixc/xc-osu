// xc-osu! server: short-lived shared answers for heavy public pages. xc softworks (kudasaixc)

using System.Text;

namespace XcOsu.Server.Api;

/// <summary>
/// Public pages that cost many queries (profiles, rankings, the room list) are built once and shared for a few seconds:
/// a crowd, or a script, asking for them at once costs one build. Bounded; the oldest answers go first.
/// </summary>
public static class ResponseCache
{
    private const int max_entries = 2000;
    private static readonly Dictionary<string, (long expires, string json)> entries = new();
    private static readonly LinkedList<string> order = new();
    private static readonly object sync = new();

    public static async Task<IResult> GetOrBuild(string key, TimeSpan ttl, Func<Task<IResult>> build)
    {
        long now = Environment.TickCount64;
        lock (sync)
            if (entries.TryGetValue(key, out var hit) && hit.expires > now)
                return Results.Content(hit.json, "application/json", Encoding.UTF8);

        var result = await build();
        // only successful JSON answers are shared.
        if (result is IContentTypeHttpResult { ContentType: not null } typed && typed.ContentType.StartsWith("application/json")
            && result is IStatusCodeHttpResult { StatusCode: null or 200 } && result is Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult content && content.ResponseContent != null)
        {
            lock (sync)
            {
                if (!entries.ContainsKey(key)) order.AddLast(key);
                entries[key] = (now + (long)ttl.TotalMilliseconds, content.ResponseContent);
                while (entries.Count > max_entries && order.First is { } oldest)
                {
                    entries.Remove(oldest.Value);
                    order.RemoveFirst();
                }
            }
        }
        return result;
    }

    public static Task<IResult> GetOrBuild(string key, TimeSpan ttl, Func<IResult> build) => GetOrBuild(key, ttl, () => Task.FromResult(build()));
}
