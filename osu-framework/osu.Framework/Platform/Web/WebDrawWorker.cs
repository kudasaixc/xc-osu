// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks)

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Platform.Web
{
    /// <summary>
    /// Owns the web worker that holds the OffscreenCanvas + WebGL2 context. The framework's <see cref="DrawThread"/>
    /// is pumped from here once per display refresh (requestAnimationFrame), so frames are presented with no extra copies.
    /// </summary>
    public static class WebDrawWorker
    {
        private static readonly ConcurrentQueue<(Action action, ManualResetEventSlim? done, Exception?[] error)> work = new ConcurrentQueue<(Action, ManualResetEventSlim?, Exception?[])>();
        private static readonly ManualResetEventSlim contextReady = new ManualResetEventSlim();

        private static volatile WebDrawThread? drawThread;

        public static Thread? Thread { get; private set; }

        public static int ContextHandle { get; private set; }

        public static double LastFrameTime { get; private set; }

        // diagnostics, read from other threads (see WebWindow heartbeat)
        public static volatile string Stage = "not started";
        public static long Loops, Pumped;

        public static string DiagnosticSummary => $"stage={Stage} loops={Interlocked.Read(ref Loops)} pumped={Interlocked.Read(ref Pumped)} drawThread={drawThread?.State.Value.ToString() ?? "none"} queued={work.Count}";

        private static int started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref started, 1) == 1)
                return;

            workerTask = WebNative.RunInWebWorker(loop);
            workerTask.ContinueWith(t =>
            {
                if (t.Exception != null)
                    Console.Error.WriteLine("xc-osu! draw worker crashed: " + t.Exception);
            });
        }

        public static void WaitForContext() => contextReady.Wait(Timeout.Infinite);

        /// <summary>
        /// Run an action on the draw worker and block until it has executed.
        /// </summary>
        public static void Invoke(Action action)
        {
            if (Thread.CurrentThread == Thread)
            {
                action();
                return;
            }

            using var done = new ManualResetEventSlim();
            var error = new Exception?[1];
            work.Enqueue((action, done, error));
            done.Wait(Timeout.Infinite);

            if (error[0] != null)
                throw new AggregateException("Draw worker invocation failed", error[0]!);
        }

        public static void Post(Action action) => work.Enqueue((action, null, new Exception?[1]));

        internal static void Attach(WebDrawThread thread) => drawThread = thread;

        internal static void Detach(WebDrawThread thread)
        {
            if (drawThread == thread)
                drawThread = null;
        }

        private static void runQueuedWork()
        {
            while (work.TryDequeue(out var item))
            {
                try
                {
                    Stage = "queued work: " + item.action.Method.DeclaringType?.Name + "." + item.action.Method.Name;
                    item.action();
                }
                catch (Exception e)
                {
                    item.error[0] = e;
                    if (item.done == null) Logger.Error(e, "Draw worker posted work failed");
                }
                finally
                {
                    item.done?.Set();
                }
            }
        }

        private static async Task loop()
        {
            Thread = Thread.CurrentThread;
            Thread.Name = GameThread.SuffixedThreadNameFor("Draw");

            // the JSWebWorker's own context: GameThread.MakeCurrent installs osu's GameThreadSynchronizationContext,
            // which would capture our rAF continuations into a queue nothing drains.
            var workerContext = SynchronizationContext.Current;

            Stage = "requesting canvas";
            WebNative.xc_request_canvas();

            int handle;
            Stage = "attaching GL";
            while ((handle = WebNative.xc_gl_attach()) == 0)
                await WebNative.RequestAnimationFrame().ConfigureAwait(true);

            if (handle < 0)
                throw new PlatformNotSupportedException("WebGL2 is not available in this browser.");

            ContextHandle = handle;
            contextReady.Set();

            bool legacy = Environment.GetEnvironmentVariable("XC_DRAW_LEGACY") == "1";
            Logger.Log($"Draw worker running (GL context {handle}, {(legacy ? "awaited rAF" : "direct rAF pump")})");

            if (!legacy)
            {
                // The frame runs synchronously inside the browser's animation frame callback. Awaiting a rAF promise instead
                // (legacy path below) adds a promise -> Task -> synchronization context hop per frame, which regularly lands
                // after the frame deadline and shows up as dropped / repeated frames.
                rafCallback = time =>
                {
                    noteRaf(time);
                    frame(workerContext);
                    Stage = "awaiting rAF";
                };
                WebNative.RequestAnimationFrameLoop(rafCallback);

                // keeps the worker (and its JS event loop) alive for the lifetime of the page
                // Must stay rooted: an unreachable pending task lets the GC finalise the whole
                // JSWebWorker ("JSWebWorker was disposed while running" => FailFast).
                keepAlive = new TaskCompletionSource<bool>();
                await keepAlive.Task.ConfigureAwait(true);
                return;
            }

            while (true)
            {
                frame(workerContext);
                Stage = "awaiting rAF";
                noteRaf(await WebNative.RequestAnimationFrame().ConfigureAwait(true));
            }
        }

        // rooted: the JS side holds the only other reference to the delegate
        private static Action<double>? rafCallback;
        private static TaskCompletionSource<bool>? keepAlive;
        private static Task? workerTask;

        private static void frame(SynchronizationContext? workerContext)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                var shared = WebShared.Instance;

                Interlocked.Increment(ref Loops);
                Stage = "resize";
                WebNative.xc_canvas_resize(Math.Max(1, shared.Read(WebShared.WIDTH)), Math.Max(1, shared.Read(WebShared.HEIGHT)));

                Stage = "queued work";
                runQueuedWork();

                var dt = drawThread;

                if (dt != null)
                {
                    try
                    {
                        Stage = "pump " + dt.State.Value;
                        dt.PumpFrame();
                        Interlocked.Increment(ref Pumped);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Draw frame failed");
                    }
                }
            }
            catch (Exception e)
            {
                // never let an exception escape into the JS animation frame callback
                Console.Error.WriteLine("xc-osu! draw worker frame failed: " + e);
            }
            finally
            {
                if (SynchronizationContext.Current != workerContext)
                    SynchronizationContext.SetSynchronizationContext(workerContext);

                double work = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                lock (stats_lock)
                {
                    statWork += work;
                    if (work > statWorkMax) statWorkMax = work;
                }

                try
                {
                    FrameObserver?.Invoke(lastInterval, work, framePresented);
                }
                catch
                {
                }

                lastInterval = 0;
                framePresented = false;
            }
        }

        // ---- frame pacing statistics (shown by the perf probe / overlay) ----
        private static readonly object stats_lock = new object();
        private static int statFrames, statLate, statPresented, statEmpty;
        private static double statInterval, statIntervalMax, statWork, statWorkMax, statBest = double.MaxValue;

        /// <summary>
        /// xc-osu! telemetry hook, invoked on the draw worker after every animation frame: the interval since the previous
        /// requestAnimationFrame callback (ms, 0 when unknown), the time spent in the frame (ms) and whether a new frame
        /// was presented (false = the update thread had nothing new, the previous image stays on screen).
        /// </summary>
        public static Action<double, double, bool>? FrameObserver;

        // draw worker only
        private static double lastInterval;
        private static bool framePresented;

        private static void noteRaf(double time)
        {
            double last = LastFrameTime;
            LastFrameTime = time;

            if (last <= 0)
                return;

            double interval = time - last;
            lastInterval = interval;

            lock (stats_lock)
            {
                statFrames++;
                statInterval += interval;
                if (interval > statIntervalMax) statIntervalMax = interval;
                if (interval < statBest && interval > 1) statBest = interval;
                // a "late" frame took at least half a refresh longer than the shortest interval seen in this window
                if (statBest < double.MaxValue && interval > statBest * 1.5) statLate++;
            }
        }

        /// <summary>Called by the host after every draw frame: whether a new frame was presented or nothing new was available.</summary>
        internal static void NoteFrame(bool presented)
        {
            if (presented) framePresented = true;

            lock (stats_lock)
            {
                if (presented) statPresented++;
                else statEmpty++;
            }
        }

        /// <summary>
        /// Frame pacing over the period since the last call: rAF callbacks, average / worst interval, late callbacks, frames
        /// presented vs. skipped because the update thread had nothing new, and time spent drawing.
        /// </summary>
        public static string TakeStats()
        {
            lock (stats_lock)
            {
                string result = statFrames == 0
                    ? "raf=idle"
                    : FormattableString.Invariant($"raf={statFrames} avg={statInterval / statFrames:0.00}ms max={statIntervalMax:0.0}ms late={statLate} presented={statPresented} empty={statEmpty} work={statWork / Math.Max(1, statPresented + statEmpty):0.00}ms/max{statWorkMax:0.0}");

                statFrames = statLate = statPresented = statEmpty = 0;
                statInterval = statIntervalMax = statWork = statWorkMax = 0;
                statBest = double.MaxValue;
                return result;
            }
        }
    }

    internal class WebDrawThread : DrawThread
    {
        public WebDrawThread(Action onNewFrame, GameHost host)
            : base(onNewFrame, host)
        {
        }

        protected override void PrepareForWork()
        {
            // No native thread is created; the draw worker pumps this thread.
            Thread = WebDrawWorker.Thread;
            WebDrawWorker.Attach(this);
        }

        internal void PumpFrame()
        {
            switch (State.Value)
            {
                case GameThreadState.Starting:
                    Initialize(false);
                    break;

                case GameThreadState.Running:
                    RunSingleFrame();
                    break;

                case GameThreadState.Paused:
                case GameThreadState.Exited:
                    WebDrawWorker.Detach(this);
                    break;
            }
        }
    }
}
