// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Game.Graphics;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;

namespace osu.Game.Users.Drawables
{
    [LongRunningLoad]
    public partial class DrawableAvatar : Sprite
    {
        private readonly IUser user;

        /// <summary>
        /// A simple, non-interactable avatar sprite for the specified user.
        /// </summary>
        /// <param name="user">The user. A null value will get a placeholder avatar.</param>
        public DrawableAvatar(IUser user = null)
        {
            this.user = user;

            RelativeSizeAxes = Axes.Both;
            FillMode = FillMode.Fit;
            Anchor = Anchor.Centre;
            Origin = Anchor.Centre;
        }

        [BackgroundDependencyLoader]
        private void load(LargeTextureStore textures, OnlineAssetCachingStore onlineTextures, IAPIProvider api)
        {
            if (user != null && user.OnlineID > 1)
            {
                // Local IDs belong to xc-osu!, including users reconstructed from local scores.
                // Never use an avatar host supplied by imported or incomplete user data.
                string url = $"{api.Endpoints.WebsiteUrl.TrimEnd('/')}/api/xc/avatar/{user.OnlineID}";
                if (Uri.TryCreate((user as APIUser)?.AvatarUrl, UriKind.Absolute, out var supplied)
                    && Uri.TryCreate(url, UriKind.Absolute, out var local)
                    && supplied.GetLeftPart(UriPartial.Path) == local.GetLeftPart(UriPartial.Path))
                    url += supplied.Query;

                Texture = onlineTextures.Get(url);
            }

            Texture ??= textures.Get(@"Online/avatar-guest");
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            this.FadeInFromZero(300, Easing.OutQuint);
        }
    }
}
