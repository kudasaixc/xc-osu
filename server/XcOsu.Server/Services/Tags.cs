// xc-osu! server: user beatmap tag catalog (osu-web's tag list, voted on by players who passed the map). xc softworks (kudasaixc)

using osu.Game.Online.API.Requests.Responses;

namespace XcOsu.Server.Services
{
    public static class Tags
    {
        public static readonly APITag[] ALL = build();

        private static APITag[] build()
        {
            (string name, string description, int? ruleset)[] list =
            {
                ("style/clean", "Sparse, minimal patterns.", null),
                ("style/messy", "Intentionally chaotic patterns.", null),
                ("style/geometric", "Strong use of geometric shapes.", 0),
                ("style/grid snap", "Objects placed on a visible grid.", 0),
                ("style/hexgrid", "Objects placed on a hexagonal grid.", 0),
                ("style/freeform", "Unstructured, flowing placement.", 0),
                ("style/symmetrical", "Mirror symmetry throughout.", 0),
                ("style/old-style revival", "Mapped like early osu! maps.", 0),
                ("style/clean", "Clean rhythm and spacing.", 1),
                ("style/technical", "Unusual rhythm and spacing challenges.", null),
                ("style/experimental", "Unconventional ideas.", null),
                ("style/low sv", "Mostly slow slider velocity.", null),
                ("style/high sv", "Mostly fast slider velocity.", null),
                ("style/slider only", "Sliders make up almost everything.", 0),
                ("style/circle only", "No sliders at all.", 0),
                ("streams", "Long consecutive runs of 1/4 notes.", null),
                ("aim/jump aim", "Large jumps between objects.", 0),
                ("aim/sharp aim", "Sharp angle changes.", 0),
                ("aim/wide aim", "Wide angles over long distances.", 0),
                ("aim/linear aim", "Movement mostly along straight lines.", 0),
                ("aim/aim control", "Irregular movement requiring control.", 0),
                ("aim/flow aim", "Smooth, circular movement.", 0),
                ("aim/precision", "Small circle size.", 0),
                ("tap/bursts", "Short streams.", null),
                ("tap/stamina", "Long sections of sustained tapping.", null),
                ("tap/cross-screen tapping", "Streams spaced across the screen.", 0),
                ("tap/stream", "Streams of notes.", null),
                ("tap/deathstream", "Very long streams.", null),
                ("reading/visually dense", "Many objects on screen at once.", null),
                ("reading/overlap reading", "Overlapping objects.", 0),
                ("reading/high ar", "Very high approach rate.", 0),
                ("reading/low ar", "Very low approach rate.", 0),
                ("reading/memorisation", "Hard to read without memorising.", null),
                ("reading/unconventional", "Unusual reading challenges.", null),
                ("rhythm/simple", "Simple, straightforward rhythm.", null),
                ("rhythm/complex", "Complex, varied rhythm.", null),
                ("rhythm/polyrhythm", "Polyrhythmic patterns.", null),
                ("rhythm/swing", "Swing rhythm.", null),
                ("rhythm/1/6 snap", "1/6 or 1/3 divisor rhythm.", null),
                ("rhythm/1/8 snap", "1/8 divisor rhythm.", null),
                ("slider/complex sliders", "Complex slider shapes.", 0),
                ("slider/slider tech", "Sliders with demanding movement.", 0),
                ("slider/repeat sliders", "Many repeating sliders.", 0),
                ("song representation/simple", "Follows the song simply.", null),
                ("song representation/detailed", "Follows every detail of the song.", null),
                ("song representation/abstract", "Interprets the song loosely.", null),
                ("meta/tournament", "Built for tournament play.", null),
                ("meta/marathon", "Very long map.", null),
                ("meta/gimmick", "Built around a gimmick.", null),
                ("meta/storyboard", "Notable storyboard.", null),
                ("meta/video", "Has a video background.", null),
                ("meta/farm", "Gives a lot of pp for its difficulty.", 0),
                ("meta/mapping contest", "Made for a mapping contest.", null),
                ("meta/collab", "Made by several mappers.", null),
            };

            return list.Select((t, i) => new APITag { Id = i + 1, Name = t.name, Description = t.description, RulesetId = t.ruleset }).ToArray();
        }
    }
}
