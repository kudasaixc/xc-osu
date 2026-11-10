// xc-osu! server — difficulty / pp using osu!'s own calculators. xc softworks (kudasaixc)

using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Online.API;
using osu.Game.IO;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring;
using osu.Game.Rulesets.Objects;

namespace XcOsu.Server.Services
{
    public sealed class Performance
    {
        private readonly Mirrors mirrors;
        private readonly ILogger<Performance> log;

        public static readonly Ruleset[] RULESETS = { new OsuRuleset() };

        public Performance(Mirrors mirrors, ILogger<Performance> log)
        {
            this.mirrors = mirrors;
            this.log = log;
        }

        public static Ruleset? Ruleset(int id) => RULESETS.FirstOrDefault(r => r.RulesetInfo.OnlineID == id);

        private async Task<FlatWorkingBeatmap?> load(int beatmapId, CancellationToken ct)
        {
            byte[]? data = await mirrors.GetOsuFile(beatmapId, ct);
            if (data == null) return null;

            using var stream = new LineBufferedReader(new MemoryStream(data));
            var beatmap = Decoder.GetDecoder<Beatmap>(stream).Decode(stream);
            beatmap.BeatmapInfo.Ruleset = RULESETS[0].RulesetInfo;
            return new FlatWorkingBeatmap(beatmap);
        }

        public sealed record ScoreCalculation(double? Pp, double StarRating, int MaxCombo, double Length);

        /// <summary>pp + difficulty of a submitted score, using osu!'s own calculators. Null when the beatmap can't be loaded.</summary>
        public async Task<ScoreCalculation?> Calculate(int beatmapId, SoloScoreInfo score, CancellationToken ct)
        {
            try
            {
                var ruleset = Ruleset(score.RulesetID);
                if (ruleset == null) return null;

                var working = await load(beatmapId, ct);
                if (working == null) return null;

                Mod[] mods = score.Mods.Select(m => m.ToMod(ruleset)).ToArray();
                var attributes = ruleset.CreateDifficultyCalculator(working).Calculate(mods, ct);
                var scoreInfo = score.ToScoreInfo(mods, working.BeatmapInfo);
                scoreInfo.Ruleset = ruleset.RulesetInfo;
                var perf = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, attributes);

                var hitObjects = working.Beatmap.HitObjects;
                double length = hitObjects.Count == 0 ? 0 : hitObjects[^1].GetEndTime() - hitObjects[0].StartTime;
                double rate = osu.Game.Utils.ModUtils.CalculateRateWithMods(mods);
                return new ScoreCalculation(perf?.Total, attributes.StarRating, attributes.MaxCombo, length / 1000 / rate);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "pp calculation failed for beatmap {Id}", beatmapId);
                return null;
            }
        }

        public async Task<double?> CalculatePp(int beatmapId, SoloScoreInfo score, CancellationToken ct) => (await Calculate(beatmapId, score, ct))?.Pp;

        public async Task<(double starRating, int maxCombo)?> CalculateDifficulty(int beatmapId, int rulesetId, IEnumerable<APIMod> apiMods, CancellationToken ct)
        {
            try
            {
                var ruleset = Ruleset(rulesetId);
                var working = ruleset == null ? null : await load(beatmapId, ct);
                if (working == null) return null;

                var attributes = ruleset!.CreateDifficultyCalculator(working).Calculate(apiMods.Select(m => m.ToMod(ruleset)), ct);
                return (attributes.StarRating, attributes.MaxCombo);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "difficulty calculation failed for beatmap {Id}", beatmapId);
                return null;
            }
        }
    }
}
