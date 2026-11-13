// xc-osu!: osu!lazer running natively in the browser.
// Web port by xc softworks (kudasaixc). osu!lazer by ppy Pty Ltd, MIT licence.

using System;
using System.Linq;
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
            Console.WriteLine("xc-osu! by xc softworks (kudasaixc): booting osu!lazer on WebAssembly");

            // Main runs on the .NET deputy thread; the game host (input thread) gets its own pthread so that
            // blocking waits never touch the browser UI thread.
            var hostThread = new Thread(run) { Name = "xc-osu host", IsBackground = false };
            hostThread.Start();
        }

        private static int perfInterval = 2000;

        private static string env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

        /// <summary>
        /// Echo the options the launcher passed in, and apply the ones that live in the runtime itself.
        /// </summary>
        private static void applyLaunchOptions()
        {
            Console.WriteLine($"[launch] runtime {Environment.Version}, {Environment.ProcessorCount} logical cores, server GC={System.Runtime.GCSettings.IsServerGC}, latency={System.Runtime.GCSettings.LatencyMode}");

            string gc = env("MONO_GC_PARAMS");
            Console.WriteLine("[launch] MONO_GC_PARAMS=" + (gc.Length > 0 ? gc : "(defaults)"));

            foreach (string key in new[] { "XC_FRAMESYNC", "XC_MAX_DRAW_HZ", "XC_MAX_UPDATE_HZ", "XC_INACTIVE_HZ", "XC_LOG", "XC_THREADPOOL_MAX", "DOTNET_PROCESSOR_COUNT" })
            {
                if (env(key).Length > 0)
                    Console.WriteLine($"[launch] {key}={env(key)}");
            }

            if (int.TryParse(env("XC_THREADPOOL_MAX"), out int maxThreads) && maxThreads > 0)
            {
                ThreadPool.GetMaxThreads(out _, out int io);
                bool ok = ThreadPool.SetMaxThreads(maxThreads, io);
                ThreadPool.GetMaxThreads(out int worker, out _);
                Console.WriteLine($"[launch] thread pool max workers -> {worker}" + (ok ? string.Empty : $" (request for {maxThreads} rejected: must be >= {Environment.ProcessorCount})"));
            }

            if (int.TryParse(env("XC_PERF_MS"), out int ms) && ms >= 250)
                perfInterval = ms;
        }

        private static void startPerfProbe(GameHost host)
        {
            new Thread(() =>
            {
                int[] lastCount = new int[3];
                TimeSpan lastPause = TimeSpan.Zero;

                while (true)
                {
                    Thread.Sleep(perfInterval);

                    try
                    {
                        probe(host, lastCount, ref lastPause);
                    }
                    catch (Exception)
                    {
                        // threads / clocks do not exist yet while the host is still starting: skip the sample, never die
                    }
                }
            }) { IsBackground = true, Name = "perf probe" }.Start();
        }

        private static void probe(GameHost host, int[] lastCount, ref TimeSpan lastPause)
        {
            {
                {
                    var sb = new System.Text.StringBuilder("[perf]");

                    foreach (var t in host.Threads.ToArray())
                    {
                        if (t?.Clock == null) continue;

                        sb.Append($" {t.Name}={t.Clock.FramesPerSecond:0}fps/{t.Clock.ElapsedFrameTime:0.0}ms");
                    }

                    var pause = GC.GetTotalPauseDuration();
                    sb.Append($" gc0+{GC.CollectionCount(0) - lastCount[0]} gc1+{GC.CollectionCount(1) - lastCount[1]} gc2+{GC.CollectionCount(2) - lastCount[2]}");
                    sb.Append($" pause+{(pause - lastPause).TotalMilliseconds:0}ms heap={GC.GetTotalMemory(false) / 1048576}MB alloc/s={GC.GetTotalAllocatedBytes() / 1048576}MB-total");

                    sb.Append(' ').Append(WebDrawWorker.TakeStats());

                    for (int i = 0; i < 3; i++) lastCount[i] = GC.CollectionCount(i);
                    lastPause = pause;
                    Console.WriteLine(sb.ToString());
                }
            }
        }

        private static void run()
        {
            try
            {
                // Not disposed: a browser tab never "exits" the host (CanExit is false), and disposing a running host
                // would throw and mask the real startup exception.
                applyLaunchOptions();

                if (Environment.GetEnvironmentVariable("XC_REALM_TRACE") == "1")
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
                if (Environment.GetEnvironmentVariable("XC_TM") == "1")
                    Telemetry.Start(host);

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
