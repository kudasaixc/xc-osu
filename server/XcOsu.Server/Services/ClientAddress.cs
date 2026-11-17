// xc-osu! server: who is asking, as a key for rate limits. xc softworks (kudasaixc)

using System.Net;
using System.Net.Sockets;

namespace XcOsu.Server.Services;

/// <summary>
/// The client address the limits are keyed on. Apache resolves the visitor's address from Cloudflare (mod_remoteip) and
/// forwards it; the forwarded-headers middleware puts it in <c>RemoteIpAddress</c>. An IPv6 visitor controls a whole /64,
/// so every IPv6 address is reduced to its /64: changing the interface id must not buy a fresh budget.
/// </summary>
public static class ClientAddress
{
    public static string Key(IPAddress? address)
    {
        if (address == null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return "v6:" + Convert.ToHexString(address.GetAddressBytes(), 0, 8).ToLowerInvariant();
        return address.ToString();
    }

    public static string Key(HttpContext? context) => Key(context?.Connection.RemoteIpAddress);
}
