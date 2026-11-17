// xc-osu! server: refuse passwords that already leaked. xc softworks (kudasaixc)

using System.Security.Cryptography;
using System.Text;

namespace XcOsu.Server.Services;

/// <summary>
/// Checks a new password against Have I Been Pwned with k-anonymity: only the first five hex characters of its SHA-1 leave the
/// server, never the password or its full hash, and nothing identifies the account. If the service cannot be reached in time the
/// password is accepted (the length and content rules still apply): sign-up must not depend on a third party being up.
/// </summary>
public sealed class PwnedPasswords(IHttpClientFactory factory, IConfiguration config, ILogger<PwnedPasswords> log)
{
    public const string MESSAGE = "This password appeared in a data breach. Please choose another one.";

    public async Task<bool> IsBreached(string password)
    {
        if (config.GetValue<bool?>("PwnedPasswords:Enabled") == false) return false;
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        string prefix = hash[..5], suffix = hash[5..];
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.pwnedpasswords.com/range/" + prefix);
            request.Headers.Add("Add-Padding", "true");
            using var response = await factory.CreateClient("pwned").SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            foreach (var line in body.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon == 35 && string.CompareOrdinal(line, 0, suffix, 0, 35) == 0)
                    return int.TryParse(line.AsSpan(colon + 1).Trim(), out int count) && count > 0;
            }
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogInformation("breached-password check unavailable ({Type}); accepted on the other rules", e.GetType().Name);
            return false;
        }
    }
}
