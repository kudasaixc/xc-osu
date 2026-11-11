using System;
using System.Collections.Generic;
using System.Text;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax.Inlines;

namespace osu.Game.Graphics.Containers.Markdown.BBCode
{
    // BBCode is opt-in for player profile pages; news/wiki Markdown keeps its existing syntax.
    public sealed class BbCodeInline : LeafInline
    {
        public string Tag { get; }
        public string Parameter { get; }
        public string Content { get; }
        public BbCodeInline(string tag, string parameter, string content)
        {
            Tag = tag;
            Parameter = parameter;
            Content = content;
        }
    }

    public sealed class BbCodeInlineParser : InlineParser
    {
        private static readonly HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "b", "i", "u", "s", "strike", "color", "colour", "size", "url", "img", "code", "quote", "notice", "list", "heading",
        };

        public BbCodeInlineParser() => OpeningCharacters = new[] { '[' };

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            if (!TryRead(slice.Text, slice.Start, slice.End + 1, out var node, out int end)) return false;
            processor.Inline = node;
            slice.Start = end;
            return true;
        }

        // Keep blank lines inside BBCode in a single Markdown paragraph. The invisible marker
        // preserves their visual line breaks while allowing the inline parser to pair the tags.
        public static string Prepare(string text)
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var output = new StringBuilder();
            for (int at = 0; at < text.Length;)
            {
                if (text[at] == '[' && TryRead(text, at, text.Length, out _, out int end))
                {
                    string block = text.Substring(at, end - at);
                    output.Append(block.Replace("\n\n", "\n\u200b\n", StringComparison.Ordinal));
                    at = end;
                }
                else output.Append(text[at++]);
            }
            return output.ToString();
        }

        // A bounded scanner, with no backtracking regex. Nested same-name tags are paired correctly.
        public static bool TryRead(string text, int start, int limit, out BbCodeInline node, out int end)
        {
            node = null!;
            end = start;
            limit = Math.Min(limit, text.Length);
            if (start >= limit || text[start] != '[') return false;
            int openEnd = text.IndexOf(']', start + 1);
            if (openEnd < 0 || openEnd >= limit || openEnd - start > 256) return false;
            string opening = text.Substring(start + 1, openEnd - start - 1);
            int equals = opening.IndexOf('=');
            string tag = (equals < 0 ? opening : opening.Substring(0, equals)).ToLowerInvariant();
            if (!tags.Contains(tag)) return false;
            string parameter = equals < 0 ? string.Empty : opening.Substring(equals + 1).Trim('"', '\'');
            int depth = 1;
            for (int at = openEnd + 1; at < limit;)
            {
                int next = text.IndexOf('[', at);
                if (next < 0 || next >= limit) break;
                int close = text.IndexOf(']', next + 1);
                if (close < 0 || close >= limit) break;
                string candidate = text.Substring(next + 1, close - next - 1);
                if (candidate.Equals("/" + tag, StringComparison.OrdinalIgnoreCase))
                {
                    if (--depth == 0)
                    {
                        node = new BbCodeInline(tag, parameter, text.Substring(openEnd + 1, next - openEnd - 1));
                        end = close + 1;
                        return true;
                    }
                }
                else if (tag != "code" && (candidate.Equals(tag, StringComparison.OrdinalIgnoreCase)
                         || candidate.StartsWith(tag + "=", StringComparison.OrdinalIgnoreCase))) depth++;
                at = close + 1;
            }
            return false; // Unknown or unclosed tags stay readable as plain text.
        }
    }
}
