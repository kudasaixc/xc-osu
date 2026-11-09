// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks)

using System;
using System.Drawing;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.Rendering;

namespace osu.Framework.Platform.Web
{
    internal class WebGLSurface : IGraphicsSurface, IOpenGLGraphicsSurface
    {
        public IntPtr WindowHandle => IntPtr.Zero;
        public IntPtr DisplayHandle => IntPtr.Zero;

        public GraphicsSurfaceType Type => GraphicsSurfaceType.OpenGL;

        public void Initialise() => WebDrawWorker.WaitForContext();

        public Size GetDrawableSize()
        {
            var s = WebShared.Instance;
            return new Size(Math.Max(1, s.Read(WebShared.WIDTH)), Math.Max(1, s.Read(WebShared.HEIGHT)));
        }

        // presentation is driven by requestAnimationFrame; never block with glFinish.
        public bool VerticalSync
        {
            get => false;
            set { }
        }

        public IntPtr WindowContext => WebDrawWorker.ContextHandle;
        public IntPtr CurrentContext => WebDrawWorker.ContextHandle;
        public int? BackbufferFramebuffer => null;

        public void SwapBuffers()
        {
        }

        public void CreateContext()
        {
        }

        public void MakeCurrent(IntPtr context)
        {
        }

        public void ClearCurrent()
        {
        }

        public void DeleteContext(IntPtr context)
        {
        }

        public IntPtr GetProcAddress(string symbol) => IntPtr.Zero;
    }

    internal class WebGLRenderer : GLRenderer
    {
        protected override void Initialise(IGraphicsSurface graphicsSurface)
        {
            WebDrawWorker.WaitForContext();
            WebDrawWorker.Invoke(() => base.Initialise(graphicsSurface));
        }
    }
}
