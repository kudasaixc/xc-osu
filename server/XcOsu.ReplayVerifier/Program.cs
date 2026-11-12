using Newtonsoft.Json;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Replays;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Replays;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Tests;
using osu.Game.Tests.Visual;

if (args.Length != 2) throw new ArgumentException("Usage: verifier <job.json> <result.json>");
var job = JsonConvert.DeserializeObject<Job>(File.ReadAllText(args[0]))!;
using var input = new LineBufferedReader(new MemoryStream(Convert.FromBase64String(job.Beatmap)));
var map = Decoder.GetDecoder<Beatmap>(input).Decode(input);
map.BeatmapInfo.Ruleset = new OsuRuleset().RulesetInfo;
using var host = new CleanRunHeadlessGameHost(realtime: false);
var runner = new OsuTestScene.OsuTestSceneTestRunner();
Exception? failure = null;
var execution = Task.Run(() =>
{
    try
    {
        while (!runner.IsLoaded) Thread.Sleep(10);
        runner.RunTestBlocking(new VerificationScene(job, map, args[1]));
    }
    catch (Exception e) { failure = e; }
    finally { host.Exit(); }
});
host.Run(runner);
await execution;
if (failure != null) throw failure;

public sealed record Job(string Beatmap, SoloScoreInfo Score, List<LegacyReplayFrame> Frames);
public partial class VerificationScene : RateAdjustedBeatmapTestScene
{
    protected override Ruleset CreateRuleset() => new OsuRuleset();
    public VerificationScene(Job job, Beatmap map, string output)
    {
        VerificationPlayer? player = null;
        AddStep("load beatmap and replay", () =>
        {
            Beatmap.Value = CreateWorkingBeatmap(map);
            Ruleset.Value = new OsuRuleset().RulesetInfo;
            var ruleset = new OsuRuleset();
            var mods = job.Score.Mods.Select(m => m.ToMod(ruleset)).ToArray();
            SelectedMods.Value = mods;
            var info = job.Score.ToScoreInfo(mods, map.BeatmapInfo);
            info.Ruleset = ruleset.RulesetInfo;
            // Never use the client's FrameHeader to reset the server's score processor.
            var replay = new Replay { Frames = job.Frames.Select(f =>
            {
                var converted = new OsuReplayFrame { Time = f.Time };
                converted.FromLegacy(f, map);
                return (ReplayFrame)converted;
            }).ToList() };
            LoadScreen(player = new VerificationPlayer(new Score { ScoreInfo = info, Replay = replay }));
        });
        AddUntilStep("player loaded", () => player?.IsCurrentScreen() == true);
        AddUntilStep("simulation finished", () => player!.GameplayState.HasCompleted || player.GameplayState.HasFailed);
        AddStep("write independent result", () =>
        {
            var result = new ScoreInfo();
            player!.GameplayState.ScoreProcessor.PopulateScore(result);
            File.WriteAllText(output, JsonConvert.SerializeObject(new
            {
                Passed = player.GameplayState.HasPassed && !player.GameplayState.HasFailed,
                result.TotalScore, result.Accuracy, result.MaxCombo, result.Statistics, result.MaximumStatistics
            }));
        });
    }
}
public partial class VerificationPlayer(Score score) : ReplayPlayer(score, new PlayerConfiguration { AutomaticallySkipIntro = true, ShowLeaderboard = false })
{
    // ReplayPlayer normally suppresses failure until its final frame; verification must use ordinary gameplay rules.
    protected override bool CheckModsAllowFailure() => GameplayState.Mods.OfType<IApplicableFailOverride>().All(m => m.PerformFail());
}
