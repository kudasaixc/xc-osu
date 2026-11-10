// xc-osu! telemetry collector — xc softworks (kudasaixc)
//
// Measures every frame of every game thread and reports, through "[tm] {json}" console lines that the page intercepts
// (js/xc-telemetry.js, never shown in the console):
//   - a window every 10 s (aggregates): per-thread frame interval / work histograms, presented vs. repeated frames, GC, heap;
//   - every stutter individually ("spike", sent every 2 s), with what the game was doing: screen, open overlay, GC in that frame,
//     texture uploads, time since the last screen change;
//   - events: screen changes, overlays opening / closing.
// Enabled by XC_TM=1 (launcher option "telemetry", on by default). Nothing here identifies the player.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using osu.Framework.Platform;
using osu.Framework.Platform.Web;
using osu.Framework.Statistics;
using osu.Framework.Threading;

namespace osu.Web
{
    public static class Telemetry
    {
        public static bool Enabled { get; private set; }

        // stutters leave every tick, so a browser killed mid-session loses 2 s of them at most; aggregates every 5th tick
        private const int tick_ms = 2000;
        private const int ticks_per_window = 5;
        private const int max_spikes = 30;
        private const int max_starve_spikes = 8;

        /// <summary>Upper bounds (ms) of the histogram buckets; the last bucket is open-ended.</summary>
        private static readonly double[] edges = { 0.25, 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 17, 20, 25, 33, 50, 75, 100, 150, 250, 500, 1000 };

        private sealed class Channel
        {
            public readonly string Name;
            public readonly object Lock = new object();

            public int Frames;
            public double IntervalSum, IntervalMax, WorkSum, WorkMax;
            public int[] IntervalHist = new int[edges.Length + 1];
            public int[] WorkHist = new int[edges.Length + 1];

            // draw only
            public int Presented, Empty, Late, EmptyRunMax, Resumes;
            public long TextureUploads;

            // owned by the measured thread
            public long LastTimestamp;
            public int LastGc0, LastGc1;
            public TimeSpan LastPause;
            public int EmptyRun, StarveSpikes;

            public Channel(string name) => Name = name;
        }

        private struct Spike
        {
            public string Thread, Kind, Screen, Overlay;
            public long At;
            public double Ms, Work, GcPause, SinceScreen, SinceOverlay, Scheduler, Tree, Mask, DrawNodes;
            public int Gc0, Gc1, Missed, Tex, TexQueue;
            public bool Playing;
        }

        private static readonly Channel draw = new Channel("draw");
        private static readonly Channel update = new Channel("update");
        private static readonly Channel audio = new Channel("audio");
        private static readonly Channel input = new Channel("input");

        private static readonly object spike_lock = new object();
        private static List<Spike> spikes = new List<Spike>(max_spikes);
        private static int spikesDropped;

        // ---- context (written by the update thread, read by all)
        private static volatile string screen = "boot";
        private static volatile string overlay = string.Empty;
        private static volatile bool playing;
        private static long screenChangedAt, overlayChangedAt;

        /// <summary>Estimated display refresh period (ms): median requestAnimationFrame interval.</summary>
        private static double period = 1000.0 / 60;

        private static readonly double[] periodSamples = new double[64];
        private static int periodCount;

        private static GameHost? host;
        private static GlobalStatistic<int>? texUploads, texQueue;

        [ThreadStatic]
        private static GameThread? currentThread;

        [ThreadStatic]
        private static Channel? currentChannel;

        private static long now() => System.Diagnostics.Stopwatch.GetTimestamp();

        private static double msSince(long timestamp) => timestamp == 0 ? -1 : System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

        private static long unixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static void Start(GameHost gameHost)
        {
            if (Enabled) return;

            host = gameHost;
            Enabled = true;
            screenChangedAt = now();

            WebDrawWorker.FrameObserver = onDrawFrame;
            GameThread.FrameObserver = onThreadFrame;

            new Thread(report) { IsBackground = true, Name = "xc telemetry" }.Start();
            Console.WriteLine("[launch] telemetry on (frame timings, stutters, hardware — no IP, no account; turn it off in the launcher options)");
        }

