// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using osu.Framework.Extensions.EnumExtensions;
using osu.Framework.Graphics.OpenGL.Batches;
using osu.Framework.Graphics.OpenGL.Buffers;
using osu.Framework.Graphics.OpenGL.Shaders;
using osu.Framework.Graphics.OpenGL.Textures;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Statistics;
using osuTK;
using osuTK.Graphics;
using osuTK.Graphics.ES30;
using GL = osu.Framework.Platform.Web.WebGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using GL4 = osuTK.Graphics.OpenGL;
using Image = SixLabors.ImageSharp.Image;

namespace osu.Framework.Graphics.OpenGL
{
    internal unsafe class GLRenderer : Renderer
    {
        private IOpenGLGraphicsSurface openGLSurface = null!;

        protected internal override bool VerticalSync
        {
            get => openGLSurface.VerticalSync;
            set => openGLSurface.VerticalSync = value;
        }

        protected internal override bool AllowTearing { get; set; }

        public override bool IsDepthRangeZeroToOne => false;
        public override bool IsUvOriginTopLeft => false;
        public override bool IsClipSpaceYInverted => false;

        public bool UseStructuredBuffers { get; private set; }

        /// <summary>
        /// The maximum allowed render buffer size.
        /// </summary>
        public int MaxRenderBufferSize { get; private set; } = 4096; // default value is to allow roughly normal flow in cases we don't have a GL context, like headless CI.

        /// <summary>
        /// Whether the current platform is embedded.
        /// </summary>
        public bool IsEmbedded { get; private set; }

        private int backbufferFramebuffer;

        private readonly Dictionary<string, IGLUniformBuffer> boundUniformBuffers = new Dictionary<string, IGLUniformBuffer>();
        private IGLUniformBuffer? globalUniformBufferBound;

        // xc-osu!: GL state cache to cut redundant wasm -> JS / WebGL2 calls per frame.
        private bool? lastBlendingEnabledState;
        private int lastBlendEqRgb = -1, lastBlendEqAlpha = -1;
        private int lastBlendSrcRgb = -1, lastBlendDstRgb = -1, lastBlendSrcAlpha = -1, lastBlendDstAlpha = -1;
        private int lastColorMask = -1;
        private int lastScissorEnabled = -1;
        private int lastDepthTest = -1;
        private int lastDepthFunc = -1;
        private int lastDepthMask = -1;
        private int lastStencilTest = -1;
        private int lastBoundVertexArray;
        private int lastBoundArrayBuffer;
        private int lastBoundUniformBuffer;
        private int lastActiveGlTextureUnit = -1;
        private int lastBoundFramebuffer = -1;

        private struct UniformSlotState
        {
            public int Id;
            public int Offset;
            public int Size;
        }

        private readonly UniformSlotState[] lastBoundUniformSlots = new UniformSlotState[16];

        #region xc-osu!: per-frame uniform ring buffer (eliminates in-place UBO mutation stalls under ANGLE/D3D11)

        internal bool UseUniformRing { get; private set; }
        internal int UniformRingBufferId { get; private set; }

        private int uniformRingAlignment = 256;
        private int uniformRingCapacity;
        private int uniformRingOffset;
        private int uniformRingUploaded;
        private byte* uniformStagingPtr;

        internal int AllocateUniformSlice<TData>(ref TData data, int size) where TData : unmanaged
        {
            int aligned = ((size + uniformRingAlignment - 1) / uniformRingAlignment) * uniformRingAlignment;
            if (uniformRingOffset + aligned > uniformRingCapacity)
                growUniformRing(uniformRingOffset + aligned);

            int offset = uniformRingOffset;
            fixed (TData* src = &data)
                System.Buffer.MemoryCopy(src, uniformStagingPtr + offset, uniformRingCapacity - offset, size);

            uniformRingOffset = offset + aligned;
            return offset;
        }

