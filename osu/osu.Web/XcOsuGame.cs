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

        // the intro waits for the player's click on the launch screen (fullscreen + audio need a user gesture)
        protected override osu.Game.Screens.Loader CreateLoader() => new XcLoader();

        private partial class XcLoader : osu.Game.Screens.Loader
        {
            protected override bool CanShowLoadedScreen => osu.Framework.Platform.Web.WebStartGate.IsOpen;
        }

        public override EndpointConfiguration CreateEndpoints() => new XcEndpointConfiguration(Origin);

        // ---- telemetry context: which screen / overlay the frame timings belong to (see Telemetry.cs)

        protected override void ScreenChanged(osu.Game.Screens.IOsuScreen current, osu.Game.Screens.IOsuScreen newScreen)
        {
            base.ScreenChanged(current, newScreen);
            Telemetry.SetScreen(current?.GetType().Name, newScreen?.GetType().Name);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (Telemetry.Enabled)
                Add(new TelemetryProbe(this));
        }

        private partial class TelemetryProbe : osu.Framework.Graphics.Component
        {
            private readonly XcOsuGame game;
            private readonly System.Text.StringBuilder names = new System.Text.StringBuilder();
            private double nextCheck;

            public TelemetryProbe(XcOsuGame game)
            {
                this.game = game;
            }

            protected override void Update()
            {
                base.Update();

                if (Time.Current < nextCheck) return;

                nextCheck = Time.Current + 200;
                names.Clear();

                foreach (var o in game.FocusedOverlays)
                {
                    if (o.State.Value != osu.Framework.Graphics.Containers.Visibility.Visible) continue;

                    if (names.Length > 0) names.Append('+');
                    names.Append(o.GetType().Name);
                }

                Telemetry.SetOverlay(names.ToString());
                Telemetry.SetPlaying(game.UserPlayingState.Value == osu.Game.Screens.Play.LocalUserPlayingState.Playing);
            }
        }

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
