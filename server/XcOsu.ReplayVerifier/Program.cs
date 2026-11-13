using Newtonsoft.Json;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;
using osu.Game.Tests;
using osu.Game.Tests.Visual;

// Replays a recorded score through the real gameplay code, headless and faster than real time, and writes what the
// replay actually achieves. The server compares that with what the client claimed; nothing the client computed is reused.
if (args.Length != 3) throw new ArgumentException("Usage: verifier <beatmap.osu> <replay.osr> <result.json>");
Beatmap map;
using (var input = new LineBufferedReader(File.OpenRead(args[0])))
    map = Decoder.GetDecoder<Beatmap>(input).Decode(input);
map.BeatmapInfo.Ruleset = new OsuRuleset().RulesetInfo;
byte[] osr = File.ReadAllBytes(args[1]);
using var host = new CleanRunHeadlessGameHost(realtime: false);
var runner = new OsuTestScene.OsuTestSceneTestRunner();
Exception? failure = null;
var execution = Task.Run(() =>
{
    try
    {
        while (!runner.IsLoaded) Thread.Sleep(10);
        runner.RunTestBlocking(new VerificationScene(map, osr, args[2]));
    }
    catch (Exception e) { failure = e; }
    finally { host.Exit(); }
});
host.Run(runner);
await execution;
// exit codes rather than an unhandled exception: an abort would leave a core dump behind for every bad replay.
if (failure != null) { Console.Error.WriteLine(failure); return 2; }
if (!File.Exists(args[2])) { Console.Error.WriteLine("The simulation produced no result."); return 3; }
return 0;

public partial class VerificationScene : RateAdjustedBeatmapTestScene
{
    protected override Ruleset CreateRuleset() => new OsuRuleset();

    public VerificationScene(Beatmap map, byte[] osr, string output)
    {
        VerificationPlayer? player = null;
        AddStep("load beatmap and replay", () =>
        {
            var working = CreateWorkingBeatmap(map);
            Beatmap.Value = working;
            Ruleset.Value = new OsuRuleset().RulesetInfo;
            var score = new ReplayDecoder(working).Parse(new MemoryStream(osr));
            score.ScoreInfo.Ruleset = new OsuRuleset().RulesetInfo;
            SelectedMods.Value = score.ScoreInfo.Mods;
            LoadScreen(player = new VerificationPlayer(score));
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

    private sealed class ReplayDecoder(WorkingBeatmap working) : LegacyScoreDecoder
    {
        protected override Ruleset GetRuleset(int rulesetId) => rulesetId == 0 ? new OsuRuleset() : throw new NotSupportedException("Only osu! replays are simulated.");
        protected override WorkingBeatmap GetBeatmap(string md5Hash) => working;
    }
}

public partial class VerificationPlayer(Score score) : ReplayPlayer(score, new PlayerConfiguration { AutomaticallySkipIntro = true, ShowLeaderboard = false })
{
    // ReplayPlayer normally suppresses failure until its final frame; verification must use ordinary gameplay rules.
    protected override bool CheckModsAllowFailure() => GameplayState.Mods.OfType<IApplicableFailOverride>().All(m => m.PerformFail());
}