        private void growUniformRing(int required)
        {
            int newCap = Math.Max(uniformRingCapacity * 2, 512 * 1024);
            while (newCap < required)
                newCap *= 2;

            byte* newPtr = (byte*)NativeMemory.Alloc((nuint)newCap);
            if (uniformStagingPtr != null)
            {
                if (uniformRingOffset > 0)
                    System.Buffer.MemoryCopy(uniformStagingPtr, newPtr, newCap, uniformRingOffset);
                NativeMemory.Free(uniformStagingPtr);
            }

            uniformStagingPtr = newPtr;
            uniformRingCapacity = newCap;

            // Allocate new GPU storage; existing frame offsets [0..uniformRingOffset) will be uploaded cleanly on next flush.
            BindUniformBufferTarget(UniformRingBufferId);
            GL.BufferData(BufferTarget.UniformBuffer, (IntPtr)uniformRingCapacity, IntPtr.Zero, BufferUsageHint.StreamDraw);
            uniformRingUploaded = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void flushUniformRing()
        {
            if (!UseUniformRing || uniformRingOffset <= uniformRingUploaded)
                return;

            int dirty = uniformRingOffset - uniformRingUploaded;
            BindUniformBufferTarget(UniformRingBufferId);
            GL.BufferSubData(BufferTarget.UniformBuffer, (IntPtr)uniformRingUploaded, (IntPtr)dirty, (IntPtr)(uniformStagingPtr + uniformRingUploaded));
            uniformRingUploaded = uniformRingOffset;
        }

        #endregion

        protected override void Initialise(IGraphicsSurface graphicsSurface)
        {
            if (graphicsSurface.Type != GraphicsSurfaceType.OpenGL)
                throw new InvalidOperationException($"{nameof(GLRenderer)} only supports OpenGL graphics surfaces.");

            openGLSurface = (IOpenGLGraphicsSurface)graphicsSurface;
            openGLSurface.MakeCurrent(openGLSurface.WindowContext);

            backbufferFramebuffer = openGLSurface.BackbufferFramebuffer ?? 0;

            string version = GL.GetString(StringName.Version);
            IsEmbedded = version.Contains("OpenGL ES"); // As defined by https://www.khronos.org/registry/OpenGL-Refpages/es2.0/xhtml/glGetString.xml

            MaxTextureSize = GL.GetInteger(GetPName.MaxTextureSize);
            MaxRenderBufferSize = GL.GetInteger(GetPName.MaxRenderbufferSize);

            GL.Disable(EnableCap.StencilTest);
            GL.Enable(EnableCap.Blend);

            string extensions = GetExtensions();

            Logger.Log($@"GL Initialized
                        GL Version:                 {GL.GetString(StringName.Version)}
                        GL Renderer:                {GL.GetString(StringName.Renderer)}
                        GL Shader Language version: {GL.GetString(StringName.ShadingLanguageVersion)}
                        GL Vendor:                  {GL.GetString(StringName.Vendor)}
                        GL Extensions:              {extensions}");

            UseStructuredBuffers = extensions.Contains(@"GL_ARB_shader_storage_buffer_object") && !FrameworkEnvironment.NoStructuredBuffers;

            // Uniform ring buffer with glBindBufferRange is opt-in only (XC_UBO_RING=1): under ANGLE D3D11,
            // Buffer11::getConstantBufferRange creates/copies per-offset constant buffers across the full ring
            // on every BufferSubData, causing severe GPU stalls in masked UI lists.
            if (RuntimeInfo.IsBrowser && Environment.GetEnvironmentVariable("XC_UBO_RING") == "1")
            {
                int align = GL.GetInteger((GetPName)0x8A34 /* GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT */);
                uniformRingAlignment = Math.Max(align, 16);
                UniformRingBufferId = GL.GenBuffer();
                uniformRingCapacity = 512 * 1024;
                uniformStagingPtr = (byte*)NativeMemory.Alloc((nuint)uniformRingCapacity);
                BindUniformBufferTarget(UniformRingBufferId);
                GL.BufferData(BufferTarget.UniformBuffer, (IntPtr)uniformRingCapacity, IntPtr.Zero, BufferUsageHint.StreamDraw);
                UseUniformRing = true;
            }

            Logger.Log($"{nameof(UseStructuredBuffers)}: {UseStructuredBuffers}");

            openGLSurface.ClearCurrent();
        }

        protected virtual string GetExtensions()
        {
#pragma warning disable CS0618
            GL.GetInteger(All.NumExtensions, out int numExtensions);
#pragma warning restore CS0618

            var extensionsBuilder = new StringBuilder();

            for (int i = 0; i < numExtensions; i++)
                extensionsBuilder.Append($"{GL.GetString(StringNameIndexed.Extensions, i)} ");

            return extensionsBuilder.ToString().TrimEnd();
        }

        protected internal override void BeginFrame(Vector2 windowSize)
        {
            lastBlendingEnabledState = null;
            lastBlendEqRgb = lastBlendEqAlpha = -1;
            lastBlendSrcRgb = lastBlendDstRgb = lastBlendSrcAlpha = lastBlendDstAlpha = -1;
            lastColorMask = -1;
            lastScissorEnabled = -1;
            lastDepthTest = lastDepthFunc = lastDepthMask = -1;
            lastStencilTest = -1;
            lastBoundVertexArray = 0;
            lastBoundArrayBuffer = 0;
            lastBoundUniformBuffer = 0;
            lastActiveGlTextureUnit = -1;
            lastBoundFramebuffer = -1;
            Array.Clear(lastBoundUniformSlots);
            boundUniformBuffers.Clear();
            globalUniformBufferBound = null;

            // Seems to be required on some drivers as the context is lost from the draw thread.
            MakeCurrent();

            if (UseUniformRing)
            {
                uniformRingOffset = 0;
                uniformRingUploaded = 0;
                BindUniformBufferTarget(UniformRingBufferId);
                GL.BufferData(BufferTarget.UniformBuffer, (IntPtr)uniformRingCapacity, IntPtr.Zero, BufferUsageHint.StreamDraw);
            }

            GL.UseProgram(0);

            base.BeginFrame(windowSize);
        }

        protected internal override void FinishFrame()
        {
            base.FinishFrame();
            GL.FlushCommands();
        }

        protected internal override void WaitUntilNextFrameReady()
        {
        }

        protected internal override void MakeCurrent() => openGLSurface.MakeCurrent(openGLSurface.WindowContext);
        protected internal override void ClearCurrent() => openGLSurface.ClearCurrent();
        protected internal override void SwapBuffers() => openGLSurface.SwapBuffers();
        protected internal override void WaitUntilIdle() => GL.Finish();

        #region xc-osu!: vertex buffer recycling

        // Every composite with enough children owns a vertex batch (three, with the triple-buffered draw nodes), and buffers
        // not drawn for RESOURCE_FREE_NO_USAGE_LENGTH frames are freed. Scrolling a list of cards therefore created and deleted
        // VBO + VAO pairs all the time, and under WebGL each creation (createVertexArray, bufferData, attribute setup) is far
        // more expensive than on a native driver. Freed pairs are kept here, keyed by buffer class (vertex type, quad/linear
        // index buffer) and size, and handed to the next buffer of the same kind.
        private readonly Dictionary<(Type, int), Stack<(int vao, int vbo)>> vertexArrayPool = new Dictionary<(Type, int), Stack<(int, int)>>();
        private long pooledVertexBytes;
        private const long max_pooled_vertex_bytes = 32L * 1024 * 1024;

        internal bool TryTakePooledVertexArray(Type kind, int bytes, out int vaoId, out int vboId)
        {
            if (vertexArrayPool.TryGetValue((kind, bytes), out var stack) && stack.TryPop(out var pair))
            {
                pooledVertexBytes -= bytes;
                (vaoId, vboId) = pair;
                return true;
            }

            vaoId = vboId = 0;
            return false;
        }

        internal bool TryPoolVertexArray(Type kind, int bytes, int vaoId, int vboId)
        {
            if (!RuntimeInfo.IsBrowser || pooledVertexBytes + bytes > max_pooled_vertex_bytes)
                return false;

            if (!vertexArrayPool.TryGetValue((kind, bytes), out var stack))
                vertexArrayPool[(kind, bytes)] = stack = new Stack<(int, int)>();

            stack.Push((vaoId, vboId));
            pooledVertexBytes += bytes;
            return true;
        }

        #endregion

        public bool BindVertexArray(int vaoId)
        {
            if (lastBoundVertexArray == vaoId)
                return false;

            lastBoundVertexArray = vaoId;
            GL.BindVertexArray(vaoId);

            FrameStatistics.Increment(StatisticsCounterType.VBufBinds);
            return true;
        }

        /// <summary>
        /// Binds a buffer to the array buffer target, skipping the call when it is already bound.
        /// </summary>
        public void BindArrayBuffer(int vboId)
        {
            if (lastBoundArrayBuffer == vboId)
                return;

            lastBoundArrayBuffer = vboId;
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboId);
        }

        /// <summary>
        /// Binds a buffer to the generic uniform buffer target, skipping the call when it is already bound.
        /// </summary>
        public void BindUniformBufferTarget(int uboId)
        {
            if (lastBoundUniformBuffer == uboId)
                return;

            lastBoundUniformBuffer = uboId;
            GL.BindBuffer(BufferTarget.UniformBuffer, uboId);
        }

        /// <summary>
        /// Forgets a deleted vertex array (deleting the bound one unbinds it).
        /// </summary>
        public void NotifyVertexArrayDeleted(int vaoId)
        {
            if (lastBoundVertexArray == vaoId)
                lastBoundVertexArray = 0;
        }

        /// <summary>
        /// Forgets a deleted buffer, so that a later buffer reusing its name is not mistaken for it.
        /// </summary>
        public void NotifyBufferDeleted(int id)
        {
            if (lastBoundArrayBuffer == id)
                lastBoundArrayBuffer = 0;

            if (lastBoundUniformBuffer == id)
                lastBoundUniformBuffer = 0;

            for (int i = 0; i < lastBoundUniformSlots.Length; i++)
            {
                if (lastBoundUniformSlots[i].Id == id)
                    lastBoundUniformSlots[i] = default;
            }
        }

        protected override void SetShaderImplementation(IShader shader) => GL.UseProgram((GLShader)shader);

        protected override void SetUniformImplementation<T>(IUniformWithValue<T> uniform)
        {
            switch (uniform)
            {
                case IUniformWithValue<bool> b:
                    GL.Uniform1(uniform.Location, b.GetValue() ? 1 : 0);
                    break;

                case IUniformWithValue<int> i:
                    GL.Uniform1(uniform.Location, i.GetValue());
                    break;

                case IUniformWithValue<float> f:
                    GL.Uniform1(uniform.Location, f.GetValue());
                    break;

                case IUniformWithValue<Vector2> v2:
                    GL.Uniform2(uniform.Location, ref v2.GetValueByRef());
                    break;

                case IUniformWithValue<Vector3> v3:
                    GL.Uniform3(uniform.Location, ref v3.GetValueByRef());
                    break;

                case IUniformWithValue<Vector4> v4:
                    GL.Uniform4(uniform.Location, ref v4.GetValueByRef());
                    break;

                case IUniformWithValue<Matrix2> m2:
                    GL.UniformMatrix2(uniform.Location, false, ref m2.GetValueByRef());
                    break;

                case IUniformWithValue<Matrix3> m3:
                    GL.UniformMatrix3(uniform.Location, false, ref m3.GetValueByRef());
                    break;

                case IUniformWithValue<Matrix4> m4:
                    GL.UniformMatrix4(uniform.Location, false, ref m4.GetValueByRef());
                    break;
            }
        }

        protected override void SetUniformBufferImplementation(string blockName, IUniformBuffer buffer)
        {
            if (blockName == "g_GlobalUniforms")
                globalUniformBufferBound = (IGLUniformBuffer)buffer;
            else
                boundUniformBuffers[blockName] = (IGLUniformBuffer)buffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void activateTextureUnit(int unit)
        {
            if (lastActiveGlTextureUnit == unit)
                return;

            lastActiveGlTextureUnit = unit;
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
        }

        protected override bool SetTextureImplementation(INativeTexture? texture, int unit)
        {
            if (texture == null)
            {
                activateTextureUnit(unit);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                return true;
            }

            switch (texture)
            {
                case GLVideoTexture glVideo:
                    if (glVideo.TextureIds == null)
                        return false;

                    for (int i = 0; i < glVideo.TextureIds.Length; i++)
                    {
                        activateTextureUnit(unit + i);
                        GL.BindTexture(TextureTarget.Texture2D, glVideo.TextureIds[i]);
                    }

                    break;

                case GLTexture glTexture:
                    if (glTexture.TextureId <= 0)
                        return false;

                    activateTextureUnit(unit);
                    GL.BindTexture(TextureTarget.Texture2D, glTexture.TextureId);
                    break;
            }

            return true;
        }

        protected override void SetFrameBufferImplementation(IFrameBuffer? frameBuffer)
        {
            int fb = ((GLFrameBuffer?)frameBuffer)?.FrameBuffer ?? backbufferFramebuffer;
            if (lastBoundFramebuffer == fb)
                return;

            lastBoundFramebuffer = fb;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
        }

        protected override void DeleteFrameBufferImplementation(IFrameBuffer frameBuffer)
            => ScheduleDisposal(f =>
            {
                if (lastBoundFramebuffer == f.FrameBuffer)
                    lastBoundFramebuffer = -1;
                GL.DeleteFramebuffer(f.FrameBuffer);
            }, (GLFrameBuffer)frameBuffer);

        protected override void ClearImplementation(ClearInfo clearInfo)
        {
            if (clearInfo.Colour != CurrentClearInfo.Colour)
                GL.ClearColor(clearInfo.Colour);

            if (clearInfo.Depth != CurrentClearInfo.Depth)
            {
                if (IsEmbedded)
                {
                    // GL ES only supports glClearDepthf
                    // See: https://www.khronos.org/registry/OpenGL-Refpages/es3.0/html/glClearDepthf.xhtml
                    GL.ClearDepth((float)clearInfo.Depth);
                }
                else
                {
                    // Older desktop platforms don't support glClearDepthf, so standard GL's double version is used instead
                    // See: https://www.khronos.org/registry/OpenGL-Refpages/gl4/html/glClearDepth.xhtml
                    osuTK.Graphics.OpenGL.GL.ClearDepth(clearInfo.Depth);
                }
            }

            if (clearInfo.Stencil != CurrentClearInfo.Stencil)
                GL.ClearStencil(clearInfo.Stencil);

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void bindUniformBlockToSlot(GLShader glShader, int blockIndex, int slot, IGLUniformBuffer buffer)
        {
            if (glShader.SetUniformBlockBinding(blockIndex, slot))
                GL.UniformBlockBinding(glShader, blockIndex, slot);

            int id = buffer.Id;
            int off = buffer.Offset;
            int sz = buffer.ByteSize;

            if ((uint)slot < (uint)lastBoundUniformSlots.Length)
            {
                ref var cur = ref lastBoundUniformSlots[slot];
                if (cur.Id == id && cur.Offset == off && cur.Size == sz)
                    return;

                cur.Id = id;
                cur.Offset = off;
                cur.Size = sz;
            }

            // Per GLES 3.0 spec, BindBufferBase / BindBufferRange also binds the buffer to the generic GL_UNIFORM_BUFFER target.
            lastBoundUniformBuffer = id;

            if (sz > 0)
                GL.BindBufferRange(BufferRangeTarget.UniformBuffer, slot, id, (IntPtr)off, (IntPtr)sz);
            else
                GL.BindBufferBase(BufferRangeTarget.UniformBuffer, slot, id);
        }

        public override void DrawVerticesImplementation(PrimitiveTopology topology, int vertexStart, int verticesCount)
        {
            var glShader = (GLShader)Shader!;
            int currentUniformBinding = 0;
            int currentStorageBinding = 0;

            var globalBuf = globalUniformBufferBound;
            int globalIdx = globalBuf != null ? glShader.GlobalUniformBlockIndex : -1;
            if (globalIdx >= 0)
                globalBuf!.Flush();

            if (boundUniformBuffers.Count > 0)
            {
                foreach ((string name, IGLUniformBuffer buffer) in boundUniformBuffers)
                {
                    if (glShader.GetUniformBlockIndex(name) is not int)
                        continue;

                    buffer.Flush();
                }
            }

            // Upload all newly written uniform ring slices before binding ranges and drawing.
            flushUniformRing();

            if (globalIdx >= 0)
            {
                bindUniformBlockToSlot(glShader, globalIdx, currentUniformBinding, globalBuf!);
                currentUniformBinding++;
            }

            if (boundUniformBuffers.Count > 0)
            {
                foreach ((string name, IGLUniformBuffer buffer) in boundUniformBuffers)
                {
                    if (glShader.GetUniformBlockIndex(name) is not int index)
                        continue;

                    if (buffer is IGLShaderStorageBufferObject && UseStructuredBuffers)
                    {
                        GL4.GL.ShaderStorageBlockBinding(glShader, index, currentStorageBinding);
                        GL4.GL.BindBufferBase(GL4.BufferRangeTarget.ShaderStorageBuffer, currentStorageBinding, buffer.Id);
                        currentStorageBinding++;
                    }
                    else
                    {
                        bindUniformBlockToSlot(glShader, index, currentUniformBinding, buffer);
                        currentUniformBinding++;
                    }
                }
            }

            GL.DrawElements(GLUtils.ToPrimitiveType(topology), verticesCount, DrawElementsType.UnsignedShort, vertexStart * sizeof(ushort));
        }

        protected override void SetScissorStateImplementation(bool enabled)
        {
            int state = enabled ? 1 : 0;
            if (lastScissorEnabled == state)
                return;

            lastScissorEnabled = state;
            if (enabled)
                GL.Enable(EnableCap.ScissorTest);
            else
                GL.Disable(EnableCap.ScissorTest);
        }

        protected override void SetBlendImplementation(BlendingParameters blendingParameters)
        {
            if (blendingParameters.IsDisabled)
            {
                if (!lastBlendingEnabledState.HasValue || lastBlendingEnabledState.Value)
                    GL.Disable(EnableCap.Blend);

                lastBlendingEnabledState = false;
            }
            else
            {
                if (!lastBlendingEnabledState.HasValue || !lastBlendingEnabledState.Value)
                    GL.Enable(EnableCap.Blend);

                lastBlendingEnabledState = true;

                int eqRgb = (int)blendingParameters.RGBEquationMode;
                int eqA = (int)blendingParameters.AlphaEquationMode;
                if (lastBlendEqRgb != eqRgb || lastBlendEqAlpha != eqA)
                {
                    lastBlendEqRgb = eqRgb;
                    lastBlendEqAlpha = eqA;
                    GL.BlendEquationSeparate(blendingParameters.RGBEquationMode, blendingParameters.AlphaEquationMode);
                }

                int sr = (int)blendingParameters.SourceBlendingFactor;
                int dr = (int)blendingParameters.DestinationBlendingFactor;
                int sa = (int)blendingParameters.SourceAlphaBlendingFactor;
                int da = (int)blendingParameters.DestinationAlphaBlendingFactor;
                if (lastBlendSrcRgb != sr || lastBlendDstRgb != dr || lastBlendSrcAlpha != sa || lastBlendDstAlpha != da)
                {
                    lastBlendSrcRgb = sr;
                    lastBlendDstRgb = dr;
                    lastBlendSrcAlpha = sa;
                    lastBlendDstAlpha = da;
                    GL.BlendFuncSeparate(blendingParameters.SourceBlendingFactor, blendingParameters.DestinationBlendingFactor,
                        blendingParameters.SourceAlphaBlendingFactor, blendingParameters.DestinationAlphaBlendingFactor);
                }
            }
        }

        protected override void SetBlendMaskImplementation(BlendingMask blendingMask)
        {
            int mask = (int)blendingMask;
            if (lastColorMask == mask)
                return;

            lastColorMask = mask;
            GL.ColorMask(blendingMask.HasFlagFast(BlendingMask.Red),
                blendingMask.HasFlagFast(BlendingMask.Green),
                blendingMask.HasFlagFast(BlendingMask.Blue),
                blendingMask.HasFlagFast(BlendingMask.Alpha));
        }

        protected override void SetViewportImplementation(RectangleI viewport) => GL.Viewport(viewport.Left, viewport.Top, viewport.Width, viewport.Height);

        protected override void SetScissorImplementation(RectangleI scissor) => GL.Scissor(scissor.X, Viewport.Height - scissor.Bottom, scissor.Width, scissor.Height);

        protected override void SetDepthInfoImplementation(DepthInfo depthInfo)
        {
            int test = depthInfo.DepthTest ? 1 : 0;
            if (lastDepthTest != test)
            {
                lastDepthTest = test;
                if (depthInfo.DepthTest)
                    GL.Enable(EnableCap.DepthTest);
                else
                    GL.Disable(EnableCap.DepthTest);
            }

            if (depthInfo.DepthTest)
            {
                int fn = (int)GLUtils.ToDepthFunction(depthInfo.Function);
                if (lastDepthFunc != fn)
                {
                    lastDepthFunc = fn;
                    GL.DepthFunc((DepthFunction)fn);
                }
            }

            int mask = depthInfo.WriteDepth ? 1 : 0;
            if (lastDepthMask != mask)
            {
                lastDepthMask = mask;
                GL.DepthMask(depthInfo.WriteDepth);
            }
        }

        protected override void SetStencilInfoImplementation(StencilInfo stencilInfo)
        {
            if (stencilInfo.StencilTest)
            {
                if (lastStencilTest != 1)
                {
                    lastStencilTest = 1;
                    GL.Enable(EnableCap.StencilTest);
                }

                GL.StencilFunc(GLUtils.ToStencilFunction(stencilInfo.TestFunction), stencilInfo.TestValue, stencilInfo.Mask);
                GL.StencilOp(
                    GLUtils.ToStencilOperation(stencilInfo.StencilTestFailOperation),
                    GLUtils.ToStencilOperation(stencilInfo.DepthTestFailOperation),
                    GLUtils.ToStencilOperation(stencilInfo.TestPassedOperation));
            }
            else if (lastStencilTest != 0)
            {
                lastStencilTest = 0;
                GL.Disable(EnableCap.StencilTest);
            }
        }

        protected internal override Image<Rgba32> TakeScreenshot()
        {
            var size = ((IGraphicsSurface)openGLSurface).GetDrawableSize();
            var data = MemoryAllocator.Default.Allocate<Rgba32>(size.Width * size.Height);

            GL.ReadPixels(0, 0, size.Width, size.Height, PixelFormat.Rgba, PixelType.UnsignedByte, ref MemoryMarshal.GetReference(data.Memory.Span));

            var image = Image.LoadPixelData(data.Memory.Span, size.Width, size.Height);
            image.Mutate(i => i.Flip(FlipMode.Vertical));
            return image;
        }

        protected internal override Image<Rgba32> ExtractFrameBufferData(IFrameBuffer frameBuffer)
        {
            int width = frameBuffer.Texture.Width;
            int height = frameBuffer.Texture.Height;

            var data = MemoryAllocator.Default.Allocate<Rgba32>(width * height);

            frameBuffer.Bind();
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, ref MemoryMarshal.GetReference(data.Memory.Span));
            frameBuffer.Unbind();

            var image = Image.LoadPixelData(data.Memory.Span, width, height);

            return image;
        }

        protected override IShaderPart CreateShaderPart(IShaderStore store, string name, byte[]? rawData, ShaderPartType partType)
        {
            ShaderType glType;

            switch (partType)
            {
                case ShaderPartType.Fragment:
                    glType = ShaderType.FragmentShader;
                    break;

                case ShaderPartType.Vertex:
                    glType = ShaderType.VertexShader;
                    break;

                default:
                    throw new ArgumentException($"Unsupported shader part type: {partType}", nameof(partType));
            }

            return new GLShaderPart(this, name, rawData, glType, store);
        }

        protected override IShader CreateShader(string name, IShaderPart[] parts, ShaderCompilationStore compilationStore)
            => new GLShader(this, name, parts.Cast<GLShaderPart>().ToArray(), compilationStore);

        public override IFrameBuffer CreateFrameBuffer(TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float, RenderBufferFormat[]? renderBufferFormats = null, TextureFilteringMode filteringMode = TextureFilteringMode.Linear)
        {
            RenderbufferInternalFormat[]? glFormats = null;

            if (renderBufferFormats != null)
            {
                glFormats = new RenderbufferInternalFormat[renderBufferFormats.Length];

                for (int i = 0; i < renderBufferFormats.Length; i++)
                {
                    switch (renderBufferFormats[i])
                    {
                        case RenderBufferFormat.D16:
                            glFormats[i] = RenderbufferInternalFormat.DepthComponent16;
                            break;

                        case RenderBufferFormat.D32:
                            glFormats[i] = RenderbufferInternalFormat.DepthComponent32f;
                            break;

                        case RenderBufferFormat.D24S8:
                            glFormats[i] = RenderbufferInternalFormat.Depth24Stencil8;
                            break;

                        case RenderBufferFormat.D32S8:
                            glFormats[i] = RenderbufferInternalFormat.Depth32fStencil8;
                            break;

                        default:
                            throw new ArgumentException($"Unsupported render buffer format: {renderBufferFormats[i]}", nameof(renderBufferFormats));
                    }
                }
            }

            return new GLFrameBuffer(this, textureFormat, glFormats, filteringMode);
        }

        protected override IUniformBuffer<TData> CreateUniformBuffer<TData>()
            => new GLUniformBuffer<TData>(this);

        protected override IShaderStorageBufferObject<TData> CreateShaderStorageBufferObject<TData>(int uboSize, int ssboSize)
            => new GLShaderStorageBufferObject<TData>(this, uboSize, ssboSize);

        protected override INativeTexture CreateNativeTexture(int width, int height, bool manualMipmaps = false, TextureFilteringMode filteringMode = TextureFilteringMode.Linear,
                                                              Color4? initialisationColour = null)
            => new GLTexture(this, width, height, TexturePixelFormat.R8G8B8A8Float, manualMipmaps, filteringMode, initialisationColour);

        protected override INativeTexture CreateNativeVideoTexture(int width, int height) => new GLVideoTexture(this, width, height);

        protected override IVertexBatch<TVertex> CreateLinearBatch<TVertex>(int size, int maxBuffers, PrimitiveTopology topology)
            => new GLLinearBatch<TVertex>(this, size, maxBuffers, topology);

        protected override IVertexBatch<TVertex> CreateQuadBatch<TVertex>(int size, int maxBuffers) => new GLQuadBatch<TVertex>(this, size, maxBuffers);
    }
}
