// xc-osu! reproducible performance scenario (xc softworks / kudasaixc)
//
// Enabled only by the page parameter ?bench (environment variable XC_BENCH, never set by the launcher). Drives the game
// through a fixed sequence so builds can be compared on the same workload (tools/perfbench.mjs reads the markers):
//   menu idle -> settings overlay -> import the bundled "circle the halls" beatmap (christmas2024.osz, the only bundled
//   track with a full difficulty: 1312 objects) -> autoplay (default skin) -> results
//   -> autoplay (classic skin, capped) -> menu idle -> done
// Each phase prints "[bench] {json}" with the wall-clock time, so telemetry windows / spikes can be sliced per phase.

using System;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Menu;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;

namespace osu.Web
{
    public partial class XcOsuGame
    {
        public static string BenchScenario => Environment.GetEnvironmentVariable("XC_BENCH") ?? string.Empty;

        private static void benchMark(string phase, string extra = "")
            => Console.WriteLine("[bench] {\"phase\":\"" + phase + "\",\"at\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + extra + "}");

        private partial class BenchDriver : Component
        {
            private readonly XcOsuGame game;
            private int step;
            private double stepStarted = -1;
            private Task<Live<BeatmapSetInfo>?>? import;
            private BeatmapInfo? map;
            private readonly double playCap;

            public BenchDriver(XcOsuGame game)
            {
                this.game = game;
                // "short" keeps the plays to 45 s (quick iteration), anything else plays the default-skin map in full
                playCap = BenchScenario == "short" ? 45_000 : 180_000;
            }

            private double elapsed => Time.Current - stepStarted;

            private void next(string phase, string extra = "")
            {
                step++;
                stepStarted = Time.Current;
                benchMark(phase, extra);
            }

            protected override void Update()
            {
                base.Update();

                var screen = game.ScreenStack?.CurrentScreen;

                switch (step)
                {
                    case 0:
                        if (screen is MainMenu)
                            next("menu-idle");
                        break;

                    case 1:
                        if (elapsed > 8000)
                        {
                            game.Settings?.Show();
                            next("settings");
                        }

                        break;

                    case 2:
                        if (elapsed > 6000)
                        {
                            game.Settings?.Hide();
                            import = game.BeatmapManager.Import(new ImportTask(game.Resources.GetStream("Tracks/christmas2024.osz"), "christmas2024.osz"));
                            next("import");
                        }

                        break;

                    case 3:
                        if (import?.IsCompleted != true) break;

                        var set = import.IsCompletedSuccessfully ? import.GetResultSafely() : null;
                        map = set?.PerformRead(s => s.Beatmaps.Where(b => b.Ruleset.OnlineID == 0).OrderByDescending(b => b.Length).FirstOrDefault()?.Detach());

                        if (map == null)
                        {
                            benchMark("failed", ",\"why\":\"no playable beatmap in christmas2024.osz\"");
                            step = 99;
                            break;
                        }

                        play("play-default");
                        break;

                    case 4:
                        if (screen is PlayerLoader && elapsed > 60000)
                        {
                            benchMark("failed", ",\"why\":\"the player never started\"");
                            step = 99;
                        }
                        else if (screen is ResultsScreen)
                            next("results");
                        else if (screen is Player && elapsed > playCap)
                        {
                            next("results");
                            game.PerformFromScreen(_ => { }, new[] { typeof(MainMenu) });
                        }

                        break;

                    case 5:
                        if (elapsed > 6000)
                        {
                            game.SkinManager.CurrentSkinInfo.Value = game.SkinManager.DefaultClassicSkin.SkinInfo;
                            play("play-classic");
                        }

                        break;

                    case 6:
                        if (elapsed > Math.Min(playCap, 60_000) || screen is ResultsScreen)
                        {
                            game.PerformFromScreen(_ => { }, new[] { typeof(MainMenu) });
                            next("menu-end");
                        }

                        break;

                    case 7:
                        if (elapsed > 10000)
                        {
                            game.SkinManager.CurrentSkinInfo.Value = osu.Game.Skinning.ArgonSkin.CreateInfo().ToLiveUnmanaged();
                            if (Environment.GetEnvironmentVariable("XC_AOTPROF") == "1")
                                XcAotProfile.XcSaveAotProfileNow();
                            next("done");
                        }

                        break;
                }
            }

            private void play(string phase)
            {
                var working = game.BeatmapManager.GetWorkingBeatmap(map);
                game.Beatmap.Value = working;
                game.Ruleset.Value = map!.Ruleset;
                var autoplay = map.Ruleset.CreateInstance().GetAutoplayMod()!;
                game.SelectedMods.Value = new Mod[] { autoplay };

                game.PerformFromScreen(s => s.Push(new PlayerLoader(() => new ReplayPlayer((b, m) => autoplay.CreateScoreFromReplayData(b, m)))),
                    new[] { typeof(MainMenu) });
                next(phase, $",\"map\":\"{map.DifficultyName.Replace("\"", "")}\",\"length\":{System.Math.Round(map.Length / 1000)}");
            }
        }
    }
}
