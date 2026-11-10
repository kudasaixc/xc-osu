// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks)

using osu.Framework.Extensions.EnumExtensions;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Input;
using osuTK;
using osuTK.Input;
using RectangleF = osu.Framework.Graphics.Primitives.RectangleF;

namespace osu.Framework.Platform.Web
{
    /// <summary>
    /// A window backed by a browser canvas. Implements <see cref="ISDLWindow"/> so the framework's stock
    /// mouse / keyboard / touch handlers (including sensitivity and raw relative input) work unchanged.
    /// </summary>
    internal sealed unsafe class WebWindow : ISDLWindow
    {
        private readonly WebShared shared = WebShared.Instance;
        private readonly WebGLSurface surface = new WebGLSurface();

        IGraphicsSurface IWindow.GraphicsSurface => surface;

        public event Action<JoystickButton>? JoystickButtonDown;
        public event Action<JoystickButton>? JoystickButtonUp;
        public event Action<JoystickAxisSource, float>? JoystickAxisChanged;
        public event Action<Touch>? TouchDown;
        public event Action<Touch>? TouchUp;
        public event Action<Vector2>? MouseMove;
        public event Action<Vector2>? MouseMoveRelative;
        public event Action<MouseButton>? MouseDown;
        public event Action<MouseButton>? MouseUp;
        public event Action<Vector2, bool>? MouseWheel;
        public event Action<Key>? KeyDown;
        public event Action<Key>? KeyUp;
        public event Action<string>? TextInput;
        public event TextEditingDelegate? TextEditing;

        /// <summary>Full pen state sample (pointer events or WebHID raw reports).</summary>
        public event Action<WebPenSample>? PenSample;

        /// <summary>A tablet was identified (name, digitizer size in mm). Empty name = disconnected.</summary>
        public event Action<string, Vector2>? TabletIdentified;

        public event Action? Update;
        public event Action? ExitRequested;
        public event Action? Exited;
        public event Action? Suspended;
        public event Action? Resumed;
        public event Action? LowOnMemory;
        public event Action? Resized;
        public event Action? KeymapChanged;
        public event Action<string>? DragDrop;
        public event Action<WindowState>? WindowStateChanged;
        public event Action<IEnumerable<Display>>? DisplaysChanged;

        public Bindable<CursorState> CursorStateBindable { get; } = new Bindable<CursorState>();

        public CursorState CursorState
        {
            get => CursorStateBindable.Value;
            set => CursorStateBindable.Value = value;
        }

        public RectangleF? CursorConfineRect { get; set; }

        private readonly BindableBool cursorInWindow = new BindableBool(true);
        public IBindable<bool> CursorInWindow => cursorInWindow;

        private readonly BindableBool isActive = new BindableBool(true);
        public IBindable<bool> IsActive => isActive;

        public bool MouseAutoCapture { set { } }

        private bool relativeMouseMode;

        /// <summary>
        /// Relative mode = browser pointer lock. A browser cannot warp the OS cursor, so the stock behaviour (leave relative
        /// mode when the cursor crosses the window edge and hand the position back to the OS) makes the cursor reappear
        /// wherever the lock started. Pointer lock is therefore only taken while the cursor is confined (fullscreen, or
        /// gameplay with the default confine setting): the game cursor is then clamped to the canvas and never escapes.
        /// Everywhere else the regular absolute cursor is used and can leave the canvas freely.
        /// </summary>
        public bool RelativeMouseMode
        {
            get => relativeMouseMode;
            set
            {
                value &= CursorState.HasFlagFast(CursorState.Confined);

                if (relativeMouseMode == value) return;

                relativeMouseMode = value;
                WebNative.UiCall(UiOp.SetPointerLock, value ? 1 : 0);
            }
        }

        public bool CapsLockPressed => shared.Read(WebShared.CAPS_LOCK) != 0;
        public bool KeyboardAttached => true;

        public WindowState WindowState
        {
            get => windowState;
            set
            {
                if (windowState == value) return;

                windowState = value;
                WebNative.UiCall(UiOp.SetFullscreen, value == WindowState.Fullscreen ? 1 : 0);
                WindowStateChanged?.Invoke(value);
            }
        }

        private WindowState windowState = WindowState.Normal;

        public WindowMode DefaultWindowMode => Configuration.WindowMode.Windowed;

        public BindableSafeArea SafeAreaPadding { get; } = new BindableSafeArea();

