// xc-osu!: runtime self-test for the browser threading primitives (enable with ?diag). xc softworks (kudasaixc)

using System;
using System.Reflection;
using System.Threading;

namespace osu.Web
{
    internal static class Diag
    {
        private static uint lockThreadId()
        {
            var t = typeof(Lock).GetNestedType("ThreadId", BindingFlags.NonPublic);
            var f = t?.GetField("t_threadId", BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) is uint u ? u : 0xDEAD;
        }

        private static void report(string where) =>
            Console.WriteLine($"[diag] {where}: managed={Environment.CurrentManagedThreadId} name={Thread.CurrentThread.Name} lockTid={lockThreadId()}");

        public static void Run()
        {
            report("host");
            var l = new Lock();
            long counter = 0;
            var threads = new Thread[4];

            for (int i = 0; i < threads.Length; i++)
            {
                int n = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        lock (l) { }
                        report($"worker{n}");
                        for (int k = 0; k < 20000; k++)
                            lock (l) counter++;
                    }
                    catch (Exception e) { Console.WriteLine($"[diag] worker{n} FAILED: {e.GetType().Name}: {e.Message}"); }
                }) { Name = $"diag{n}" };
                threads[i].Start();
            }

            foreach (var t in threads) t.Join();
            Console.WriteLine($"[diag] lock counter {counter} (expect 80000)");

            using var fired = new ManualResetEventSlim();
            try
            {
                using var timer = new Timer(_ => { report("timer callback"); fired.Set(); }, null, 50, Timeout.Infinite);
                Console.WriteLine($"[diag] timer fired: {fired.Wait(5000)}");
            }
            catch (Exception e) { Console.WriteLine($"[diag] timer FAILED: {e}"); }
        }
    }
}
