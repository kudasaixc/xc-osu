// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks)

using osu.Framework.Extensions.EnumExtensions;
using System;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Tablet;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osu.Framework.Platform.Web;
using osu.Framework.Statistics;
using osuTK;

namespace osu.Framework.Platform.Web
{
    /// <summary>
    /// Graphics tablet support for the browser, mirroring <see cref="OpenTabletDriverHandler"/>'s absolute output mode:
    /// configurable tablet area (size / offset in mm), rotation, output area and pressure threshold with hysteresis.
    /// <para>
    /// Sources: WebHID raw digitizer reports (true tablet coordinates, Chromium) when the user connects a tablet,
    /// otherwise Pointer Events from the OS driver, whose screen-mapped coordinates are treated as the digitizer.
    /// </para>
    /// </summary>
    public class WebTabletHandler : InputHandler, ITabletHandler
    {
        private static readonly GlobalStatistic<ulong> statistic_total_events = GlobalStatistics.Get<ulong>(StatisticGroupFor<WebTabletHandler>(), "Total events");

        public override string Description => "Tablet";

        public override bool IsActive => tablet.Value != null;

        public Bindable<Vector2> AreaOffset { get; } = new Bindable<Vector2>();

        public Bindable<Vector2> AreaSize { get; } = new Bindable<Vector2>();

        public Bindable<Vector2> OutputAreaOffset { get; } = new Bindable<Vector2>(new Vector2(0.5f, 0.5f));

        public Bindable<Vector2> OutputAreaSize { get; } = new Bindable<Vector2>(new Vector2(1f, 1f));

        public Bindable<float> Rotation { get; } = new Bindable<float>();

        public BindableFloat PressureThreshold { get; } = new BindableFloat
        {
            MinValue = 0f,
            MaxValue = 1f,
            Precision = 0.005f,
        };

        public IBindable<TabletInfo?> Tablet => tablet;

        private readonly Bindable<TabletInfo?> tablet = new Bindable<TabletInfo?>();

        /// <summary>
        /// Default virtual digitizer width (mm) when only OS-mapped pointer events are available.
        /// </summary>
        private const float virtual_width_mm = 152f;

        private WebWindow window = null!;
        private IBindable<bool> windowActive = null!;

        private bool rawSource;
        private bool penPressed;
        private int lastButtons;

        public override bool Initialize(GameHost host)
        {
            if (!base.Initialize(host))
                return false;

            if (host.Window is not WebWindow webWindow)
                return false;

            window = webWindow;
            windowActive = window.IsActive.GetBoundCopy();

            window.TabletIdentified += onTabletIdentified;

            Enabled.BindValueChanged(e =>
            {
                if (e.NewValue)
                    window.PenSample += onPenSample;
                else
                {
                    window.PenSample -= onPenSample;
                    setPressed(false);
                }
            }, true);

            return true;
        }

        private void onTabletIdentified(string name, Vector2 sizeMm)
        {
            if (string.IsNullOrEmpty(name))
            {
                rawSource = false;
                tablet.Value = null;
                setPressed(false);
                return;
            }

            rawSource = sizeMm.X > 0;
            setDigitizer(name, sizeMm.X > 0 ? sizeMm : virtualDigitizerSize());
        }

        private Vector2 virtualDigitizerSize()
        {
            var display = window.CurrentDisplayBindable.Value.Bounds;
            float aspect = display.Width > 0 ? (float)display.Height / display.Width : 9 / 16f;
            return new Vector2(virtual_width_mm, MathF.Round(virtual_width_mm * aspect, 1));
        }

        private void setDigitizer(string name, Vector2 size)
        {
            AreaSize.Default = size;
            AreaOffset.Default = size / 2;

            // if it's clear the user has not configured the area, take the full area from the tablet that was just found.
            if (AreaSize.Value == Vector2.Zero)
                AreaSize.SetDefault();

            if (AreaOffset.Value == Vector2.Zero)
                AreaOffset.SetDefault();

            tablet.Value = new TabletInfo(name, size);
        }

        private void onPenSample(WebPenSample sample)
        {
            bool raw = sample.Flags.HasFlagFast(WebPenFlags.Raw);

            // when a raw (WebHID) source is connected, ignore the duplicate OS pointer stream.
            if (rawSource && !raw)
                return;

            // pens on touchscreens (direct input) are handled as touches by the pen handler.
            if (sample.Flags.HasFlagFast(WebPenFlags.Direct))
                return;

            if (tablet.Value == null)
                setDigitizer("Pen tablet (OS mapping)", virtualDigitizerSize());

            Vector2 digitizer = tablet.Value!.Size;
            Vector2 pos = mapToWindow(sample.Normalised * digitizer);

            enqueueInput(new MousePositionAbsoluteInputFromPen { Position = pos, DeviceType = TabletPenDeviceType.Indirect });

            if (sample.Flags.HasFlagFast(WebPenFlags.InRange))
                setPressure(sample.Flags.HasFlagFast(WebPenFlags.Contact) ? Math.Max(sample.Pressure, 0.0001f) : 0);
            else
                setPressed(false);

            int changed = sample.Buttons ^ lastButtons;

            if (changed != 0)
            {
                // bit0 = tip (handled by pressure), bit1 = barrel 1, bit2 = barrel 2 (eraser / secondary)
                for (int i = 1; i < 8; i++)
                {
                    if ((changed & (1 << i)) == 0) continue;

                    enqueueInput(new TabletPenButtonInput((TabletPenButton)(i - 1), (sample.Buttons & (1 << i)) != 0));
                }

                lastButtons = sample.Buttons;
            }
        }