        private readonly Bindable<MarginPadding> borderSize = new Bindable<MarginPadding>();
        public IBindable<MarginPadding> BorderSize => borderSize;

        public IEnumerable<WindowMode> SupportedWindowModes => new[] { Configuration.WindowMode.Windowed, Configuration.WindowMode.Borderless, Configuration.WindowMode.Fullscreen };

        public Bindable<WindowMode> WindowMode { get; } = new Bindable<WindowMode>();

        public ImmutableArray<Display> Displays => ImmutableArray.Create(currentDisplay());

        public Display PrimaryDisplay => currentDisplay();

        public Bindable<Display> CurrentDisplayBindable { get; } = new Bindable<Display>();

        private readonly Bindable<DisplayMode> currentDisplayMode = new Bindable<DisplayMode>();
        public IBindable<DisplayMode> CurrentDisplayMode => currentDisplayMode;

        public bool Focused => isActive.Value;

        public Size ClientSize => surface.GetDrawableSize();
        public Point Position => Point.Empty;
        public bool PositionAccurate => true;
        public Size Size => ClientSize;
        public float Scale => shared.Read(WebShared.DPR_MILLI) / 1000f;
        public Size MinSize { get; set; }
        public Size MaxSize { get; set; }
        public bool Resizable { get; set; } = true;

        private string title = string.Empty;

        public string Title
        {
            get => title;
            set
            {
                title = value;
                WebNative.UiCall(UiOp.SetTitle, s: value);
            }
        }

        private volatile bool exitRequested;

        public WebWindow()
        {
            CursorStateBindable.BindValueChanged(e =>
            {
                WebNative.UiCall(UiOp.SetCursorVisible, e.NewValue.HasFlagFast(CursorState.Hidden) ? 0 : 1);
                WebNative.UiCall(UiOp.SetCursorConfined, e.NewValue.HasFlagFast(CursorState.Confined) ? 1 : 0);
            });

            WindowMode.BindValueChanged(e => WindowState = e.NewValue == Configuration.WindowMode.Windowed ? WindowState.Normal : WindowState.Fullscreen);

            CurrentDisplayBindable.Value = currentDisplay();
            currentDisplayMode.Value = currentDisplay().DisplayModes[0];
        }

        private Display? cachedDisplay;
        private int cachedDisplayKey;

        private Display currentDisplay()
        {
            int w = Math.Max(1, shared.Read(WebShared.SCREEN_W));
            int h = Math.Max(1, shared.Read(WebShared.SCREEN_H));
            int hz = shared.Read(WebShared.REFRESH_MILLIHZ);
            int key = HashCode.Combine(w, h, hz);

            if (cachedDisplay != null && key == cachedDisplayKey)
                return cachedDisplay;

            var bounds = new Rectangle(0, 0, w, h);
            var mode = new DisplayMode("RGBA8", bounds.Size, 32, hz / 1000f, 0);
            cachedDisplayKey = key;
            return cachedDisplay = new Display(0, "Browser", bounds, bounds, new[] { mode });
        }

        /// <summary>Runs an action on the update thread (set by the host). Bindables shared with the game must only change there.</summary>
        internal Action<Action>? ScheduleToUpdate;

        private readonly Bindable<ConfineMouseMode> confineMouseMode = new Bindable<ConfineMouseMode>();

        public void SetupWindow(FrameworkConfigManager config)
        {
            config.BindWith(FrameworkSetting.WindowMode, WindowMode);

            // The page always starts windowed (fullscreen needs a click, the launch screen asks for it when enabled) and the
            // browser can leave fullscreen on its own, so the stored mode means nothing here: the real state is mirrored
            // into the setting instead (see syncFullscreen).
            WindowMode.Value = Configuration.WindowMode.Windowed;

            config.BindWith(FrameworkSetting.ConfineMouseMode, confineMouseMode);
            confineMouseMode.BindValueChanged(_ => updateConfineMode());
            WindowMode.BindValueChanged(_ => updateConfineMode(), true);
        }

        private void updateConfineMode()
        {
            bool confine = confineMouseMode.Value == ConfineMouseMode.Always || WindowMode.Value != Configuration.WindowMode.Windowed;

            if (confine)
                CursorStateBindable.Value |= CursorState.Confined;
            else
                CursorStateBindable.Value &= ~CursorState.Confined;
        }

        private int lastFullscreen;

