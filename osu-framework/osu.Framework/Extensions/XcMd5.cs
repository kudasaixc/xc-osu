// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks, kudasaixc): browser .NET has no MD5 provider, osu! hashes everything with it.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace osu.Framework.Extensions
{
    /// <summary>Managed RFC 1321 MD5, used where the platform has no MD5 implementation (WebAssembly).</summary>
    internal static class XcMd5
    {
        private static readonly uint[] k = new uint[64];

        private static readonly int[] r =
        {
            7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
            5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
            4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
            6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
        };

        static XcMd5()
        {
            for (int i = 0; i < 64; i++)
                k[i] = (uint)(long)Math.Floor(Math.Abs(Math.Sin(i + 1)) * 4294967296.0);
        }

        private struct State
        {
            public uint A, B, C, D;
            public long Length;
        }

        private static State init() => new State { A = 0x67452301, B = 0xefcdab89, C = 0x98badcfe, D = 0x10325476 };

        private static void block(ref State s, ReadOnlySpan<byte> chunk)
        {
            Span<uint> m = stackalloc uint[16];
            for (int i = 0; i < 16; i++)
                m[i] = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(i * 4, 4));

            uint a = s.A, b = s.B, c = s.C, d = s.D;

            for (int i = 0; i < 64; i++)
            {
                uint f;
                int g;

                if (i < 16) { f = (b & c) | (~b & d); g = i; }
                else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) & 15; }
                else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) & 15; }
                else { f = c ^ (b | ~d); g = (7 * i) & 15; }

                uint tmp = d;
                d = c;
                c = b;
                b += BitOperations.RotateLeft(a + f + k[i] + m[g], r[i]);
                a = tmp;
            }

            s.A += a;
            s.B += b;
            s.C += c;
            s.D += d;
        }

        private static byte[] finish(ref State s, ReadOnlySpan<byte> tail)
        {
            // tail.Length < 64
            Span<byte> pad = stackalloc byte[128];
            tail.CopyTo(pad);
            pad[tail.Length] = 0x80;
            int total = tail.Length + 1 + 8 <= 64 ? 64 : 128;
            BinaryPrimitives.WriteInt64LittleEndian(pad.Slice(total - 8, 8), s.Length * 8);

            for (int o = 0; o < total; o += 64)
                block(ref s, pad.Slice(o, 64));

            byte[] result = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), s.A);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), s.B);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), s.C);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), s.D);
            return result;
        }

        public static byte[] HashData(ReadOnlySpan<byte> data)
        {
            var s = init();
            s.Length = data.Length;
            int full = data.Length & ~63;
            for (int o = 0; o < full; o += 64)
                block(ref s, data.Slice(o, 64));
            return finish(ref s, data.Slice(full));
        }

        public static byte[] HashData(Stream stream)
        {
            var s = init();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

            try
            {
                int filled = 0;
                int read;

                while ((read = stream.Read(buffer, filled, 64 * 1024 - filled)) > 0)
                {
                    filled += read;
                    s.Length += read;
                    int full = filled & ~63;
                    for (int o = 0; o < full; o += 64)
                        block(ref s, buffer.AsSpan(o, 64));
                    Buffer.BlockCopy(buffer, full, buffer, 0, filled - full);
                    filled -= full;
                }

                return finish(ref s, buffer.AsSpan(0, filled));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
