// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks): WebGL2 bindings with the same surface as osuTK.Graphics.ES30.GL,
// statically linked against emscripten's GLES3 -> WebGL2 implementation.

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

        [DllImport(lib)] private static extern void glActiveTexture(int texture);
        [DllImport(lib)] private static extern void glAttachShader(int program, int shader);
        [DllImport(lib)] private static extern void glBindBuffer(int target, int buffer);
        [DllImport(lib)] private static extern void glBindBufferBase(int target, int index, int buffer);
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

        private static byte b(bool v) => v ? (byte)1 : (byte)0;

        private static string str(byte* p) => p == null ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)p) ?? string.Empty;

        private static byte[] utf8z(string s)
        {
            byte[] bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }

        public static void ActiveTexture(TextureUnit unit) => glActiveTexture((int)unit);
        public static void AttachShader(int program, int shader) => glAttachShader(program, shader);
        public static void BindBuffer(BufferTarget target, int buffer) => glBindBuffer((int)target, buffer);
        public static void BindBufferBase(BufferRangeTarget target, int index, int buffer) => glBindBufferBase((int)target, index, buffer);
        public static void BindFramebuffer(FramebufferTarget target, int fb) => glBindFramebuffer((int)target, fb);
        public static void BindRenderbuffer(RenderbufferTarget target, int rb) => glBindRenderbuffer((int)target, rb);
        public static void BindTexture(TextureTarget target, int tex) => glBindTexture((int)target, tex);
        public static void BindVertexArray(int vao) => glBindVertexArray(vao);
        public static void BlendEquationSeparate(BlendEquationMode rgb, BlendEquationMode alpha) => glBlendEquationSeparate((int)rgb, (int)alpha);

        public static void BlendFuncSeparate(BlendingFactorSrc srcRgb, BlendingFactorDest dstRgb, BlendingFactorSrc srcAlpha, BlendingFactorDest dstAlpha)
            => glBlendFuncSeparate((int)srcRgb, (int)dstRgb, (int)srcAlpha, (int)dstAlpha);

        public static void BufferData(BufferTarget target, IntPtr size, IntPtr data, BufferUsageHint usage) => glBufferData((int)target, size, (void*)data, (int)usage);

        public static void BufferData<T>(BufferTarget target, IntPtr size, T[] data, BufferUsageHint usage) where T : unmanaged
        {
            fixed (T* p = data) glBufferData((int)target, size, p, (int)usage);
        }

        public static void BufferData<T>(BufferTarget target, IntPtr size, ref T data, BufferUsageHint usage) where T : unmanaged
        {
            fixed (T* p = &data) glBufferData((int)target, size, p, (int)usage);
        }

        public static void BufferData<T>(BufferTarget target, int size, ref T data, BufferUsageHint usage) where T : unmanaged
            => BufferData(target, (IntPtr)size, ref data, usage);

        public static void BufferSubData<T>(BufferTarget target, IntPtr offset, IntPtr size, ref T data) where T : unmanaged
        {
            fixed (T* p = &data) glBufferSubData((int)target, offset, size, p);
        }

        public static void BufferSubData<T>(BufferTarget target, int offset, int size, ref T data) where T : unmanaged
            => BufferSubData(target, (IntPtr)offset, (IntPtr)size, ref data);

        public static void Clear(ClearBufferMask mask) => glClear((int)mask);
        public static void ClearColor(Color4 c) => glClearColor(c.R, c.G, c.B, c.A);
        public static void ClearColor(float r, float g, float bl, float a) => glClearColor(r, g, bl, a);
        public static void ClearDepth(float d) => glClearDepthf(d);
        public static void ClearStencil(int s) => glClearStencil(s);
        public static void ColorMask(bool r, bool g, bool bl, bool a) => glColorMask(b(r), b(g), b(bl), b(a));
        public static void CompileShader(int shader) => glCompileShader(shader);
        public static int CreateProgram() => glCreateProgram();
        public static int CreateShader(ShaderType type) => glCreateShader((int)type);

        public static void DeleteBuffer(int buffer) => glDeleteBuffers(1, &buffer);
        public static void DeleteFramebuffer(int fb) => glDeleteFramebuffers(1, &fb);
        public static void DeleteProgram(int program) => glDeleteProgram(program);
        public static void DeleteRenderbuffer(int rb) => glDeleteRenderbuffers(1, &rb);
        public static void DeleteShader(int shader) => glDeleteShader(shader);

        public static void DeleteTextures(int n, int[] textures)
        {
            fixed (int* p = textures) glDeleteTextures(n, p);
        }

        public static void DeleteVertexArray(int vao) => glDeleteVertexArrays(1, &vao);
        public static void DepthFunc(DepthFunction func) => glDepthFunc((int)func);
        public static void DepthMask(bool flag) => glDepthMask(b(flag));
        public static void DetachShader(int program, int shader) => glDetachShader(program, shader);
        public static void Disable(EnableCap cap) => glDisable((int)cap);
        public static void DrawElements(PrimitiveType mode, int count, DrawElementsType type, int offset) => glDrawElements((int)mode, count, (int)type, offset);
        public static void Enable(EnableCap cap) => glEnable((int)cap);
        public static void EnableVertexAttribArray(int index) => glEnableVertexAttribArray(index);
        public static void Finish() => glFinish();

        public static void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget rbTarget, int rb)
            => glFramebufferRenderbuffer((int)target, (int)attachment, (int)rbTarget, rb);

        public static void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget2d texTarget, int tex, int level)
            => glFramebufferTexture2D((int)target, (int)attachment, (int)texTarget, tex, level);

        public static int GenBuffer()
        {
            int id;
            glGenBuffers(1, &id);
            return id;
        }

        public static void GenBuffers(int n, out int buffer)
        {
            int id;
            glGenBuffers(1, &id);
            buffer = id;
        }

        public static void GenerateMipmap(TextureTarget target) => glGenerateMipmap((int)target);

        public static int GenFramebuffer()
        {
            int id;
            glGenFramebuffers(1, &id);
            return id;
        }

        public static int GenRenderbuffer()
        {
            int id;
            glGenRenderbuffers(1, &id);
            return id;
        }

        public static void GenTextures(int n, int[] textures)
        {
            fixed (int* p = textures) glGenTextures(n, p);
        }

        public static int GenVertexArray()
        {
            int id;
            glGenVertexArrays(1, &id);
            return id;
        }

        public static int GetInteger(GetPName pname)
        {
            int v;
            glGetIntegerv((int)pname, &v);
            return v;
        }

        public static void GetInteger(All pname, out int value)
        {
            int v;
            glGetIntegerv((int)pname, &v);
            value = v;
        }

        public static void GetProgram(int program, GetProgramParameterName pname, out int value)
        {
            int v;
            glGetProgramiv(program, (int)pname, &v);
            value = v;
        }

        public static string GetProgramInfoLog(int program)
        {
            int len;
            glGetProgramiv(program, 0x8B84 /* GL_INFO_LOG_LENGTH */, &len);
            if (len <= 0) return string.Empty;

            byte* buf = stackalloc byte[len + 1];
            glGetProgramInfoLog(program, len + 1, &len, buf);
            return str(buf);
        }

        public static void GetShader(int shader, ShaderParameter pname, out int value)
        {
            int v;
            glGetShaderiv(shader, (int)pname, &v);
            value = v;
        }

        public static string GetShaderInfoLog(int shader)
        {
            int len;
            glGetShaderiv(shader, 0x8B84, &len);
            if (len <= 0) return string.Empty;

            byte* buf = stackalloc byte[len + 1];
            glGetShaderInfoLog(shader, len + 1, &len, buf);
            return str(buf);
        }

        public static string GetString(StringName name) => str(glGetString((int)name));
        public static string GetString(StringNameIndexed name, int index) => str(glGetStringi((int)name, index));

        public static int GetUniformBlockIndex(int program, string name)
        {
            fixed (byte* p = utf8z(name)) return glGetUniformBlockIndex(program, p);
        }

        public static int GetUniformLocation(int program, string name)
        {
            fixed (byte* p = utf8z(name)) return glGetUniformLocation(program, p);
        }

        public static void Hint(HintTarget target, HintMode mode)
        {
            // GL_GENERATE_MIPMAP_HINT is valid in WebGL2.
            glHint((int)target, (int)mode);
        }

        public static void InvalidateFramebuffer(FramebufferTarget target, int count, ref FramebufferAttachment attachment)
        {
            fixed (FramebufferAttachment* p = &attachment) glInvalidateFramebuffer((int)target, count, (int*)p);
        }

        public static void LinkProgram(int program) => glLinkProgram(program);
        public static void PixelStore(PixelStoreParameter pname, int param) => glPixelStorei((int)pname, param);

        public static void ReadPixels<T>(int x, int y, int w, int h, PixelFormat format, PixelType type, ref T data) where T : unmanaged
        {
            fixed (T* p = &data) glReadPixels(x, y, w, h, (int)format, (int)type, p);
        }

        public static void RenderbufferStorage(RenderbufferTarget target, RenderbufferInternalFormat format, int w, int h) => glRenderbufferStorage((int)target, (int)format, w, h);
        public static void Scissor(int x, int y, int w, int h) => glScissor(x, y, w, h);

        public static void ShaderSource(int shader, string source)
        {
            byte[] bytes = utf8z(source);

            fixed (byte* p = bytes)
            {
                byte* s = p;
                int len = bytes.Length - 1;
                glShaderSource(shader, 1, &s, &len);
            }
        }

        public static void StencilFunc(StencilFunction func, int @ref, int mask) => glStencilFunc((int)func, @ref, mask);
        public static void StencilOp(StencilOp fail, StencilOp zfail, StencilOp zpass) => glStencilOp((int)fail, (int)zfail, (int)zpass);

        public static void TexImage2D(TextureTarget2d target, int level, TextureComponentCount internalFormat, int w, int h, int border, PixelFormat format, PixelType type, IntPtr data)
            => glTexImage2D((int)target, level, (int)internalFormat, w, h, border, (int)format, (int)type, (void*)data);

        public static void TexImage2D<T>(TextureTarget2d target, int level, TextureComponentCount internalFormat, int w, int h, int border, PixelFormat format, PixelType type, ref T data)
            where T : unmanaged
        {
            fixed (T* p = &data) glTexImage2D((int)target, level, (int)internalFormat, w, h, border, (int)format, (int)type, p);
        }

        public static void TexParameter(TextureTarget target, TextureParameterName pname, int param) => glTexParameteri((int)target, (int)pname, param);

        public static void TexSubImage2D(TextureTarget2d target, int level, int x, int y, int w, int h, PixelFormat format, PixelType type, IntPtr data)
            => glTexSubImage2D((int)target, level, x, y, w, h, (int)format, (int)type, (void*)data);

        public static void Uniform1(int loc, int v) => glUniform1i(loc, v);
        public static void Uniform1(int loc, float v) => glUniform1f(loc, v);

        public static void Uniform2(int loc, ref osuTK.Vector2 v)
        {
            fixed (osuTK.Vector2* p = &v) glUniform2fv(loc, 1, (float*)p);
        }

        public static void Uniform3(int loc, ref osuTK.Vector3 v)
        {
            fixed (osuTK.Vector3* p = &v) glUniform3fv(loc, 1, (float*)p);
        }

        public static void Uniform4(int loc, ref osuTK.Vector4 v)
        {
            fixed (osuTK.Vector4* p = &v) glUniform4fv(loc, 1, (float*)p);
        }

        public static void UniformBlockBinding(int program, int index, int binding) => glUniformBlockBinding(program, index, binding);

        public static void UniformMatrix2(int loc, bool transpose, ref osuTK.Matrix2 m)
        {
            fixed (osuTK.Matrix2* p = &m) glUniformMatrix2fv(loc, 1, b(transpose), (float*)p);
        }

        public static void UniformMatrix3(int loc, bool transpose, ref osuTK.Matrix3 m)
        {
            fixed (osuTK.Matrix3* p = &m) glUniformMatrix3fv(loc, 1, b(transpose), (float*)p);
        }

        public static void UniformMatrix4(int loc, bool transpose, ref osuTK.Matrix4 m)
        {
            fixed (osuTK.Matrix4* p = &m) glUniformMatrix4fv(loc, 1, b(transpose), (float*)p);
        }

        public static void UseProgram(int program) => glUseProgram(program);

        public static void VertexAttribIPointer(int index, int size, VertexAttribIntegerType type, int stride, IntPtr offset)
            => glVertexAttribIPointer(index, size, (int)type, stride, offset);

        public static void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, IntPtr offset)
            => glVertexAttribPointer(index, size, (int)type, b(normalized), stride, offset);

        public static void Viewport(int x, int y, int w, int h) => glViewport(x, y, w, h);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Touch()
        {
        }
    }
}
