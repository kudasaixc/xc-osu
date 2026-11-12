// xc-osu! server — difficulty / pp using osu!'s own calculators. xc softworks (kudasaixc)

using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
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
        private static readonly SemaphoreSlim calculationSlots = new(2, 2);
        private static readonly SemaphoreSlim queueSlots = new(64, 64);
        private static readonly MemoryCache results = new(new MemoryCacheOptions { SizeLimit = 2048 });
        private static string cacheKey(string kind, int beatmap, object input) => kind + beatmap + ":" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(input))));
        private static async Task<IDisposable> reserve(CancellationToken ct)
        {
            if (!await queueSlots.WaitAsync(0, ct)) throw new Scores.SubmitException("Calculation queue is full. Please retry.", 429);
            try { await calculationSlots.WaitAsync(ct); }
            catch { queueSlots.Release(); throw; }
            return new Reservation();
        }
        private sealed class Reservation : IDisposable
        {
            public void Dispose() { calculationSlots.Release(); queueSlots.Release(); }
        }
        private static void cache(string key, object value) => results.Set(key, value,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
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
            var beatmap = osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(stream).Decode(stream);
            beatmap.BeatmapInfo.Ruleset = RULESETS[0].RulesetInfo;
            return new FlatWorkingBeatmap(beatmap);
        }

        public sealed record ScoreCalculation(double? Pp, double StarRating, int MaxCombo, double Length);

        /// <summary>pp + difficulty of a submitted score, using osu!'s own calculators. Null when the beatmap can't be loaded.</summary>
        public async Task<ScoreCalculation?> Calculate(int beatmapId, SoloScoreInfo score, CancellationToken ct)
        {
            string key = cacheKey("score:", beatmapId, score);
            if (results.TryGetValue(key, out ScoreCalculation? cached)) return cached;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            ct = timeout.Token;
            using var reservation = await reserve(ct);
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
                var result = new ScoreCalculation(perf?.Total, attributes.StarRating, attributes.MaxCombo, length / 1000 / rate);
                cache(key, result);
                return result;
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
            var mods = apiMods.ToArray();
            string key = cacheKey("difficulty:", beatmapId, new { rulesetId, mods });
            if (results.TryGetValue(key, out ValueTuple<double, int> cached)) return cached;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            ct = timeout.Token;
            using var reservation = await reserve(ct);
            try
            {
                var ruleset = Ruleset(rulesetId);
                var working = ruleset == null ? null : await load(beatmapId, ct);
                if (working == null) return null;

                var attributes = ruleset!.CreateDifficultyCalculator(working).Calculate(mods.Select(m => m.ToMod(ruleset)), ct);
                var result = (attributes.StarRating, attributes.MaxCombo);
                cache(key, result);
                return result;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "difficulty calculation failed for beatmap {Id}", beatmapId);
                return null;
            }

        }
    }
}
