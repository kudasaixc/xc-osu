using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Markdig.Syntax.Inlines;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Containers.Markdown;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Graphics.Containers.Markdown.BBCode
{
    public partial class ProfileMarkdownContainer : OsuMarkdownContainer
    {
        protected override OsuMarkdownContainerOptions Options => new OsuMarkdownContainerOptions { BbCode = true, CustomContainers = true };
        public override OsuMarkdownTextFlowContainer CreateTextFlow() => new ProfileTextFlow();

        private partial class ProfileTextFlow : OsuMarkdownTextFlowContainer
        {
            private readonly record struct Style(bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false,
                                                 float Size = 14, Color4? Colour = null);

            protected override void AddImage(LinkInline linkInline)
            {
                if (safeUrl(linkInline.Url)) AddDrawable(new MarkdownImage(linkInline.Url));
            }

            protected override void AddNotImplementedInlineText(Inline inline)
            {
                if (inline is BbCodeInline bb) render(bb, new Style(Size: 14), 0);
                else base.AddNotImplementedInlineText(inline);
            }

            private void render(BbCodeInline bb, Style style, int depth)
            {
                if (depth > 32) { addStyled(bb.Content, style); return; }
                switch (bb.Tag)
                {
                    case "b": style = style with { Bold = true }; break;
                    case "i": style = style with { Italic = true }; break;
                    case "u": style = style with { Underline = true }; break;
                    case "s":
                    case "strike": style = style with { Strike = true }; break;
                    case "color":
                    case "colour":
                        if (Colour4.TryParseHex(bb.Parameter, out var colour)) style = style with { Colour = colour };
                        break;
                    case "size":
                        if (float.TryParse(bb.Parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out float size) && float.IsFinite(size))
                            style = style with { Size = Math.Clamp(size > 40 ? 14 * size / 100 : size, 8, 40) };
                        break;
                    case "heading": style = style with { Bold = true, Size = 24 }; break;
                    case "url":
                        string url = string.IsNullOrEmpty(bb.Parameter) ? bb.Content.Trim() : bb.Parameter;
                        if (safeUrl(url)) { AddLinkText(bb.Content, new LinkInline(url, null)); return; }
                        break;
                    case "img":
                        string image = bb.Content.Trim();
                        if (safeUrl(image)) AddDrawable(new MarkdownImage(image));
                        else addStyled(bb.Content, style);
                        return;
                    case "code":
                        AddCodeInLine(new CodeInline { Content = bb.Content });
                        return;
                    case "quote":
                    case "notice":
                        NewLine();
                        addStyled(string.IsNullOrEmpty(bb.Parameter) ? "▎ " : bb.Parameter + ": ", style with { Bold = true });
                        break;
                    case "list":
                        NewLine();
                        break;
                }
                string content = bb.Tag == "list" ? bb.Content.Replace("[*]", "\n• ", StringComparison.Ordinal) : bb.Content;
                for (int at = 0; at < content.Length;)
                {
                    if (content[at] == '[' && BbCodeInlineParser.TryRead(content, at, content.Length, out var nested, out int end))
                    {
                        render(nested, style, depth + 1);
                        at = end;
                    }
                    else
                    {
                        int next = content.IndexOf('[', at + 1);
                        if (next < 0) next = content.Length;
                        addStyled(content.Substring(at, next - at), style);
                        at = next;
                    }
                }
                if (bb.Tag is "quote" or "notice" or "list" or "heading") NewLine();
            }

            private static bool safeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == "https" || uri.Scheme == "http") && string.IsNullOrEmpty(uri.UserInfo);

            private void addStyled(string text, Style style)
            {
                var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (int line = 0; line < lines.Length; line++)
                {
                    if (line > 0) NewLine();
                    if (!style.Underline && !style.Strike)
                    {
                        AddText(lines[line], t => apply(t, style));
                        continue;
                    }
                    // Keep words separate so decorated text can wrap with the rest of the paragraph.
                    foreach (string word in Regex.Split(lines[line], "(?<= )", RegexOptions.None, TimeSpan.FromSeconds(1)))
                    {
                        if (word.Length == 0) continue;
                        var label = new OsuSpriteText { Text = word };
                        apply(label, style);
                        var decorated = new Container { AutoSizeAxes = Axes.Both, Child = label };
                        if (style.Underline) decorated.Add(new Box { RelativeSizeAxes = Axes.X, Height = 1, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Colour = label.Colour });
                        if (style.Strike) decorated.Add(new Box { RelativeSizeAxes = Axes.X, Height = 1, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = label.Colour });
                        AddDrawable(decorated);
                    }
                }
            }

            private static void apply(SpriteText text, Style style)
            {
                text.Font = OsuFont.GetFont(Typeface.Inter, size: style.Size, weight: style.Bold ? FontWeight.Bold : FontWeight.Regular, italics: style.Italic);
                if (style.Colour is Color4 colour) text.Colour = colour;
            }
        }
    }
}
