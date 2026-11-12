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
        // The token used to open the connection is rotated by a normal refresh while the hub stays up (a multiplayer
        // match must survive that), so check that the account still holds a live session rather than that exact token.
        if (invocation.Context.User?.UserId() is not int user || !accounts.HasActiveSession(user))
        {
            invocation.Context.Abort();
            throw new HubException("Session expired. Please reconnect.");
        }
        int limit = invocation.HubMethodName.StartsWith("SendFrameData") ? 6000 : 1200;
        if (!AbuseGuard.Allow("hub:" + invocation.Context.UserIdentifier + ":" + invocation.HubMethodName, limit, TimeSpan.FromMinutes(1)))
            throw new HubException("Too many requests.");
        return await next(invocation);
    }
}
