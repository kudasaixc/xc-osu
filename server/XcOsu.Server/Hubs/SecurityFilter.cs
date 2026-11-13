using Microsoft.AspNetCore.SignalR;
using XcOsu.Server.Services;

namespace XcOsu.Server.Hubs;

public sealed class SecurityFilter(Accounts accounts, SessionConnections sessions) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var lease = ConnectionLimits.Acquire(context.Context.User?.UserId() ?? 0,
            context.Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        if (lease == null) { context.Context.Abort(); throw new HubException("Too many connections."); }
        // bind the connection to the session that opened it: it is closed as soon as that session ends, idle or not.
        var bound = sessions.Register(context.Context.User?.UserId() ?? 0, context.Context.User?.SessionId(), context.Context.Abort);
        if (bound == null) { lease.Dispose(); context.Context.Abort(); throw new HubException("Session expired. Please reconnect."); }
        context.Context.Items["connection-limit"] = lease;
        context.Context.Items["session-binding"] = bound;
        try { await next(context); }
        catch { lease.Dispose(); bound.Dispose(); context.Context.Items.Remove("connection-limit"); context.Context.Items.Remove("session-binding"); throw; }
    }
    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        try { await next(context, exception); }
        finally
        {
            if (context.Context.Items.Remove("connection-limit", out var lease)) ((IDisposable)lease!).Dispose();
            if (context.Context.Items.Remove("session-binding", out var bound)) ((IDisposable)bound!).Dispose();
        }
    }
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocation,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        // The token used to open the connection is rotated by a normal refresh while the hub stays up (a multiplayer match
        // must survive that), so the check is on the session that token belonged to: rotation keeps it, revocation ends it.
        // Another still-valid session of the same account does not keep this connection alive.
        if (invocation.Context.User?.UserId() is not int user || !accounts.HasActiveSession(user, invocation.Context.User.SessionId()))
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
