// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace osu.Game.Online
{
    public sealed class TrustedDomainOnlineStore : OnlineStore
    {
        // xc-osu!: trust our server and beatmap mirrors; official avatars are explicitly blocked below.
        private static readonly string[] trusted_domains = { @"ppy.sh", @"xcsoftworks.com", @"catboy.best", @"nerinyan.moe", @"osu.direct" };

        private static bool isTrusted(string host)
        {
            foreach (string d in trusted_domains)
            {
                if (host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith(@"." + d, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        protected override string GetLookupUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !isTrusted(uri.Host) || uri.Host.Equals("a.ppy.sh", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($@"Blocking resource lookup from external website: {url}", LoggingTarget.Network, LogLevel.Important);
                return string.Empty;
            }

            return url;
        }
    }
}
