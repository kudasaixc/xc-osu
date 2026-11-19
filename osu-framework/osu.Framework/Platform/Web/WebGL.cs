// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks): WebGL2 bindings with the same surface as osuTK.Graphics.ES30.GL,
// statically linked against emscripten's GLES3 -> WebGL2 implementation, with a wasm -> JS command
// buffer that drains a frame's hot draw/state/upload calls in a single transition.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using osuTK.Graphics;
using osuTK.Graphics.ES30;

#pragma warning disable IDE1006

namespace osu.Framework.Platform.Web
{
    internal static unsafe class WebGL
    {
        private const string lib = "xcweb";

        #region native

        [DllImport(lib)] private static extern void xc_gl_submit(int* cmds, int cmdInts, byte* data);

        [DllImport(lib)] private static extern void glActiveTexture(int texture);
        [DllImport(lib)] private static extern void glAttachShader(int program, int shader);
        [DllImport(lib)] private static extern void glBindBuffer(int target, int buffer);
        [DllImport(lib)] private static extern void glBindBufferBase(int target, int index, int buffer);
        [DllImport(lib)] private static extern void glBindBufferRange(int target, int index, int buffer, nint offset, nint size);
        [DllImport(lib)] private static extern void glBindFramebuffer(int target, int fb);
        [DllImport(lib)] private static extern void glBindRenderbuffer(int target, int rb);
        [DllImport(lib)] private static extern void glBindTexture(int target, int tex);
        [DllImport(lib)] private static extern void glBindVertexArray(int vao);
        [DllImport(lib)] private static extern void glBlendEquationSeparate(int rgb, int alpha);
        [DllImport(lib)] private static extern void glBlendFuncSeparate(int srcRgb, int dstRgb, int srcA, int dstA);
        [DllImport(lib)] private static extern void glBufferData(int target, nint size, void* data, int usage);
        [DllImport(lib)] private static extern void glBufferSubData(int target, nint offset, nint size, void* data);
        [DllImport(lib)] private static extern void glClear(int mask);
        [DllImport(lib)] private static extern void glClearColor(float r, float g, float b, float a);
        [DllImport(lib)] private static extern void glClearDepthf(float d);
        [DllImport(lib)] private static extern void glClearStencil(int s);
        [DllImport(lib)] private static extern void glColorMask(byte r, byte g, byte b, byte a);
        [DllImport(lib)] private static extern void glCompileShader(int shader);
        [DllImport(lib)] private static extern int glCreateProgram();
        [DllImport(lib)] private static extern int glCreateShader(int type);
        [DllImport(lib)] private static extern void glDeleteBuffers(int n, int* buffers);
        [DllImport(lib)] private static extern void glDeleteFramebuffers(int n, int* fbs);
        [DllImport(lib)] private static extern void glDeleteProgram(int program);
        [DllImport(lib)] private static extern void glDeleteRenderbuffers(int n, int* rbs);
        [DllImport(lib)] private static extern void glDeleteShader(int shader);
        [DllImport(lib)] private static extern void glDeleteTextures(int n, int* textures);
        [DllImport(lib)] private static extern void glDeleteVertexArrays(int n, int* arrays);
        [DllImport(lib)] private static extern void glDepthFunc(int func);
        [DllImport(lib)] private static extern void glDepthMask(byte flag);
        [DllImport(lib)] private static extern void glDetachShader(int program, int shader);
        [DllImport(lib)] private static extern void glDisable(int cap);
        [DllImport(lib)] private static extern void glDrawElements(int mode, int count, int type, nint offset);
        [DllImport(lib)] private static extern void glEnable(int cap);
        [DllImport(lib)] private static extern void glEnableVertexAttribArray(int index);
        [DllImport(lib)] private static extern void glFinish();
        [DllImport(lib)] private static extern void glFramebufferRenderbuffer(int target, int attachment, int rbTarget, int rb);
        [DllImport(lib)] private static extern void glFramebufferTexture2D(int target, int attachment, int texTarget, int tex, int level);
        [DllImport(lib)] private static extern void glGenBuffers(int n, int* buffers);
        [DllImport(lib)] private static extern void glGenerateMipmap(int target);
        [DllImport(lib)] private static extern void glGenFramebuffers(int n, int* fbs);
        [DllImport(lib)] private static extern void glGenRenderbuffers(int n, int* rbs);
        [DllImport(lib)] private static extern void glGenTextures(int n, int* textures);
        [DllImport(lib)] private static extern void glGenVertexArrays(int n, int* arrays);
        [DllImport(lib)] private static extern void glGetIntegerv(int pname, int* data);
        [DllImport(lib)] private static extern void glGetProgramiv(int program, int pname, int* param);
        [DllImport(lib)] private static extern void glGetProgramInfoLog(int program, int bufSize, int* length, byte* log);
        [DllImport(lib)] private static extern void glGetShaderiv(int shader, int pname, int* param);
        [DllImport(lib)] private static extern void glGetShaderInfoLog(int shader, int bufSize, int* length, byte* log);
        [DllImport(lib)] private static extern byte* glGetString(int name);
        [DllImport(lib)] private static extern byte* glGetStringi(int name, int index);
        [DllImport(lib)] private static extern int glGetUniformBlockIndex(int program, byte* name);
        [DllImport(lib)] private static extern int glGetUniformLocation(int program, byte* name);
        [DllImport(lib)] private static extern void glHint(int target, int mode);
        [DllImport(lib)] private static extern void glInvalidateFramebuffer(int target, int count, int* attachments);
        [DllImport(lib)] private static extern void glLinkProgram(int program);
        [DllImport(lib)] private static extern void glPixelStorei(int pname, int param);
        [DllImport(lib)] private static extern void glReadPixels(int x, int y, int w, int h, int format, int type, void* data);
        [DllImport(lib)] private static extern void glRenderbufferStorage(int target, int format, int w, int h);
        [DllImport(lib)] private static extern void glScissor(int x, int y, int w, int h);
        [DllImport(lib)] private static extern void glShaderSource(int shader, int count, byte** strings, int* lengths);
        [DllImport(lib)] private static extern void glStencilFunc(int func, int @ref, int mask);
        [DllImport(lib)] private static extern void glStencilOp(int fail, int zfail, int zpass);
        [DllImport(lib)] private static extern void glTexImage2D(int target, int level, int internalFormat, int w, int h, int border, int format, int type, void* data);
        [DllImport(lib)] private static extern void glTexParameteri(int target, int pname, int param);
        [DllImport(lib)] private static extern void glTexSubImage2D(int target, int level, int x, int y, int w, int h, int format, int type, void* data);
        [DllImport(lib)] private static extern void glUniform1i(int loc, int v);
        [DllImport(lib)] private static extern void glUniform1f(int loc, float v);
        [DllImport(lib)] private static extern void glUniform2fv(int loc, int count, float* v);
        [DllImport(lib)] private static extern void glUniform3fv(int loc, int count, float* v);
        [DllImport(lib)] private static extern void glUniform4fv(int loc, int count, float* v);
        [DllImport(lib)] private static extern void glUniformBlockBinding(int program, int index, int binding);
        [DllImport(lib)] private static extern void glUniformMatrix2fv(int loc, int count, byte transpose, float* v);
        [DllImport(lib)] private static extern void glUniformMatrix3fv(int loc, int count, byte transpose, float* v);
        [DllImport(lib)] private static extern void glUniformMatrix4fv(int loc, int count, byte transpose, float* v);
        [DllImport(lib)] private static extern void glUseProgram(int program);
        [DllImport(lib)] private static extern void glVertexAttribIPointer(int index, int size, int type, int stride, nint offset);
        [DllImport(lib)] private static extern void glVertexAttribPointer(int index, int size, int type, byte normalized, int stride, nint offset);
        [DllImport(lib)] private static extern void glViewport(int x, int y, int w, int h);

