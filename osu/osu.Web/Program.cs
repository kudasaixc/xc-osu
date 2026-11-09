// xc-osu! — osu!lazer running natively in the browser.
// Web port by xc softworks (kudasaixc). osu!lazer by ppy Pty Ltd, MIT licence.

using System;
using System.Threading;
using osu.Framework;
using osu.Framework.Platform;
using osu.Framework.Platform.Web;

namespace osu.Web
{
    public static class Program
    {
        public const string GAME_NAME = "xc-osu";

        public static void Main(string[] args)
        {
            Console.WriteLine("xc-osu! by xc softworks (kudasaixc) — booting osu!lazer on WebAssembly");

            // Main runs on the .NET deputy thread; the game host (input thread) gets its own pthread so that
            // blocking waits never touch the browser UI thread.
            var hostThread = new Thread(run) { Name = "xc-osu host", IsBackground = false };
            hostThread.Start();
        }

        private static void startPerfProbe(GameHost host)
        {
            new Thread(() =>
            {
                int[] lastCount = new int[3];
                TimeSpan lastPause = TimeSpan.Zero;

                while (true)
                {
                    Thread.Sleep(2000);

                    var sb = new System.Text.StringBuilder("[perf]");

                    foreach (var t in host.Threads)
                        sb.Append($" {t.Name}={t.Clock.FramesPerSecond:0}fps/{t.Clock.ElapsedFrameTime:0.0}ms");

                    var pause = GC.GetTotalPauseDuration();
                    sb.Append($" gc0+{GC.CollectionCount(0) - lastCount[0]} gc1+{GC.CollectionCount(1) - lastCount[1]} gc2+{GC.CollectionCount(2) - lastCount[2]}");
                    sb.Append($" pause+{(pause - lastPause).TotalMilliseconds:0}ms heap={GC.GetTotalMemory(false) / 1048576}MB alloc/s={GC.GetTotalAllocatedBytes() / 1048576}MB-total");

                    for (int i = 0; i < 3; i++) lastCount[i] = GC.CollectionCount(i);
                    lastPause = pause;
                    Console.WriteLine(sb.ToString());
                }
            }) { IsBackground = true, Name = "perf probe" }.Start();
        }

        private static void run()
        {
            try
            {
                // Not disposed: a browser tab never "exits" the host (CanExit is false), and disposing a running host
                // would throw and mask the real startup exception.
                if (Environment.GetEnvironmentVariable("XC_VERBOSE_LOG") == "1")
                {
                    Realms.Logging.RealmLogger.Default = Realms.Logging.RealmLogger.Function(m => Console.WriteLine("[realm] " + m));
                    Realms.Logging.RealmLogger.SetLogLevel(Realms.Logging.LogLevel.Trace);
                }

                // Rulesets are discovered from loaded assemblies (there is no DLL directory to scan in the browser).
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(osu.Game.Rulesets.Osu.OsuRuleset).TypeHandle);

                if (Environment.GetEnvironmentVariable("XC_DIAG") == "1")
                    Diag.Run();

                GameHost host = new WebGameHost(GAME_NAME, new HostOptions { FriendlyGameName = "xc-osu!" });
                if (Environment.GetEnvironmentVariable("XC_PERF") == "1")
                    startPerfProbe(host);

                host.Run(new XcOsuGame());
                Console.WriteLine("xc-osu! host loop returned (state " + host.ExecutionState + ")");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("xc-osu! fatal: " + e);
                WebNative.UiCall(UiOp.Exit, 1, 0, e.ToString());
            }
        }
    }
}