        // ------------------------------------------------------------------ context

        public static void SetScreen(string? from, string? to)
        {
            if (!Enabled) return;

            double stayed = msSince(Interlocked.Exchange(ref screenChangedAt, now()));
            screen = to ?? "none";
            emit(new Json().Str("k", "e").Str("n", "screen").Num("at", unixMs()).Str("from", from ?? "none").Str("to", to ?? "none").Num("stayed", Math.Round(stayed)));
        }

        public static void SetOverlay(string value)
        {
            if (!Enabled || value == overlay) return;

            string before = overlay;
            overlay = value;
            Interlocked.Exchange(ref overlayChangedAt, now());
            emit(new Json().Str("k", "e").Str("n", "overlay").Num("at", unixMs()).Str("from", before).Str("to", value).Str("scr", screen));
        }

        public static void SetPlaying(bool value) => playing = value;

        // ------------------------------------------------------------------ per-frame observers

        private static int bucket(double ms)
        {
            int i = 0;
            while (i < edges.Length && ms > edges[i]) i++;
            return i;
        }

        /// <summary>Collections and pause time since this thread's previous frame.</summary>
        private static void gcSince(Channel c, out int gc0, out int gc1, out double pauseMs)
        {
            int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1);
            gc0 = c0 - c.LastGc0;
            gc1 = c1 - c.LastGc1;

            if (gc0 != 0 || gc1 != 0)
            {
                c.LastGc0 = c0;
                c.LastGc1 = c1;
            }

            // read every frame: the estimate (see totalPause) may be recorded by the input thread after the collection itself
            var pause = totalPause();
            pauseMs = (pause - c.LastPause).TotalMilliseconds;
            c.LastPause = pause;
        }

        /// <summary>
        /// Mono (the WebAssembly runtime) does not implement <see cref="GC.GetTotalPauseDuration"/>: it always returns zero.
        /// Collections stop every managed thread, so the pause is estimated from the input thread, which otherwise ticks
        /// every 1-2 ms: whenever a collection happened during one of its frames, the time beyond a normal frame is counted.
        /// </summary>
        private static long estimatedPauseUs;

        private static bool pauseEstimated;

        private static TimeSpan totalPause()
        {
            var real = GC.GetTotalPauseDuration();
            if (real > TimeSpan.Zero) return real;

            pauseEstimated = true;
            return TimeSpan.FromMilliseconds(Interlocked.Read(ref estimatedPauseUs) / 1000.0);
        }

        // Fixed ring: every draw frame, no allocation on the normal frame path.
        private struct FlightFrame
        {
            public long At;
            public double Interval, Work, Pause, UpdateWork;
            public int Gc0, Gc1, Tex, Queue;
        }
        private static readonly object flightLock = new object();
        private static readonly FlightFrame[] flightRing = new FlightFrame[4096];
        private static int flightHead, flightCount;
        private static List<FlightFrame>? flight;
        private static long flightTrigger, flightEnd, flightNext;
        private static int flightDropped, flightSuppressed;
        private static double latestUpdateWork; // maximum update work since the previous draw callback
        private static string flightScreen = "", flightOverlay = "", flightThread = "";

        private static void recordFlight(double interval, double work, double pause, int g0, int g1, int tex, int queue)
        {
            var f = new FlightFrame { At = unixMs(), Interval = interval, Work = work, Pause = pause,
                Gc0 = g0, Gc1 = g1, Tex = tex, Queue = queue, UpdateWork = latestUpdateWork };
            lock (flightLock)
            {
                f.UpdateWork = latestUpdateWork;
                latestUpdateWork = 0;
                flightRing[flightHead] = f;
                flightHead = (flightHead + 1) % flightRing.Length;
                flightCount = Math.Min(flightCount + 1, flightRing.Length);
                if (flight != null && f.At <= flightEnd)
                {
                    if (flight.Count < 4096) flight.Add(f);
                    else flightDropped++;
                }
            }
        }

