using Microsoft.AspNetCore.SignalR;
using XcOsu.Server.Services;

namespace XcOsu.Server.Hubs;

public sealed class SecurityFilter(Accounts accounts) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var lease = ConnectionLimits.Acquire(context.Context.User?.UserId() ?? 0,
            context.Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        if (lease == null) { context.Context.Abort(); throw new HubException("Too many connections."); }
        context.Context.Items["connection-limit"] = lease;
        try { await next(context); }
        catch { lease.Dispose(); throw; }
    }
    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        try { await next(context, exception); }
        finally { if (context.Context.Items.Remove("connection-limit", out var lease)) ((IDisposable)lease!).Dispose(); }
    }
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocation,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var context = invocation.Context.GetHttpContext();
        string? token = context?.Request.Headers.Authorization.ToString();
        token = token?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? token[7..].Trim()
            : context?.Request.Query["access_token"].FirstOrDefault() ?? context?.Request.Cookies[BrowserSession.AccessCookie];
        if (token == null || accounts.ValidateAccessToken(token) != invocation.Context.User?.UserId())
        {
            invocation.Context.Abort();
            throw new HubException("Session expired. Please reconnect.");
        }
        int limit = invocation.HubMethodName.StartsWith("SendFrameData") ? 6000 : 240;
        if (!AbuseGuard.Allow("hub:" + invocation.Context.UserIdentifier + ":" + invocation.HubMethodName, limit, TimeSpan.FromMinutes(1)))
            throw new HubException("Too many requests.");
        return await next(invocation);
    }
}
