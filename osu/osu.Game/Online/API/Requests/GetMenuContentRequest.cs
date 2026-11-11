// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Online.API.Requests.Responses;

namespace osu.Game.Online.API.Requests
{
    public class GetMenuContentRequest : OsuJsonWebRequest<APIMenuContent>
    {
        // xc-osu!: the banners shown on the main menu come from this server, not from the official asset host.
        public GetMenuContentRequest(string websiteUrl)
            : base($@"{websiteUrl}/api/xc/menu-content")
        {
        }
    }
}
