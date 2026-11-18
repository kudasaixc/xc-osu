// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using osu.Framework.Development;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Statistics;
using osuTK.Graphics.ES30;
using GL = osu.Framework.Platform.Web.WebGL;

namespace osu.Framework.Graphics.OpenGL.Buffers
{
    internal class GLUniformBuffer<TData> : IUniformBuffer<TData>, IGLUniformBuffer
        where TData : unmanaged, IEquatable<TData>
    {
        private readonly GLRenderer renderer;
        private readonly int size;

        private TData data;
        private int uboId;
        private bool allocated;

        public GLUniformBuffer(GLRenderer renderer)
        {
            Trace.Assert(ThreadSafety.IsDrawThread);

            this.renderer = renderer;

            size = Marshal.SizeOf(default(TData));

            GL.GenBuffers(1, out uboId);

            // Initialise the buffer with the default data.
            setData(ref data);
        }

        public TData Data
        {
            get => data;
            set
            {
                if (value.Equals(data))
                    return;

                setData(ref value);
            }
        }

        private void setData(ref TData data)
        {
            this.data = data;

            GL.BindBuffer(BufferTarget.UniformBuffer, uboId);
            // xc-osu!: allocate the storage once, then only overwrite it. Re-specifying it with BufferData on every change (once per
            // masking change: every rounded card, cover or avatar) makes WebGL/ANGLE reallocate the buffer each time, which was the
            // largest draw-thread cost in image-heavy overlays (beatmap listing, profile).
            if (!allocated)
            {
                GL.BufferData(BufferTarget.UniformBuffer, size, ref data, BufferUsageHint.DynamicDraw);
                allocated = true;
            }
            else
                GL.BufferSubData(BufferTarget.UniformBuffer, 0, size, ref data);
            // xc-osu: the generic binding is left as is; everything that uses it binds first, and this runs once per masking change.

            FrameStatistics.Increment(StatisticsCounterType.UniformUpl);
        }

        #region Disposal

        ~GLUniformBuffer()
        {
            renderer.ScheduleDisposal(static b => b.Dispose(false), this);
        }

        public void Dispose()
        {
            renderer.ScheduleDisposal(static v => v.Dispose(true), this);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (uboId == -1)
                return;

            GL.DeleteBuffer(uboId);
            renderer.NotifyBufferDeleted(uboId);
            uboId = -1;
        }

        #endregion

        public int Id => uboId;

        public void Flush()
        {
        }
    }
}