        #endregion

        #region command buffer

        private const int cmd_cap = 32768;          // 128 KB of int32 commands
        private const int data_cap = 2 * 1024 * 1024; // 2 MB staged vertex/uniform bytes

        private static readonly bool batch_enabled = RuntimeInfo.IsBrowser && Environment.GetEnvironmentVariable("XC_GL_BATCH") != "0";

        private static readonly int* cmdBuf = batch_enabled ? (int*)NativeMemory.Alloc((nuint)(cmd_cap * sizeof(int))) : null;
        private static readonly byte* dataBuf = batch_enabled ? (byte*)NativeMemory.Alloc((nuint)data_cap) : null;
        private static int cmdPos;
        private static int dataPos;

        private const int op_use_program = 1;
        private const int op_bind_vao = 2;
        private const int op_bind_buffer = 3;
        private const int op_bind_buffer_base = 4;
        private const int op_bind_buffer_range = 5;
        private const int op_active_texture = 6;
        private const int op_bind_texture = 7;
        private const int op_scissor = 8;
        private const int op_viewport = 9;
        private const int op_enable = 10;
        private const int op_disable = 11;
        private const int op_blend_eq_sep = 12;
        private const int op_blend_func_sep = 13;
        private const int op_color_mask = 14;
        private const int op_depth_func = 15;
        private const int op_depth_mask = 16;
        private const int op_stencil_func = 17;
        private const int op_stencil_op = 18;
        private const int op_clear = 19;
        private const int op_clear_color = 20;
        private const int op_clear_depth = 21;
        private const int op_clear_stencil = 22;
        private const int op_bind_framebuffer = 23;
        private const int op_buffer_sub_data = 24;
        private const int op_draw_elements = 25;
        private const int op_uniform_block_binding = 26;
        private const int op_buffer_data_null = 27;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FlushCommands()
        {
            if (cmdPos > 0)
                flushSlow();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void flushSlow()
        {
            int n = cmdPos;
            cmdPos = 0;
            dataPos = 0;
            xc_gl_submit(cmdBuf, n, dataBuf);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ensureCmd(int ints)
        {
            if (cmdPos + ints > cmd_cap)
                flushSlow();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void push1(int op, int a)
        {
            ensureCmd(2);
            int p = cmdPos;
            cmdBuf[p] = op;
            cmdBuf[p + 1] = a;
            cmdPos = p + 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void push2(int op, int a, int b)
        {
            ensureCmd(3);
            int p = cmdPos;
            cmdBuf[p] = op;
            cmdBuf[p + 1] = a;
            cmdBuf[p + 2] = b;
            cmdPos = p + 3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void push3(int op, int a, int b, int c)
        {
            ensureCmd(4);
            int p = cmdPos;
            cmdBuf[p] = op;
            cmdBuf[p + 1] = a;
            cmdBuf[p + 2] = b;
            cmdBuf[p + 3] = c;
            cmdPos = p + 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void push4(int op, int a, int b, int c, int d)
        {
            ensureCmd(5);
            int p = cmdPos;
            cmdBuf[p] = op;
            cmdBuf[p + 1] = a;
            cmdBuf[p + 2] = b;
            cmdBuf[p + 3] = c;
            cmdBuf[p + 4] = d;
            cmdPos = p + 5;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void push5(int op, int a, int b, int c, int d, int e)
        {
            ensureCmd(6);
            int p = cmdPos;
            cmdBuf[p] = op;
            cmdBuf[p + 1] = a;
            cmdBuf[p + 2] = b;
            cmdBuf[p + 3] = c;
            cmdBuf[p + 4] = d;
            cmdBuf[p + 5] = e;
            cmdPos = p + 6;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void enqueueBufferSubData(int target, nint offset, nint size, void* src)
        {
            int sz = (int)size;
            if (sz <= 0)
                return;

            if (!batch_enabled || sz > data_cap)
            {
                FlushCommands();
                glBufferSubData(target, offset, size, src);
                return;
            }

            if (dataPos + sz > data_cap || cmdPos + 5 > cmd_cap)
                flushSlow();

            int dOff = dataPos;
            System.Buffer.MemoryCopy(src, dataBuf + dOff, data_cap - dOff, sz);
            dataPos = (dOff + sz + 3) & ~3;

            int p = cmdPos;
            cmdBuf[p] = op_buffer_sub_data;
            cmdBuf[p + 1] = target;
            cmdBuf[p + 2] = (int)offset;
            cmdBuf[p + 3] = sz;
            cmdBuf[p + 4] = dOff;
            cmdPos = p + 5;
        }

        #endregion

        private static byte b(bool v) => v ? (byte)1 : (byte)0;

        private static string str(byte* p) => p == null ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)p) ?? string.Empty;

        private static byte[] utf8z(string s)
        {
            byte[] bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }

        public static void ActiveTexture(TextureUnit unit)
        {
            if (batch_enabled) push1(op_active_texture, (int)unit);
            else glActiveTexture((int)unit);
        }

        public static void AttachShader(int program, int shader)
        {
            FlushCommands();
            glAttachShader(program, shader);
        }

        public static void BindBuffer(BufferTarget target, int buffer)
        {
            if (batch_enabled) push2(op_bind_buffer, (int)target, buffer);
            else glBindBuffer((int)target, buffer);
        }

        public static void BindBufferBase(BufferRangeTarget target, int index, int buffer)
        {
            if (batch_enabled) push3(op_bind_buffer_base, (int)target, index, buffer);
            else glBindBufferBase((int)target, index, buffer);
        }

        public static void BindBufferRange(BufferRangeTarget target, int index, int buffer, IntPtr offset, IntPtr size)
        {
            if (batch_enabled) push5(op_bind_buffer_range, (int)target, index, buffer, (int)offset, (int)size);
            else glBindBufferRange((int)target, index, buffer, offset, size);
        }

        public static void BindFramebuffer(FramebufferTarget target, int fb)
        {
            if (batch_enabled) push2(op_bind_framebuffer, (int)target, fb);
            else glBindFramebuffer((int)target, fb);
        }

        public static void BindRenderbuffer(RenderbufferTarget target, int rb)
        {
            FlushCommands();
            glBindRenderbuffer((int)target, rb);
        }

        public static void BindTexture(TextureTarget target, int tex)
        {
            if (batch_enabled) push2(op_bind_texture, (int)target, tex);
            else glBindTexture((int)target, tex);
        }

        public static void BindVertexArray(int vao)
        {
            if (batch_enabled) push1(op_bind_vao, vao);
            else glBindVertexArray(vao);
        }

        public static void BlendEquationSeparate(BlendEquationMode rgb, BlendEquationMode alpha)
        {
            if (batch_enabled) push2(op_blend_eq_sep, (int)rgb, (int)alpha);
            else glBlendEquationSeparate((int)rgb, (int)alpha);
        }

        public static void BlendFuncSeparate(BlendingFactorSrc srcRgb, BlendingFactorDest dstRgb, BlendingFactorSrc srcAlpha, BlendingFactorDest dstAlpha)
        {
            if (batch_enabled) push4(op_blend_func_sep, (int)srcRgb, (int)dstRgb, (int)srcAlpha, (int)dstAlpha);
            else glBlendFuncSeparate((int)srcRgb, (int)dstRgb, (int)srcAlpha, (int)dstAlpha);
        }

        public static void BufferData(BufferTarget target, IntPtr size, IntPtr data, BufferUsageHint usage)
        {
            if (batch_enabled && data == IntPtr.Zero)
                push3(op_buffer_data_null, (int)target, (int)size, (int)usage);
            else
            {
                FlushCommands();
                glBufferData((int)target, size, (void*)data, (int)usage);
            }
        }

        public static void BufferData<T>(BufferTarget target, IntPtr size, T[] data, BufferUsageHint usage) where T : unmanaged
        {
            FlushCommands();
            fixed (T* p = data) glBufferData((int)target, size, p, (int)usage);
        }

        public static void BufferData<T>(BufferTarget target, IntPtr size, ref T data, BufferUsageHint usage) where T : unmanaged
        {
            FlushCommands();
            fixed (T* p = &data) glBufferData((int)target, size, p, (int)usage);
        }

        public static void BufferData<T>(BufferTarget target, int size, ref T data, BufferUsageHint usage) where T : unmanaged
            => BufferData(target, (IntPtr)size, ref data, usage);

        public static void BufferSubData(BufferTarget target, IntPtr offset, IntPtr size, IntPtr data)
            => enqueueBufferSubData((int)target, offset, size, (void*)data);

        public static void BufferSubData<T>(BufferTarget target, IntPtr offset, IntPtr size, ref T data) where T : unmanaged
        {
            fixed (T* p = &data) enqueueBufferSubData((int)target, offset, size, p);
        }

        public static void BufferSubData<T>(BufferTarget target, int offset, int size, ref T data) where T : unmanaged
            => BufferSubData(target, (IntPtr)offset, (IntPtr)size, ref data);

        public static void Clear(ClearBufferMask mask)
        {
            if (batch_enabled) push1(op_clear, (int)mask);
            else glClear((int)mask);
        }

        public static void ClearColor(Color4 c) => ClearColor(c.R, c.G, c.B, c.A);

        public static void ClearColor(float r, float g, float bl, float a)
        {
            if (batch_enabled) push4(op_clear_color, BitConverter.SingleToInt32Bits(r), BitConverter.SingleToInt32Bits(g), BitConverter.SingleToInt32Bits(bl), BitConverter.SingleToInt32Bits(a));
            else glClearColor(r, g, bl, a);
        }

        public static void ClearDepth(float d)
        {
            if (batch_enabled) push1(op_clear_depth, BitConverter.SingleToInt32Bits(d));
            else glClearDepthf(d);
        }

        public static void ClearStencil(int s)
        {
            if (batch_enabled) push1(op_clear_stencil, s);
            else glClearStencil(s);
        }

        public static void ColorMask(bool r, bool g, bool bl, bool a)
        {
            if (batch_enabled) push4(op_color_mask, b(r), b(g), b(bl), b(a));
            else glColorMask(b(r), b(g), b(bl), b(a));
        }

        public static void CompileShader(int shader)
        {
            FlushCommands();
            glCompileShader(shader);
        }

        public static int CreateProgram()
        {
            FlushCommands();
            return glCreateProgram();
        }

        public static int CreateShader(ShaderType type)
        {
            FlushCommands();
            return glCreateShader((int)type);
        }

        public static void DeleteBuffer(int buffer)
        {
            FlushCommands();
            glDeleteBuffers(1, &buffer);
        }

        public static void DeleteFramebuffer(int fb)
        {
            FlushCommands();
            glDeleteFramebuffers(1, &fb);
        }

        public static void DeleteProgram(int program)
        {
            FlushCommands();
            glDeleteProgram(program);
        }

        public static void DeleteRenderbuffer(int rb)
        {
            FlushCommands();
            glDeleteRenderbuffers(1, &rb);
        }

        public static void DeleteShader(int shader)
        {
            FlushCommands();
            glDeleteShader(shader);
        }

        public static void DeleteTextures(int n, int[] textures)
        {
            FlushCommands();
            fixed (int* p = textures) glDeleteTextures(n, p);
        }

        public static void DeleteVertexArray(int vao)
        {
            FlushCommands();
            glDeleteVertexArrays(1, &vao);
        }

        public static void DepthFunc(DepthFunction func)
        {
            if (batch_enabled) push1(op_depth_func, (int)func);
            else glDepthFunc((int)func);
        }

        public static void DepthMask(bool flag)
        {
            if (batch_enabled) push1(op_depth_mask, b(flag));
            else glDepthMask(b(flag));
        }

        public static void DetachShader(int program, int shader)
        {
            FlushCommands();
            glDetachShader(program, shader);
        }

        public static void Disable(EnableCap cap)
        {
            if (batch_enabled) push1(op_disable, (int)cap);
            else glDisable((int)cap);
        }

        public static void DrawElements(PrimitiveType mode, int count, DrawElementsType type, int offset)
        {
            if (batch_enabled) push4(op_draw_elements, (int)mode, count, (int)type, offset);
            else glDrawElements((int)mode, count, (int)type, offset);
        }

        public static void Enable(EnableCap cap)
        {
            if (batch_enabled) push1(op_enable, (int)cap);
            else glEnable((int)cap);
        }

        public static void EnableVertexAttribArray(int index)
        {
            FlushCommands();
            glEnableVertexAttribArray(index);
        }

        public static void Finish()
        {
            FlushCommands();
            glFinish();
        }

        public static void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget rbTarget, int rb)
        {
            FlushCommands();
            glFramebufferRenderbuffer((int)target, (int)attachment, (int)rbTarget, rb);
        }

        public static void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget2d texTarget, int tex, int level)
        {
            FlushCommands();
            glFramebufferTexture2D((int)target, (int)attachment, (int)texTarget, tex, level);
        }

        public static int GenBuffer()
        {
            FlushCommands();
            int id;
            glGenBuffers(1, &id);
            return id;
        }

        public static void GenBuffers(int n, out int buffer)
        {
            FlushCommands();
            int id;
            glGenBuffers(1, &id);
            buffer = id;
        }

        public static void GenerateMipmap(TextureTarget target)
        {
            FlushCommands();
            glGenerateMipmap((int)target);
        }

        public static int GenFramebuffer()
        {
            FlushCommands();
            int id;
            glGenFramebuffers(1, &id);
            return id;
        }

        public static int GenRenderbuffer()
        {
            FlushCommands();
            int id;
            glGenRenderbuffers(1, &id);
            return id;
        }

        public static void GenTextures(int n, int[] textures)
        {
            FlushCommands();
            fixed (int* p = textures) glGenTextures(n, p);
        }

        public static int GenVertexArray()
        {
            FlushCommands();
            int id;
            glGenVertexArrays(1, &id);
            return id;
        }

        public static int GetInteger(GetPName pname)
        {
            FlushCommands();
            int v;
            glGetIntegerv((int)pname, &v);
            return v;
        }

        public static void GetInteger(All pname, out int value)
        {
            FlushCommands();
            int v;
            glGetIntegerv((int)pname, &v);
            value = v;
        }

        public static void GetProgram(int program, GetProgramParameterName pname, out int value)
        {
            FlushCommands();
            int v;
            glGetProgramiv(program, (int)pname, &v);
            value = v;
        }

        public static string GetProgramInfoLog(int program)
        {
            FlushCommands();
            int len;
            glGetProgramiv(program, 0x8B84 /* GL_INFO_LOG_LENGTH */, &len);
            if (len <= 0) return string.Empty;

            byte* buf = stackalloc byte[len + 1];
            glGetProgramInfoLog(program, len + 1, &len, buf);
            return str(buf);
        }

        public static void GetShader(int shader, ShaderParameter pname, out int value)
        {
            FlushCommands();
            int v;
            glGetShaderiv(shader, (int)pname, &v);
            value = v;
        }

        public static string GetShaderInfoLog(int shader)
        {
            FlushCommands();
            int len;
            glGetShaderiv(shader, 0x8B84, &len);
            if (len <= 0) return string.Empty;

            byte* buf = stackalloc byte[len + 1];
            glGetShaderInfoLog(shader, len + 1, &len, buf);
            return str(buf);
        }

        public static string GetString(StringName name)
        {
            FlushCommands();
            return str(glGetString((int)name));
        }

        public static string GetString(StringNameIndexed name, int index)
        {
            FlushCommands();
            return str(glGetStringi((int)name, index));
        }

        public static int GetUniformBlockIndex(int program, string name)
        {
            FlushCommands();
            fixed (byte* p = utf8z(name)) return glGetUniformBlockIndex(program, p);
        }

        public static int GetUniformLocation(int program, string name)
        {
            FlushCommands();
            fixed (byte* p = utf8z(name)) return glGetUniformLocation(program, p);
        }

        public static void Hint(HintTarget target, HintMode mode)
        {
            FlushCommands();
            // GL_GENERATE_MIPMAP_HINT is valid in WebGL2.
            glHint((int)target, (int)mode);
        }

        public static void InvalidateFramebuffer(FramebufferTarget target, int count, ref FramebufferAttachment attachment)
        {
            FlushCommands();
            fixed (FramebufferAttachment* p = &attachment) glInvalidateFramebuffer((int)target, count, (int*)p);
        }

        public static void LinkProgram(int program)
        {
            FlushCommands();
            glLinkProgram(program);
        }

        public static void PixelStore(PixelStoreParameter pname, int param)
        {
            FlushCommands();
            glPixelStorei((int)pname, param);
        }

        public static void ReadPixels<T>(int x, int y, int w, int h, PixelFormat format, PixelType type, ref T data) where T : unmanaged
        {
            FlushCommands();
            fixed (T* p = &data) glReadPixels(x, y, w, h, (int)format, (int)type, p);
        }

        public static void RenderbufferStorage(RenderbufferTarget target, RenderbufferInternalFormat format, int w, int h)
        {
            FlushCommands();
            glRenderbufferStorage((int)target, (int)format, w, h);
        }

        public static void Scissor(int x, int y, int w, int h)
        {
            if (batch_enabled) push4(op_scissor, x, y, w, h);
            else glScissor(x, y, w, h);
        }

        public static void ShaderSource(int shader, string source)
        {
            FlushCommands();
            byte[] bytes = utf8z(source);

            fixed (byte* p = bytes)
            {
                byte* s = p;
                int len = bytes.Length - 1;
                glShaderSource(shader, 1, &s, &len);
            }
        }

        public static void StencilFunc(StencilFunction func, int @ref, int mask)
        {
            if (batch_enabled) push3(op_stencil_func, (int)func, @ref, mask);
            else glStencilFunc((int)func, @ref, mask);
        }

        public static void StencilOp(StencilOp fail, StencilOp zfail, StencilOp zpass)
        {
            if (batch_enabled) push3(op_stencil_op, (int)fail, (int)zfail, (int)zpass);
            else glStencilOp((int)fail, (int)zfail, (int)zpass);
        }

        public static void TexImage2D(TextureTarget2d target, int level, TextureComponentCount internalFormat, int w, int h, int border, PixelFormat format, PixelType type, IntPtr data)
        {
            FlushCommands();
            glTexImage2D((int)target, level, (int)internalFormat, w, h, border, (int)format, (int)type, (void*)data);
        }

        public static void TexImage2D<T>(TextureTarget2d target, int level, TextureComponentCount internalFormat, int w, int h, int border, PixelFormat format, PixelType type, ref T data)
            where T : unmanaged
        {
            FlushCommands();
            fixed (T* p = &data) glTexImage2D((int)target, level, (int)internalFormat, w, h, border, (int)format, (int)type, p);
        }

        public static void TexParameter(TextureTarget target, TextureParameterName pname, int param)
        {
            FlushCommands();
            glTexParameteri((int)target, (int)pname, param);
        }

        public static void TexSubImage2D(TextureTarget2d target, int level, int x, int y, int w, int h, PixelFormat format, PixelType type, IntPtr data)
        {
            FlushCommands();
            glTexSubImage2D((int)target, level, x, y, w, h, (int)format, (int)type, (void*)data);
        }

        public static void Uniform1(int loc, int v)
        {
            FlushCommands();
            glUniform1i(loc, v);
        }

        public static void Uniform1(int loc, float v)
        {
            FlushCommands();
            glUniform1f(loc, v);
        }

        public static void Uniform2(int loc, ref osuTK.Vector2 v)
        {
            FlushCommands();
            fixed (osuTK.Vector2* p = &v) glUniform2fv(loc, 1, (float*)p);
        }

        public static void Uniform3(int loc, ref osuTK.Vector3 v)
        {
            FlushCommands();
            fixed (osuTK.Vector3* p = &v) glUniform3fv(loc, 1, (float*)p);
        }

        public static void Uniform4(int loc, ref osuTK.Vector4 v)
        {
            FlushCommands();
            fixed (osuTK.Vector4* p = &v) glUniform4fv(loc, 1, (float*)p);
        }

        public static void UniformBlockBinding(int program, int index, int binding)
        {
            if (batch_enabled) push3(op_uniform_block_binding, program, index, binding);
            else glUniformBlockBinding(program, index, binding);
        }

        public static void UniformMatrix2(int loc, bool transpose, ref osuTK.Matrix2 m)
        {
            FlushCommands();
            fixed (osuTK.Matrix2* p = &m) glUniformMatrix2fv(loc, 1, b(transpose), (float*)p);
        }

        public static void UniformMatrix3(int loc, bool transpose, ref osuTK.Matrix3 m)
        {
            FlushCommands();
            fixed (osuTK.Matrix3* p = &m) glUniformMatrix3fv(loc, 1, b(transpose), (float*)p);
        }

        public static void UniformMatrix4(int loc, bool transpose, ref osuTK.Matrix4 m)
        {
            FlushCommands();
            fixed (osuTK.Matrix4* p = &m) glUniformMatrix4fv(loc, 1, b(transpose), (float*)p);
        }

        public static void UseProgram(int program)
        {
            if (batch_enabled) push1(op_use_program, program);
            else glUseProgram(program);
        }

        public static void VertexAttribIPointer(int index, int size, VertexAttribIntegerType type, int stride, IntPtr offset)
        {
            FlushCommands();
            glVertexAttribIPointer(index, size, (int)type, stride, offset);
        }

        public static void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, IntPtr offset)
        {
            FlushCommands();
            glVertexAttribPointer(index, size, (int)type, b(normalized), stride, offset);
        }

        public static void Viewport(int x, int y, int w, int h)
        {
            if (batch_enabled) push4(op_viewport, x, y, w, h);
            else glViewport(x, y, w, h);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Touch()
        {
        }
    }
}