        private static void triggerFlight(string thread, bool severe = false)
        {
            long at = unixMs();
            lock (flightLock)
            {
                if (flight != null || (!severe && at < flightNext)) { flightSuppressed++; return; }
                flight = new List<FlightFrame>(1024);
                flightTrigger = at;
                flightEnd = at + 2000;
                flightNext = at + 10000;
                flightScreen = screen; flightOverlay = overlay; flightThread = thread;
                flightDropped = 0;
                for (int i = 0; i < flightCount; i++)
                {
                    var f = flightRing[(flightHead - flightCount + i + flightRing.Length) % flightRing.Length];
                    if (f.At >= at - 2000) flight.Add(f);
                }
            }
        }

        private static void emitFlight()
        {
            List<FlightFrame>? frames;
            long at;
            int dropped, suppressed;
            string scr, ov, thread;
            lock (flightLock)
            {
                if (flight == null || unixMs() < flightEnd) return;
                frames = flight; flight = null; at = flightTrigger;
                dropped = flightDropped; suppressed = flightSuppressed; flightSuppressed = 0;
                scr = flightScreen; ov = flightOverlay; thread = flightThread;
            }
            int parts = (frames.Count + 99) / 100;
            for (int start = 0; start < frames.Count; start += 100)
            {
                var rows = new StringBuilder("[");
                for (int i = start; i < Math.Min(start + 100, frames.Count); i++)
                {
                    if (i > start) rows.Append(',');
                    var f = frames[i];
                    rows.Append('[').Append(f.At - at);
                    foreach (double v in new double[] { f.Interval, f.Work, f.Pause, f.Gc0, f.Gc1, f.Tex, f.Queue, f.UpdateWork })
                        rows.Append(',').Append(double.IsFinite(v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "null");
                    rows.Append(']');
                }
                emit(new Json().Str("k", "e").Str("n", "flight").Num("at", at).Str("scr", scr).Str("ov", ov)
                    .Str("name", thread).Num("part", start / 100).Num("parts", parts).Num("dropped", dropped).Num("suppressed", suppressed)
                    .Str("cols", "offset,interval,drawWork,gcPause,gc0,gc1,tex,texQueue,maxUpdateWorkSinceDraw")
                    .Raw("frames", rows.Append(']').ToString()));
            }
        }

        private static void addSpike(Channel c, string kind, double ms, double work, int gc0, int gc1, double gcPause, int missed = 0, int tex = 0, int texQueued = 0)
        {
            var s = new Spike
            {
                Thread = c.Name, Kind = kind, At = unixMs(), Ms = ms, Work = work,
                Screen = screen, Overlay = overlay, Playing = playing,
                Gc0 = gc0, Gc1 = gc1, GcPause = gcPause, Missed = missed, Tex = tex, TexQueue = texQueued,
                SinceScreen = msSince(Interlocked.Read(ref screenChangedAt)),
                SinceOverlay = msSince(Interlocked.Read(ref overlayChangedAt)),
                Scheduler = c == update ? currentThread?.LastSchedulerWorkMs ?? 0 : 0,
                Tree = c == update ? host?.LastUpdateTreeMs ?? 0 : 0,
                Mask = c == update ? host?.LastUpdateMaskMs ?? 0 : 0,
                DrawNodes = c == update ? host?.LastUpdateDrawNodesMs ?? 0 : 0,
            };

            lock (spike_lock)
            {
                if (spikes.Count < max_spikes) spikes.Add(s);
                else spikesDropped++;
            }
        }

        private static void onDrawFrame(double interval, double work, bool presented)
        {
            var c = draw;
            gcSince(c, out int gc0, out int gc1, out double gcPause);

            int tex = 0, queued = 0;

            try
            {
                if (texUploads == null && host?.Renderer != null)
                {
                    string group = host.Renderer.GetType().Name;
                    texUploads = GlobalStatistics.Get<int>(group, "Texture uploads performed");
                    texQueue = GlobalStatistics.Get<int>(group, "Texture upload queue length");
                }

                tex = texUploads?.Value ?? 0;
                queued = texQueue?.Value ?? 0;
            }
            catch
            {
            }

            recordFlight(interval, work, gcPause, gc0, gc1, tex, queued);
            double p = period;
            // returning from a hidden tab / suspended page: not a stutter
            bool resume = interval > 1000;
            bool late = !resume && interval > p * 1.6 + 1;
            int missed = late ? Math.Max(1, (int)Math.Round(interval / p) - 1) : 0;
            bool slow = work > p * 0.9;

            // A tab resumption is not a visible stall and must not join two empty-frame runs.
            if (resume) c.EmptyRun = 0;
            int endedRun = 0;

            lock (c.Lock)
            {
                c.Frames++;
                c.WorkSum += work;
                if (work > c.WorkMax) c.WorkMax = work;
                c.WorkHist[bucket(work)]++;
                c.TextureUploads += tex;

                if (resume) c.Resumes++;
                else if (interval > 0)
                {
                    c.IntervalSum += interval;
                    if (interval > c.IntervalMax) c.IntervalMax = interval;
                    c.IntervalHist[bucket(interval)]++;
                    if (late) c.Late++;
                    if (periodCount < periodSamples.Length) periodSamples[periodCount++] = interval;
                }

                if (presented)
                {
                    c.Presented++;
                    endedRun = c.EmptyRun;
                    c.EmptyRun = 0;
                }
                else
                {
                    c.Empty++;
                    c.EmptyRun++;
                    if (c.EmptyRun > c.EmptyRunMax) c.EmptyRunMax = c.EmptyRun;
                }
            }

            // Capture only missed display deadlines or sustained repeats. A slow update/audio
            // callback can still meet the next display deadline and must not consume the capture budget.
            if (StutterCapturePolicy.ShouldCapture(interval, p, c.EmptyRun, endedRun))
                triggerFlight("draw", StutterCapturePolicy.IsSevere(interval, p, c.EmptyRun, endedRun));

            if (late || slow)
                addSpike(c, late ? "late" : "slow", interval, work, gc0, gc1, gcPause, missed, tex, queued);

            // the update thread delivered nothing for one or more refreshes: the previous image stayed on screen
            if (endedRun > 0 && c.StarveSpikes < max_starve_spikes)
            {
                c.StarveSpikes++;
                addSpike(c, "starve", endedRun * p, work, gc0, gc1, gcPause, endedRun, tex, queued);
            }
        }

        private static void onThreadFrame(GameThread thread, double work)
        {
            if (!ReferenceEquals(currentThread, thread))
            {
                currentThread = thread;
                currentChannel = thread.Name switch
                {
                    "Update" => update,
                    "Audio" => audio,
                    "Input" => input,
                    _ => null, // the draw thread is measured from the animation frame (onDrawFrame)
                };
            }

            var c = currentChannel;
            if (c == null) return;
            if (c == update)
            {
                lock (flightLock) latestUpdateWork = Math.Max(latestUpdateWork, work);
            }

            long t = now();
            double interval = c.LastTimestamp == 0 ? 0 : (t - c.LastTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            c.LastTimestamp = t;

            gcSince(c, out int gc0, out int gc1, out double gcPause);

            if (c == input && (gc0 != 0 || gc1 != 0) && interval > 4 && interval < 1000)
                Interlocked.Add(ref estimatedPauseUs, (long)((interval - 2) * 1000));

            lock (c.Lock)
            {
                c.Frames++;
                c.WorkSum += work;
                if (work > c.WorkMax) c.WorkMax = work;
                c.WorkHist[bucket(work)]++;

                if (interval > 0 && interval < 1000)
                {
                    c.IntervalSum += interval;
                    if (interval > c.IntervalMax) c.IntervalMax = interval;
                    c.IntervalHist[bucket(interval)]++;
                }
            }

            double p = period;

            if (work > Math.Max(4, p * 0.5))
                addSpike(c, "slow", interval, work, gc0, gc1, gcPause);
            else if (interval > p * 2 && interval < 1000)
                // the work was short but the frame came late: the limiter overslept or the thread was stopped (GC, scheduling)
                addSpike(c, "gap", interval, work, gc0, gc1, gcPause);
        }

        // ------------------------------------------------------------------ reporting

        private static void report()
        {
            int[] lastGc = new int[3];
            TimeSpan lastPause = totalPause();
            long lastAlloc = GC.GetTotalAllocatedBytes();
            long lastCompleted = ThreadPool.CompletedWorkItemCount;
            long windowStart = now();
            for (int i = 0; i < 3; i++) lastGc[i] = GC.CollectionCount(i);

            int tick = 0;

            while (true)
            {
                Thread.Sleep(tick_ms);

                try
                {
                    emitSpikes();
                    emitFlight();
                    draw.StarveSpikes = 0;

                    if (++tick % ticks_per_window != 0)
                        continue;

                    var j = new Json().Str("k", "w").Num("at", unixMs()).Num("dur", Math.Round(msSince(windowStart)));
                    windowStart = now();

                    j.Str("scr", screen).Str("ov", overlay).Num("play", playing ? 1 : 0);
                    j.Num("sc", Math.Round(msSince(Interlocked.Read(ref screenChangedAt))));

                    j.Raw("th", "{" + string.Join(",", channel(draw, true), channel(update, false), channel(audio, false), channel(input, false)) + "}");
                    j.Num("per", Math.Round(period, 3));

                    var pause = totalPause();
                    long alloc = GC.GetTotalAllocatedBytes();
                    var gc = new Json();
                    for (int i = 0; i < 3; i++)
                    {
                        int n = GC.CollectionCount(i);
                        gc.Num("c" + i, n - lastGc[i]);
                        lastGc[i] = n;
                    }

                    gc.Num("pause", Math.Round((pause - lastPause).TotalMilliseconds, 1));
                    if (pauseEstimated) gc.Num("est", 1); // estimated from input thread stalls, not reported by the runtime
                    gc.Num("heap", Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1));
                    gc.Num("alloc", Math.Round((alloc - lastAlloc) / 1048576.0, 1));
                    lastPause = pause;
                    lastAlloc = alloc;
                    j.Raw("gc", gc.ToString());

                    long completed = ThreadPool.CompletedWorkItemCount;
                    j.Raw("tp", new Json().Num("threads", ThreadPool.ThreadCount).Num("pending", ThreadPool.PendingWorkItemCount).Num("done", completed - lastCompleted).ToString());
                    lastCompleted = completed;

                    emit(j);
                }
                catch (Exception e)
                {
                    // never let telemetry take anything down; one line so a broken probe is still visible
                    Console.WriteLine("[launch] telemetry window skipped: " + e.Message);
                }
            }
        }

        private static void emitSpikes()
        {
            List<Spike> taken;
            int dropped;

            lock (spike_lock)
            {
                if (spikes.Count == 0 && spikesDropped == 0) return;

                taken = spikes;
                spikes = new List<Spike>(max_spikes);
                dropped = spikesDropped;
                spikesDropped = 0;
            }

            var sb = new StringBuilder("[");

            for (int i = 0; i < taken.Count; i++)
            {
                var s = taken[i];
                if (i > 0) sb.Append(',');
                var sj = new Json().Str("th", s.Thread).Str("kd", s.Kind).Num("at", s.At).Num("ms", Math.Round(s.Ms, 2)).Num("w", Math.Round(s.Work, 2))
                                   .Str("scr", s.Screen).Str("ov", s.Overlay).Num("play", s.Playing ? 1 : 0)
                                   .Num("sc", Math.Round(s.SinceScreen));
                if (s.Thread == "update") sj.Num("scheduler", Math.Round(s.Scheduler, 2)).Num("tree", Math.Round(s.Tree, 2))
                    .Num("mask", Math.Round(s.Mask, 2)).Num("drawNodes", Math.Round(s.DrawNodes, 2));
                if (s.SinceOverlay >= 0) sj.Num("so", Math.Round(s.SinceOverlay));
                if (s.Gc0 != 0) sj.Num("g0", s.Gc0);
                if (s.Gc1 != 0) sj.Num("g1", s.Gc1);
                if (s.GcPause > 0) sj.Num("gp", Math.Round(s.GcPause, 2));
                if (s.Missed != 0) sj.Num("miss", s.Missed);
                if (s.Tex != 0) sj.Num("tex", s.Tex);
                if (s.TexQueue != 0) sj.Num("texq", s.TexQueue);
                sb.Append(sj);
            }

            var j = new Json().Str("k", "s").Raw("sp", sb.Append(']').ToString());
            if (dropped > 0) j.Num("spx", dropped);
            emit(j);
        }

        private static string channel(Channel c, bool isDraw)
        {
            var j = new Json();
            int[] ih, wh;

            lock (c.Lock)
            {
                j.Num("n", c.Frames).Num("is", Math.Round(c.IntervalSum, 1)).Num("im", Math.Round(c.IntervalMax, 2))
                 .Num("ws", Math.Round(c.WorkSum, 1)).Num("wm", Math.Round(c.WorkMax, 2));

                if (isDraw)
                {
                    j.Num("pres", c.Presented).Num("empty", c.Empty).Num("late", c.Late).Num("erun", c.EmptyRunMax).Num("tex", c.TextureUploads);
                    if (c.Resumes > 0) j.Num("resume", c.Resumes);

                    if (periodCount >= 16)
                    {
                        Array.Sort(periodSamples, 0, periodCount);
                        double median = periodSamples[periodCount / 2];
                        if (median > 2 && median < 100) period = median;
                    }

                    periodCount = 0;
                    c.Presented = c.Empty = c.Late = c.EmptyRunMax = c.Resumes = 0;
                    c.TextureUploads = 0;
                }

                ih = c.IntervalHist;
                wh = c.WorkHist;
                c.IntervalHist = new int[edges.Length + 1];
                c.WorkHist = new int[edges.Length + 1];
                c.Frames = 0;
                c.IntervalSum = c.IntervalMax = c.WorkSum = c.WorkMax = 0;
            }

            j.Raw("ih", hist(ih)).Raw("wh", hist(wh));
            return "\"" + c.Name + "\":" + j;
        }

        /// <summary>Bucket counts with the trailing zeros dropped.</summary>
        private static string hist(int[] h)
        {
            int last = h.Length - 1;
            while (last >= 0 && h[last] == 0) last--;

            var sb = new StringBuilder("[");

            for (int i = 0; i <= last; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(h[i].ToString(CultureInfo.InvariantCulture));
            }

            return sb.Append(']').ToString();
        }

        private static void emit(Json j) => Console.WriteLine("[tm] " + j);

        /// <summary>Minimal JSON object writer (no serializer: nothing for the trimmer to break).</summary>
        private sealed class Json
        {
            private readonly StringBuilder sb = new StringBuilder("{");

            private Json key(string k)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append('"').Append(k).Append("\":");
                return this;
            }

            public Json Num(string k, double v)
            {
                key(k);
                sb.Append(double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "null");
                return this;
            }

            public Json Num(string k, long v)
            {
                key(k);
                sb.Append(v.ToString(CultureInfo.InvariantCulture));
                return this;
            }

            public Json Raw(string k, string json)
            {
                key(k);
                sb.Append(json);
                return this;
            }

            public Json Str(string k, string v)
            {
                key(k);
                sb.Append('"');

                foreach (char ch in v)
                {
                    switch (ch)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        default:
                            if (ch < 0x20) sb.Append(' ');
                            else sb.Append(ch);
                            break;
                    }
                }

                sb.Append('"');
                return this;
            }

            public override string ToString() => sb + "}";
        }
    }
}