        /// <summary>
        /// Follows fullscreen changes made by the browser (launch screen click, Escape held, F11 handled by the browser,
        /// tab switch): the window mode setting always reflects what is really on screen.
        /// </summary>
        private void syncFullscreen()
        {
            int fullscreen = shared.Read(WebShared.FULLSCREEN);
            if (fullscreen == lastFullscreen) return;

            lastFullscreen = fullscreen;
            var mode = fullscreen != 0 ? Configuration.WindowMode.Fullscreen : Configuration.WindowMode.Windowed;

            void apply()
            {
                // already known to the page: no request must go back to it
                windowState = fullscreen != 0 ? WindowState.Fullscreen : WindowState.Normal;

                if ((WindowMode.Value != Configuration.WindowMode.Windowed) != (fullscreen != 0))
                    WindowMode.Value = mode;

                WindowStateChanged?.Invoke(windowState);
            }

            if (ScheduleToUpdate != null) ScheduleToUpdate(apply);
            else apply();
        }

        public void Create()
        {
            shared.Attach();
        }

        public void Run()
        {
            int lastW = 0, lastH = 0;
            long lastDisplayCheck = 0, lastHeartbeat = 0;
            bool heartbeat = Environment.GetEnvironmentVariable("XC_VERBOSE_LOG") == "1";

            while (!exitRequested)
            {
                processEvents();

                int w = shared.Read(WebShared.WIDTH), h = shared.Read(WebShared.HEIGHT);

                if (w != lastW || h != lastH)
                {
                    lastW = w;
                    lastH = h;
                    Resized?.Invoke();
                }

                long now = Environment.TickCount64;

                if (heartbeat && now - lastHeartbeat > 5000)
                {
                    lastHeartbeat = now;
                    osu.Framework.Logging.Logger.Log($"draw worker: {WebDrawWorker.DiagnosticSummary} canvas={w}x{h}");
                }

                if (now - lastDisplayCheck > 1000)
                {
                    lastDisplayCheck = now;
                    var display = currentDisplay();

                    if (!ReferenceEquals(display, CurrentDisplayBindable.Value))
                    {
                        CurrentDisplayBindable.Value = display;
                        currentDisplayMode.Value = display.DisplayModes[0];
                        DisplaysChanged?.Invoke(Displays);
                    }
                }

                Update?.Invoke();
            }

            Exited?.Invoke();
        }

        private bool wasVisible = true;

        private void processEvents()
        {
            bool focused = shared.Read(WebShared.FOCUSED) != 0;
            if (isActive.Value != focused) isActive.Value = focused;

            bool visible = shared.Read(WebShared.VISIBLE) != 0;

            if (visible != wasVisible)
            {
                wasVisible = visible;
                if (visible) Resumed?.Invoke();
            }

            syncFullscreen();

            int* ev = stackalloc int[WebShared.EVENT_INTS];

            while (shared.TryDequeue(ev))
            {
                float f(int i) => BitConverter.Int32BitsToSingle(ev[i]);

                switch (ev[0])
                {
                    case 1:
                        MouseMove?.Invoke(new Vector2(f(1), f(2)));
                        break;

                    case 2:
                        MouseMoveRelative?.Invoke(new Vector2(f(1), f(2)));
                        break;

                    case 3:
                        MouseDown?.Invoke(toMouseButton(ev[1]));
                        break;

                    case 4:
                        MouseUp?.Invoke(toMouseButton(ev[1]));
                        break;

                    case 5:
                        MouseWheel?.Invoke(new Vector2(f(1), f(2)), ev[3] != 0);
                        break;

                    case 6:
                        if (ev[1] >= 0 && ev[1] < WebKeys.MAP.Length) KeyDown?.Invoke(WebKeys.MAP[ev[1]]);
                        break;

                    case 7:
                        if (ev[1] >= 0 && ev[1] < WebKeys.MAP.Length) KeyUp?.Invoke(WebKeys.MAP[ev[1]]);
                        break;

                    case 8:
                        TextInput?.Invoke(char.ConvertFromUtf32(ev[1]));
                        break;

                    case 9:
                        TouchDown?.Invoke(new Touch((TouchSource)Math.Clamp(ev[1], 0, 9), new Vector2(f(2), f(3))));
                        break;

                    case 10:
                        TouchDown?.Invoke(new Touch((TouchSource)Math.Clamp(ev[1], 0, 9), new Vector2(f(2), f(3))));
                        break;

                    case 11:
                        TouchUp?.Invoke(new Touch((TouchSource)Math.Clamp(ev[1], 0, 9), new Vector2(f(2), f(3))));
                        break;

                    case 12:
                        PenSample?.Invoke(new WebPenSample
                        {
                            WindowPosition = new Vector2(f(1), f(2)),
                            Pressure = f(3),
                            Normalised = new Vector2(f(4), f(5)),
                            Buttons = ev[6],
                            Flags = (WebPenFlags)ev[7],
                        });
                        break;

                    case 25:
                        TabletIdentified?.Invoke(shared.ReadText(ev[1], ev[2]), new Vector2(f(3), f(4)));
                        break;

                    case 16:
                        cursorInWindow.Value = ev[1] != 0;
                        break;

                    case 17:
                        DragDrop?.Invoke(shared.ReadText(ev[1], ev[2]));
                        break;

                    case 18:
                        TextEditing?.Invoke(shared.ReadText(ev[1], ev[2]), ev[3], ev[4]);
                        break;

                    case 19:
                        TextInput?.Invoke(shared.ReadText(ev[1], ev[2]));
                        break;

                    case 20:
                        ExitRequested?.Invoke();
                        break;

                    case 21:
                        LowOnMemory?.Invoke();
                        break;

                    case 22:
                        if (ev[2] != 0) JoystickButtonDown?.Invoke((JoystickButton)ev[1]);
                        else JoystickButtonUp?.Invoke((JoystickButton)ev[1]);
                        break;

                    case 23:
                        JoystickAxisChanged?.Invoke((JoystickAxisSource)ev[1], f(2));
                        break;

                    case 24:
                        KeymapChanged?.Invoke();
                        break;
                }
            }
        }

