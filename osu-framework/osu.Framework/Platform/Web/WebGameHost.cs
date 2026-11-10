// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks)

using System;
using System.Collections.Generic;
using osu.Framework.Configuration;
using osu.Framework.Development;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Keyboard;
using osu.Framework.Input.Handlers.Mouse;
using osu.Framework.Input.Handlers.Touch;
using osu.Framework.Logging;
using osu.Framework.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Platform.Web
{
    /// <summary>
    /// Game host running inside a browser (WebAssembly, multithreaded runtime).
    /// <list type="bullet">
    /// <item>Draw thread: dedicated web worker owning an OffscreenCanvas + WebGL2 context, paced by requestAnimationFrame.</item>
    /// <item>Update / audio / input threads: regular .NET threads (web workers backed by SharedArrayBuffer).</item>
    /// </list>
    /// </summary>
    public class WebGameHost : SDLGameHost
    {
        public const string STORAGE_ROOT = "/xc";

        public WebGameHost(string gameName, HostOptions? options = null)
            : base(gameName, options)
        {
            WebDrawWorker.Start();
        }

        public override bool CanExit => false;

        public override IEnumerable<string> UserStoragePaths => new[] { STORAGE_ROOT };

        public override Storage GetStorage(string path) => new NativeStorage(path, this);

        protected override Storage GetDefaultGameStorage() => GetStorage(STORAGE_ROOT).GetStorageForDirectory(Name);

        public override bool OpenFileExternally(string filename)
        {
            WebNative.UiCall(UiOp.PresentFile, s: filename);
            return true;
        }

        public override bool PresentFileExternally(string filename) => OpenFileExternally(filename);

        public override void OpenUrlExternally(string url) => WebNative.UiCall(UiOp.OpenUrl, s: url);

        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface) => new WebWindow
        {
            ScheduleToUpdate = action =>
            {
                var scheduler = UpdateThread?.Scheduler;
                if (scheduler != null) scheduler.Add(action);
                else action();
            },
        };

        protected override Clipboard CreateClipboard() => new WebClipboard();

        protected override void ChooseAndSetupRenderer() => SetupRendererAndWindow(new WebGLRenderer(), GraphicsSurfaceType.OpenGL);

        protected override DrawThread CreateDrawThread(Action onNewFrame) => new WebDrawThread(onNewFrame, this);

        // The draw worker runs once per requestAnimationFrame. Spinning there for a new update frame (stock behaviour, up to
        // 100 ms) makes it miss display refreshes whenever the update thread hitches; without a new frame it simply keeps the
        // previous one on screen. XC_DRAW_WAIT=1 restores the stock wait for A/B comparisons.
        private static readonly bool draw_wait = Environment.GetEnvironmentVariable("XC_DRAW_WAIT") == "1";

        protected override int DrawReadTimeout => draw_wait ? base.DrawReadTimeout : 0;

        protected override void DrawFrame()
        {
            base.DrawFrame();
            WebDrawWorker.NoteFrame(DidRenderFrame);
        }

        protected override void SetupConfig(IDictionary<FrameworkSetting, object> defaultOverrides)
        {
            defaultOverrides[FrameworkSetting.ExecutionMode] = ExecutionMode.MultiThreaded;
            base.SetupConfig(defaultOverrides);

            // the draw thread is bound to the canvas worker, so single-threaded execution is impossible on the web.
            var executionMode = Config.GetBindable<ExecutionMode>(FrameworkSetting.ExecutionMode);
            executionMode.Value = ExecutionMode.MultiThreaded;
            executionMode.Disabled = true;

            // xc-osu! launcher overrides (only present when chosen on the launch screen)
            if (Enum.TryParse(Environment.GetEnvironmentVariable("XC_FRAMESYNC"), true, out FrameSync frameSync))
            {
                Config.SetValue(FrameworkSetting.FrameSync, frameSync);
                Logger.Log($"launcher: frame limiter forced to {frameSync}");
            }

            if (launchHz("XC_INACTIVE_HZ") is double inactive)
            {
                MaximumInactiveHz = inactive;
                Logger.Log($"launcher: inactive rate capped at {inactive} Hz");
            }
        }

        private static double? launchHz(string name)
            => double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hz) && hz > 0 ? hz : null;

        private readonly double? maxDrawHz = launchHz("XC_MAX_DRAW_HZ");
        private readonly double? maxUpdateHz = launchHz("XC_MAX_UPDATE_HZ");

        protected override void AdjustFrameLimits(ref double drawHz, ref double updateHz)
        {
            if (maxDrawHz is double d) drawHz = Math.Min(drawHz, d);
            // Native unlimited mode can rebuild the entire UI/draw tree ~1000 times per second
            // for a 60 Hz canvas. Leave CPU time for the draw worker, asset loading and browser.
            // An explicit launcher ceiling remains authoritative (including values above 240).
            updateHz = Math.Min(updateHz, maxUpdateHz ?? 240);
        }

        protected override void SetupForRun()
        {
            base.SetupForRun();

            // XC_LOG: "verbose" = everything for the whole session, "boot" = everything until the menu shows up (the launch
            // screen renders those lines), anything else = Important and above. XC_VERBOSE_LOG=1 (?log) is the legacy switch.
            string logMode = Environment.GetEnvironmentVariable("XC_LOG") ?? string.Empty;
            bool verboseLog = DebugUtils.IsDebugBuild || Environment.GetEnvironmentVariable("XC_VERBOSE_LOG") == "1" || logMode == "verbose" || logMode == "boot";
            bool bootSignalled = false;
            bool bootOnly = logMode == "boot" && !DebugUtils.IsDebugBuild && Environment.GetEnvironmentVariable("XC_VERBOSE_LOG") != "1";

            // Mirror the framework log to the browser devtools console (files in the virtual FS are invisible otherwise).
            Logger.NewEntry += entry =>
            {
                if (entry.Target == LoggingTarget.Performance)
                    return;

                // the intro (or the main menu when the intro is skipped) means the boot is over; the launch screen waits for
                // this line, so it is printed whatever the log level
                if (!bootSignalled && entry.Message.Contains(" entered ", StringComparison.Ordinal)
                                   && (entry.Message.Contains(" entered Intro", StringComparison.Ordinal) || entry.Message.Contains(" entered MainMenu", StringComparison.Ordinal)))
                {
                    bootSignalled = true;
                    if (bootOnly) verboseLog = false;
                    Console.WriteLine($"[{entry.LoggerName ?? entry.Target?.ToString()}] {entry.Message}");
                    Console.WriteLine("[launch] boot complete" + (bootOnly ? ", console back to important messages only" : string.Empty));
                    return;
                }

                if (entry.Level < LogLevel.Important && !verboseLog && entry.Exception == null)
                    return;

                string line = $"[{entry.LoggerName ?? entry.Target?.ToString()}] {entry.Message}";
                if (entry.Exception != null) line += "\n" + entry.Exception;

                if (entry.Level == LogLevel.Error) Console.Error.WriteLine(line);
                else Console.WriteLine(line);
            };

            Logger.Log($"xc-osu! web host starting ({Environment.ProcessorCount} logical cores, {Environment.Version})");
        }

        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers()
        {
            yield return new KeyboardHandler();
            yield return new WebTabletHandler();
            yield return new WebPenHandler();
            yield return new MouseHandler();
            yield return new TouchHandler();
        }
    }

    internal class WebClipboard : Clipboard
    {
        public override string? GetText()
        {
            var shared = WebShared.Instance;
            int len = shared.Read(WebShared.CLIPBOARD_SEQ + 2);
            return len <= 0 ? null : shared.ReadText(shared.Read(WebShared.CLIPBOARD_SEQ + 1), len);
        }

        public override void SetText(string text) => WebNative.UiCall(UiOp.SetClipboardText, s: text);

        public override Image<TPixel>? GetImage<TPixel>() => null;

        public override bool SetImage(Image image) => false;
    }
}
