// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks)

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;
using System.Threading.Tasks;

namespace osu.Framework.Platform.Web
{
    /// <summary>
    /// Native glue (osu.Web/Native/xcweb.c) and browser runtime helpers.
    /// </summary>
    public static partial class WebNative
    {
        private const string lib = "xcweb";

        [DllImport(lib)] internal static extern void xc_request_canvas();
        [DllImport(lib)] internal static extern int xc_gl_attach();
        [DllImport(lib)] internal static extern void xc_canvas_resize(int w, int h);
        [DllImport(lib)] internal static extern void xc_attach_shared(IntPtr shared, int size);
        [DllImport(lib, CharSet = CharSet.Ansi)] private static extern void xc_ui_call(int op, int a, int b, IntPtr str);

        public static bool IsBrowser => OperatingSystem.IsBrowser();

        /// <summary>
        /// Fire-and-forget request executed on the browser UI thread (see wwwroot/js/xc-ui.js, <c>xcUi</c>).
        /// </summary>
        public static void UiCall(UiOp op, int a = 0, int b = 0, string? s = null)
        {
            if (s == null)
            {
                xc_ui_call((int)op, a, b, IntPtr.Zero);
                return;
            }

            IntPtr p = Marshal.StringToCoTaskMemUTF8(s);

            try
            {
                xc_ui_call((int)op, a, b, p);
            }
            finally
            {
                Marshal.FreeCoTaskMem(p);
            }
        }

        [JSImport("globalThis.xcRaf")]
        internal static partial Task<double> RequestAnimationFrame();

        /// <summary>
        /// Installs a permanent requestAnimationFrame loop on the calling worker; <paramref name="callback"/> runs synchronously
        /// inside every animation frame (no promise / task hop between the display refresh and the draw).
        /// </summary>
        [JSImport("globalThis.xcRafLoop")]
        internal static partial void RequestAnimationFrameLoop([JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> callback);

        /// <summary>
        /// Runs <paramref name="body"/> on a new dedicated web worker thread which owns a JS event loop (internal runtime API).
        /// </summary>
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, "System.Runtime.InteropServices.JavaScript.JSWebWorker", "System.Runtime.InteropServices.JavaScript")]
        [UnconditionalSuppressMessage("Trimming", "IL2026")]
        [UnconditionalSuppressMessage("Trimming", "IL2075")]
        public static Task RunInWebWorker(Func<Task> body)
        {
            var type = typeof(JSHost).Assembly.GetType("System.Runtime.InteropServices.JavaScript.JSWebWorker", true)!;
            var method = type.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(Func<Task>) })!;
            return (Task)method.Invoke(null, new object[] { body })!;
        }
    }

    public enum UiOp
    {
        SetTitle = 1,
        SetCursorVisible = 2,
        SetPointerLock = 3,
        SetFullscreen = 4,
        OpenUrl = 5,
        SetClipboardText = 6,
        StartTextInput = 7,
        StopTextInput = 8,
        PresentFile = 9,
        Exit = 10,
        SetCursorConfined = 11,
        Vibrate = 12,
        RequestFiles = 13,
        DownloadFile = 14,
        StartGateReady = 15,
    }

    /// <summary>
    /// Shared memory block written by the browser UI thread and read by the input thread.
    /// Layout must match wwwroot/js/xc-ui.js.
    /// </summary>
    internal sealed unsafe class WebShared
    {
        public const int HEADER_INTS = 32;
        public const int EVENT_INTS = 8;
        public const int RING_CAPACITY = 8192;
        public const int TEXT_CAPACITY = 1 << 17; // UTF-16 code units, circular

        public const int MAGIC = 0, WIDTH = 1, HEIGHT = 2, DPR_MILLI = 3, FOCUSED = 4, VISIBLE = 5, CURSOR_INSIDE = 6, POINTER_LOCKED = 7,
                         FULLSCREEN = 8, REFRESH_MILLIHZ = 9, CAPS_LOCK = 10, SCREEN_W = 11, SCREEN_H = 12, RING_CAP = 13, WRITE_INDEX = 14,
                         READ_INDEX = 15, KEYBOARD_ATTACHED = 16, CLIPBOARD_SEQ = 17, START_GATE = 20;

        public static readonly WebShared Instance = new WebShared();

        public readonly int* Base;
        public readonly int SizeBytes;

        private WebShared()
        {
            SizeBytes = (HEADER_INTS + RING_CAPACITY * EVENT_INTS) * sizeof(int) + TEXT_CAPACITY * sizeof(char);
            Base = (int*)NativeMemory.AllocZeroed((nuint)SizeBytes);
            Base[MAGIC] = 0x0C05;
            Base[RING_CAP] = RING_CAPACITY;
            Base[WIDTH] = 1280;
            Base[HEIGHT] = 720;
            Base[DPR_MILLI] = 1000;
            Base[FOCUSED] = 1;
            Base[VISIBLE] = 1;
            Base[REFRESH_MILLIHZ] = 60000;
        }

        private bool attached;

        public void Attach()
        {
            if (attached) return;

            attached = true;
            WebNative.xc_attach_shared((IntPtr)Base, SizeBytes);
        }

        /// <summary>
        /// Read a string written by the UI thread into the circular text area.
        /// </summary>
        public string ReadText(int start, int length)
        {
            char* text = (char*)(Base + HEADER_INTS + RING_CAPACITY * EVENT_INTS);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = text[(start + i) % TEXT_CAPACITY];
            return new string(chars);
        }

        public int Read(int slot) => Volatile.Read(ref Base[slot]);
        public float ReadFloat(int slot) => BitConverter.Int32BitsToSingle(Volatile.Read(ref Base[slot]));

        /// <summary>
        /// Dequeue one 8-int event. Returns false when the ring is empty.
        /// </summary>
        public bool TryDequeue(int* dest)
        {
            int read = Base[READ_INDEX];
            int write = Volatile.Read(ref Base[WRITE_INDEX]);

            if (read == write)
                return false;

            int* ev = Base + HEADER_INTS + (read % RING_CAPACITY) * EVENT_INTS;
            for (int i = 0; i < EVENT_INTS; i++)
                dest[i] = ev[i];

            Volatile.Write(ref Base[READ_INDEX], read + 1);
            return true;
        }
    }
}
