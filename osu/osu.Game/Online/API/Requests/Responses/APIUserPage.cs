// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Newtonsoft.Json;

namespace osu.Game.Online.API.Requests.Responses
{
    /// <summary>
    /// The user's "me!" page (xc-osu!: shown on the profile, edited from the account page).
    /// </summary>
    public class APIUserPage
    {
        [JsonProperty(@"html")]
        public string Html { get; set; } = string.Empty;

        [JsonProperty(@"raw")]
        public string Raw { get; set; } = string.Empty;
    }
}