        private static MouseButton toMouseButton(int domButton)
        {
            switch (domButton)
            {
                default:
                case 0: return MouseButton.Left;
                case 1: return MouseButton.Middle;
                case 2: return MouseButton.Right;
                case 3: return MouseButton.Button1;
                case 4: return MouseButton.Button2;
            }
        }

        public void UpdateMousePosition(Vector2 position)
        {
            // browsers cannot warp the OS cursor; position feedback is applied in relative mode only.
        }

        public void StartTextInput(TextInputProperties properties) => WebNative.UiCall(UiOp.StartTextInput, (int)properties.Type, properties.AllowIme ? 1 : 0);
        public void StopTextInput() => WebNative.UiCall(UiOp.StopTextInput);

        public void SetTextInputRect(RectangleF rectangle)
        {
        }

        public void ResetIme()
        {
        }

        // F11 / Alt+Enter. Goes through the setting so the settings screen and the confine logic follow.
        public void CycleMode() => WindowMode.Value = WindowMode.Value == Configuration.WindowMode.Windowed ? Configuration.WindowMode.Fullscreen : Configuration.WindowMode.Windowed;

        public void OnDraw()
        {
        }

        public void Close() => exitRequested = true;

        public void Raise()
        {
        }

        public void Hide()
        {
        }

        public void Show()
        {
        }

        public void Flash(bool flashUntilFocused = false)
        {
        }

        public void CancelFlash()
        {
        }

        public void EnableScreenSuspension() => WebNative.UiCall(UiOp.Vibrate, -1, 0);
        public void DisableScreenSuspension() => WebNative.UiCall(UiOp.Vibrate, -1, 1);

        public void SetIconFromStream(Stream imageStream)
        {
        }

        public Point PointToClient(Point point) => point;
        public Point PointToScreen(Point point) => point;

        public void Dispose()
        {
        }

        internal void RequestExit() => ExitRequested?.Invoke();

        internal void RaiseSuspended() => Suspended?.Invoke();
    }

    public struct WebPenSample
    {
        /// <summary>Position in window (backing pixel) space as reported by the OS pointer.</summary>
        public Vector2 WindowPosition;

        /// <summary>Position normalised over the full digitizer (raw HID) or the full screen (OS mapping).</summary>
        public Vector2 Normalised;

        public float Pressure;
        public int Buttons;
        public WebPenFlags Flags;
    }

    [Flags]
    public enum WebPenFlags
    {
        None = 0,
        Contact = 1,
        InRange = 2,
        Raw = 4,
        Eraser = 8,
        Direct = 16,
    }
}