        /// <summary>
        /// Maps a digitizer position (mm) through the configured input area (with rotation) to the output area in window pixels.
        /// Equivalent to OpenTabletDriver's AbsoluteOutputMode with area clipping.
        /// </summary>
        private Vector2 mapToWindow(Vector2 digitizerPos)
        {
            Vector2 areaSize = AreaSize.Value;
            if (areaSize.X <= 0 || areaSize.Y <= 0) areaSize = tablet.Value!.Size;

            Vector2 relative = digitizerPos - AreaOffset.Value;

            float rad = -MathHelper.DegreesToRadians(Rotation.Value);
            float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
            relative = new Vector2(relative.X * cos - relative.Y * sin, relative.X * sin + relative.Y * cos);

            Vector2 normalised = new Vector2(relative.X / areaSize.X, relative.Y / areaSize.Y); // -0.5 .. 0.5 within the area

            normalised = Vector2.Clamp(normalised, new Vector2(-0.5f), new Vector2(0.5f));

            var clientSize = window.ClientSize;
            Vector2 windowSize = new Vector2(clientSize.Width, clientSize.Height);
            Vector2 scaledSize = windowSize * OutputAreaSize.Value;
            Vector2 offsetFromCenter = (OutputAreaOffset.Value - new Vector2(0.5f, 0.5f)) * (windowSize - scaledSize);
            Vector2 outputCentre = windowSize / 2 + offsetFromCenter;

            return outputCentre + normalised * scaledSize;
        }

        private void setPressure(float pressure)
        {
            const float hysteresis_half = 0.02f;

            pressure = Math.Clamp(pressure, 0f, 1f);

            float releaseThreshold = PressureThreshold.Value - hysteresis_half;
            float pressThreshold = PressureThreshold.Value + hysteresis_half;

            if (releaseThreshold < 0f)
            {
                pressThreshold = hysteresis_half * 2;
                releaseThreshold = 0f;
            }
            else if (pressThreshold > 1f)
            {
                releaseThreshold = 1 - (hysteresis_half * 2);
                pressThreshold = 1f;
            }

            setPressed(penPressed ? pressure > releaseThreshold : pressure > pressThreshold);
        }

        private void setPressed(bool pressed)
        {
            if (pressed == penPressed)
                return;

            enqueueInput(new MouseButtonInputFromPen(pressed) { DeviceType = TabletPenDeviceType.Indirect });
            penPressed = pressed;
        }

        private void enqueueInput(IInput input)
        {
            if (!windowActive.Value)
                return;

            PendingInputs.Enqueue(input);
            FrameStatistics.Increment(StatisticsCounterType.TabletEvents);
            statistic_total_events.Value++;
        }
    }

    /// <summary>
    /// Direct pen input (pen on touchscreen) as touches, and indirect pen input with cursor sensitivity when the tablet handler is disabled.
    /// Mirrors the framework's SDL3 <c>PenHandler</c>.
    /// </summary>
    public class WebPenHandler : InputHandler, IHasCursorSensitivity
    {
        private static readonly GlobalStatistic<ulong> statistic_total_events = GlobalStatistics.Get<ulong>(StatisticGroupFor<WebPenHandler>(), "Total events");

        public override string Description => "Pen";

        public override bool IsActive => true;

        public BindableDouble Sensitivity { get; } = new BindableDouble(1)
        {
            MinValue = 0.1,
            MaxValue = 10,
            Precision = 0.01
        };

        private WebWindow window = null!;
        private GameHost host = null!;
        private bool pressed;

        public override bool Initialize(GameHost host)
        {
            if (!base.Initialize(host))
                return false;

            if (host.Window is not WebWindow webWindow)
                return false;

            this.host = host;
            window = webWindow;

            Enabled.BindValueChanged(e =>
            {
                if (e.NewValue)
                    window.PenSample += onPenSample;
                else
                    window.PenSample -= onPenSample;
            }, true);

            return true;
        }

        public override void Reset()
        {
            Sensitivity.SetDefault();
            base.Reset();
        }

        private bool tabletHandlerActive()
        {
            foreach (var h in host.AvailableInputHandlers)
            {
                if (h is WebTabletHandler t && t.Enabled.Value)
                    return true;
            }

            return false;
        }

        private void onPenSample(WebPenSample sample)
        {
            bool direct = sample.Flags.HasFlagFast(WebPenFlags.Direct);
            bool contact = sample.Flags.HasFlagFast(WebPenFlags.Contact);

            if (!direct && tabletHandlerActive())
                return;

            if (direct)
            {
                if (contact || pressed)
                    enqueueInput(new TouchInput(new Touch(TouchSource.PenTouch, sample.WindowPosition), contact));

                pressed = contact;
                return;
            }

            enqueueInput(new MousePositionAbsoluteInputFromPen { DeviceType = TabletPenDeviceType.Indirect, Position = applySensitivity(sample.WindowPosition) });

            if (contact != pressed)
            {
                pressed = contact;
                enqueueInput(new MouseButtonInputFromPen(contact) { DeviceType = TabletPenDeviceType.Indirect });
            }
        }

        private Vector2 applySensitivity(Vector2 position)
        {
            var size = window.ClientSize;
            var centre = new Vector2(size.Width, size.Height) * 0.5f;
            return position + (position - centre) * (float)(Sensitivity.Value - 1);
        }

        private void enqueueInput(IInput input)
        {
            PendingInputs.Enqueue(input);
            FrameStatistics.Increment(StatisticsCounterType.TabletEvents);
            statistic_total_events.Value++;
        }
    }
}
