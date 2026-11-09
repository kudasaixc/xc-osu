using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

static partial class Gl
{
    [DllImport("glue")] public static extern void xc_request_canvas();
    [DllImport("glue")] public static extern int xc_gl_attach();
    [DllImport("glue")] public static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("glue")] public static extern void glClear(uint mask);
}
public static partial class App
{
    static float t;
    static readonly object l = new object();
    static volatile int frames;

    [JSImport("globalThis.xcRaf")] private static partial Task<double> Raf();

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "System.Runtime.InteropServices.JavaScript.JSWebWorker", "System.Runtime.InteropServices.JavaScript")]
    static Task RunInWebWorker(Func<Task> body)
    {
        var type = typeof(JSHost).Assembly.GetType("System.Runtime.InteropServices.JavaScript.JSWebWorker", true)!;
        var m = type.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(Func<Task>) })!;
        return (Task)m.Invoke(null, new object[] { body })!;
    }

    static async Task DrawLoop()
    {
        Console.WriteLine("draw thread=" + Environment.CurrentManagedThreadId);
        Gl.xc_request_canvas();
        int h;
        while ((h = Gl.xc_gl_attach()) == 0) await Raf();
        Console.WriteLine("gl handle=" + h);
        while (true)
        {
            float tt; lock (l) tt = t;
            Gl.glClearColor(MathF.Sin(tt) * .5f + .5f, .2f, .6f, 1); Gl.glClear(0x4000);
            frames++;
            await Raf();
        }
    }

    public static void Main()
    {
        Console.WriteLine("main thread=" + Environment.CurrentManagedThreadId);
        _ = RunInWebWorker(DrawLoop).ContinueWith(t => Console.WriteLine("draw died: " + t.Exception));
        new Thread(() => {
            for (int i = 0; ; i++) { lock (l) t += 0.05f; Thread.Sleep(16); if (i % 60 == 0) Console.WriteLine("update thread=" + Environment.CurrentManagedThreadId + " i=" + i + " frames=" + frames); }
        }) { IsBackground = true }.Start();
    }
}
