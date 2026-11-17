// xc-osu! server: time-based one-time passwords (RFC 6238), the second factor the game already knows how to ask for. xc softworks (kudasaixc)

using System.Security.Cryptography;

namespace XcOsu.Server.Services;

public static class Totp
{
    private const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] decode(string secret)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (char ch in secret.TrimEnd('=').ToUpperInvariant())
        {
            int v = alphabet.IndexOf(ch);
            if (v < 0) continue;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return output.ToArray();
    }

    private static string code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(key, counter);
        int offset = hash[^1] & 0x0f;
        int binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>The 30-second step a valid code belongs to (one step of clock drift either way), or null. A step at or below
    /// <paramref name="lastUsedStep"/> is refused, so an observed code cannot be replayed.</summary>
    public static long? Verify(string secret, string? input, long lastUsedStep)
    {
        string given = new string((input ?? "").Where(char.IsDigit).ToArray());
        if (given.Length != 6) return null;
        byte[] key = decode(secret);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (long step = now - 1; step <= now + 1; step++)
            if (step > lastUsedStep && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(code(key, step)), System.Text.Encoding.ASCII.GetBytes(given)))
                return step;
        return null;
    }

    public static string Uri(string secret, string account) =>
        $"otpauth://totp/{System.Uri.EscapeDataString("xc-osu!:" + account)}?secret={secret}&issuer={System.Uri.EscapeDataString("xc-osu!")}&algorithm=SHA1&digits=6&period=30";
}
