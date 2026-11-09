// xc-osu! game entry (xc softworks / kudasaixc)

using System;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Online;

namespace osu.Web
{
    public partial class XcOsuGame : OsuGame
    {
        /// <summary>
        /// The page origin (e.g. https://osu.xcsoftworks.com). All online services are served from the same origin by xc-osu-server.
        /// </summary>
        public static string Origin => Environment.GetEnvironmentVariable("XC_ORIGIN")?.TrimEnd('/') ?? "https://osu.xcsoftworks.com";

        public XcOsuGame()
            : base(Array.Empty<string>())
        {
        }

        protected override System.Collections.Generic.IEnumerable<osu.Framework.IO.Stores.IResourceStore<byte[]>> CreateOverrideResourceStores() => new[]
        {
            // xc-osu! branding (menu logo etc.)
            new osu.Framework.IO.Stores.NamespacedResourceStore<byte[]>(new osu.Framework.IO.Stores.DllResourceStore(typeof(XcOsuGame).Assembly), @"Resources"),
        };

        public override EndpointConfiguration CreateEndpoints() => new XcEndpointConfiguration(Origin);

        public override void SetHost(GameHost host)
        {
            base.SetHost(host);
            host.Window.Title = "xc-osu!";
        }
    }

    public class XcEndpointConfiguration : EndpointConfiguration
    {
        public XcEndpointConfiguration(string origin)
        {
            WebsiteUrl = APIUrl = origin;
            APIClientID = "xc-osu";
            APIClientSecret = "xc-softworks";
            SpectatorUrl = $"{origin}/signalr/spectator";
            MultiplayerUrl = $"{origin}/signalr/multiplayer";
            MetadataUrl = $"{origin}/signalr/metadata";
            BeatmapSubmissionServiceUrl = null;
        }
    }
}
