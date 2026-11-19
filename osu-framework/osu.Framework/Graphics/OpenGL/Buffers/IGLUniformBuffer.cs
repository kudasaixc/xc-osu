// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.OpenGL.Buffers
{
    internal interface IGLUniformBuffer
    {
        int Id { get; }

        /// <summary>Byte offset inside <see cref="Id"/> when backed by a shared uniform ring buffer, or 0 for standalone UBOs.</summary>
        int Offset => 0;

        /// <summary>Byte size of the slice inside <see cref="Id"/> when using <c>BindBufferRange</c>, or 0 for full-buffer <c>BindBufferBase</c>.</summary>
        int ByteSize => 0;

        void Flush();
    }
}
