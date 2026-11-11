// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Graphics.Containers.Markdown;
using osu.Game.Graphics.Containers.Markdown.BBCode;
using osu.Game.Resources.Localisation.Web;

namespace osu.Game.Overlays.Profile.Sections
{
    /// <summary>
    /// The "me!" page. Upstream lazer leaves this section empty; xc-osu! renders the user's markdown page.
    /// </summary>
    public partial class AboutSection : ProfileSection
    {
        public override LocalisableString Title => UsersStrings.ShowExtraMeTitle;

        public override string Identifier => @"me";

        private OsuMarkdownContainer markdown = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            Child = markdown = new ProfileMarkdownContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            User.BindValueChanged(u =>
            {
                string raw = u.NewValue?.User.Page?.Raw ?? string.Empty;
                markdown.Text = BbCodeInlineParser.Prepare(raw);

                // like osu-web, an empty page isn't shown.
                if (string.IsNullOrWhiteSpace(raw)) Hide();
                else Show();
            }, true);
        }
    }
}
