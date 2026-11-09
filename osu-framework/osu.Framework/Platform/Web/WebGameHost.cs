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

        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface) => new WebWindow();

        protected override Clipboard CreateClipboard() => new WebClipboard();

        protected override void ChooseAndSetupRenderer() => SetupRendererAndWindow(new WebGLRenderer(), GraphicsSurfaceType.OpenGL);

        protected override DrawThread CreateDrawThread(Action onNewFrame) => new WebDrawThread(onNewFrame, this);

        protected override void SetupConfig(IDictionary<FrameworkSetting, object> defaultOverrides)
        {
            defaultOverrides[FrameworkSetting.ExecutionMode] = ExecutionMode.MultiThreaded;
            base.SetupConfig(defaultOverrides);

            // the draw thread is bound to the canvas worker, so single-threaded execution is impossible on the web.
            var executionMode = Config.GetBindable<ExecutionMode>(FrameworkSetting.ExecutionMode);
            executionMode.Value = ExecutionMode.MultiThreaded;
            executionMode.Disabled = true;
        }

        protected override void SetupForRun()
        {
            base.SetupForRun();

            bool verboseLog = DebugUtils.IsDebugBuild || Environment.GetEnvironmentVariable("XC_VERBOSE_LOG") == "1";

            // Mirror the framework log to the browser devtools console (files in the virtual FS are invisible otherwise).
            Logger.NewEntry += entry =>
            {
                if (entry.Target == LoggingTarget.Performance)
                    return;

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
