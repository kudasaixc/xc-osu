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
        private readonly bool useRing;

        private TData data;
        private int uboId;
        private int ringOffset;
        private ulong ringFrameIndex;
        private bool allocated;

        public GLUniformBuffer(GLRenderer renderer)
        {
            Trace.Assert(ThreadSafety.IsDrawThread);

            this.renderer = renderer;
            size = Marshal.SizeOf(default(TData));
            useRing = renderer.UseUniformRing;

            if (!useRing)
            {
                GL.GenBuffers(1, out uboId);
                setData(ref data);
            }
        }

        public TData Data
        {
            get => data;
            set
            {
                if (value.Equals(data))
                    return;

                if (useRing)
                {
                    data = value;
                    ringFrameIndex = 0;
                    FrameStatistics.Increment(StatisticsCounterType.UniformUpl);
                }
                else
                    setData(ref value);
            }
        }

        private void setData(ref TData data)
        {
            this.data = data;

            renderer.BindUniformBufferTarget(uboId);

            if (!allocated)
            {
                GL.BufferData(BufferTarget.UniformBuffer, size, ref data, BufferUsageHint.DynamicDraw);
                allocated = true;
            }
            else
                GL.BufferSubData(BufferTarget.UniformBuffer, 0, size, ref data);

            FrameStatistics.Increment(StatisticsCounterType.UniformUpl);
        }

        #region Disposal

        ~GLUniformBuffer()
        {
            if (!useRing)
                renderer.ScheduleDisposal(static b => b.Dispose(false), this);
        }

        public void Dispose()
        {
            if (!useRing)
                renderer.ScheduleDisposal(static v => v.Dispose(true), this);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (useRing || uboId == -1)
                return;

            GL.DeleteBuffer(uboId);
            renderer.NotifyBufferDeleted(uboId);
            uboId = -1;
        }

        #endregion

        public int Id => useRing ? renderer.UniformRingBufferId : uboId;

        public int Offset => useRing ? ringOffset : 0;

        public int ByteSize => useRing ? size : 0;

        public void Flush()
        {
            if (!useRing)
                return;

            ulong frame = renderer.FrameIndex;
            if (ringFrameIndex == frame)
                return;

            ringOffset = renderer.AllocateUniformSlice(ref data, size);
            ringFrameIndex = frame;
        }
    }
}
