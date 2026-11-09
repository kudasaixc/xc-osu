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
    internal static class WebDrawWorker
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

            WebNative.RunInWebWorker(loop).ContinueWith(t =>
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

        public static void Attach(WebDrawThread thread) => drawThread = thread;

        public static void Detach(WebDrawThread thread)
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

            var shared = WebShared.Instance;
            Logger.Log($"Draw worker running (GL context {handle})");

            while (true)
            {
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

                if (SynchronizationContext.Current != workerContext)
                    SynchronizationContext.SetSynchronizationContext(workerContext);

                Stage = "awaiting rAF";
                LastFrameTime = await WebNative.RequestAnimationFrame().ConfigureAwait(true);
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
