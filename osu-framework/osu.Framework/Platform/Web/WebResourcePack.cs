// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// xc-osu! web port (xc softworks, kudasaixc)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Development;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace osu.Framework.Platform.Web
{
    /// <summary>
    /// Embedded resources of an assembly served from a separate pack file (tools/respack, js/xc-respack.js) instead of the
    /// assembly itself. The .NET loader copies every assembly whole into the wasm heap, where memory is never given back;
    /// with the pack, a resource only enters the heap while something reads it. Names follow <see cref="DllResourceStore"/>.
    /// </summary>
    public sealed class WebResourcePackStore : IResourceStore<byte[]>
    {
        // offset as int: the pack stays far below 2 GB, and an (int, int, int) signature is one the interpreter can always
        // call (development builds run osu.Framework interpreted)
        [DllImport("xcweb")]
        private static extern unsafe int xc_respack_read(int offset, int length, byte* dst);

        private readonly string prefix;
        private readonly long dataStart;
        private readonly Dictionary<string, (long Offset, int Length)> entries;

        public int Count => entries.Count;

        private WebResourcePackStore(string prefix, long dataStart, Dictionary<string, (long, int)> entries)
        {
            this.prefix = prefix;
            this.dataStart = dataStart;
            this.entries = entries;
        }

        /// <summary>
        /// Opens the page's pack for <paramref name="assemblyName"/>, or returns null when the page has none (development
        /// builds keep their resources inside the assembly) or it cannot be read.
        /// </summary>
        public static unsafe WebResourcePackStore? TryOpen(string assemblyName)
        {
            if (!OperatingSystem.IsBrowser())
                return null;

            try
            {
                byte[] header = new byte[12];
                int n;
                fixed (byte* p = header)
                    n = xc_respack_read(0, header.Length, p);

                if (n != header.Length || Encoding.ASCII.GetString(header, 0, 4) != "XCRP")
                {
                    Logger.Log($"resource pack: none for {assemblyName} ({n})");
                    return null;
                }

                int indexLength = BitConverter.ToInt32(header, 8);
                byte[] index = new byte[indexLength];
                fixed (byte* p = index)
                    n = xc_respack_read(header.Length, indexLength, p);
                if (n != indexLength)
                    return null;

                var map = new Dictionary<string, (long, int)>(StringComparer.Ordinal);
                string? prefix = null;
                var reader = new Utf8JsonReader(index);

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("prefix"))
                    {
                        reader.Read();
                        prefix = reader.GetString();
                    }
                    else if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("entries"))
                    {
                        reader.Read(); // [
                        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                        {
                            reader.Read();
                            string name = reader.GetString()!;
                            reader.Read();
                            long offset = reader.GetInt64();
                            reader.Read();
                            int length = reader.GetInt32();
                            reader.Read(); // ]
                            map[name] = (offset, length);
                        }
                    }
                }

                if (prefix != assemblyName)
                {
                    Logger.Log($"resource pack: the page's pack is for {prefix}, not {assemblyName}");
                    return null;
                }

                Logger.Log($"resource pack: {map.Count} resources of {assemblyName} served from the page's pack");
                return new WebResourcePackStore(prefix, header.Length + indexLength, map);
            }
            catch (Exception e)
            {
                Logger.Error(e, "resource pack: could not open");
                return null;
            }
        }

        // ---- statistics (read by the telemetry collector)

        private static long reads, readBytes, readTicks, maxReadTicks, misses, nonBackgroundReads;

        /// <summary>
        /// Reads, bytes, total / max milliseconds, lookups of missing names and reads made by the update, draw or audio
        /// thread since the last call.
        /// </summary>
        public static (long Reads, long Bytes, double TotalMs, double MaxMs, long Misses, long NonBackground) TakeStats()
        {
            double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return (Interlocked.Exchange(ref reads, 0), Interlocked.Exchange(ref readBytes, 0), Interlocked.Exchange(ref readTicks, 0) * tick,
                Interlocked.Exchange(ref maxReadTicks, 0) * tick, Interlocked.Exchange(ref misses, 0), Interlocked.Exchange(ref nonBackgroundReads, 0));
        }

        // ---- lookups

        private bool tryFind(string name, out (long Offset, int Length) entry)
        {
            string[] split = name.Split('/');
            for (int i = 0; i < split.Length - 1; i++)
                split[i] = split[i].Replace('-', '_');

            if (entries.TryGetValue($@"{prefix}.{string.Join('.', split)}", out entry))
                return true;

            Interlocked.Increment(ref misses);
            return false;
        }

        private static int nonBackgroundLogged;

        private unsafe bool read(string name, (long Offset, int Length) entry, byte* dst)
        {
            bool foreground = ThreadSafety.IsUpdateThread || ThreadSafety.IsDrawThread || ThreadSafety.IsAudioThread;
            if (foreground)
                Interlocked.Increment(ref nonBackgroundReads);

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = entry.Length == 0 ? 0 : xc_respack_read(checked((int)(dataStart + entry.Offset)), entry.Length, dst);
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;

            Interlocked.Increment(ref reads);
            Interlocked.Add(ref readBytes, Math.Max(0, n));
            Interlocked.Add(ref readTicks, ticks);

            long max;
            while (ticks > (max = Interlocked.Read(ref maxReadTicks)) && Interlocked.CompareExchange(ref maxReadTicks, ticks, max) != max)
            {
            }

            // a game thread waiting on the pack is a stutter candidate: name the first ones (tools/perfbench.mjs keeps these lines)
            if (foreground && Interlocked.Increment(ref nonBackgroundLogged) <= 40)
            {
                string thread = ThreadSafety.IsUpdateThread ? "update" : ThreadSafety.IsDrawThread ? "draw" : "audio";
                Console.WriteLine($"[respack] {name} ({entry.Length} bytes) read on the {thread} thread in {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms");
            }

            if (n == entry.Length)
                return true;

            Logger.Log($"resource pack: read failed ({n} of {entry.Length} bytes at {entry.Offset})", level: LogLevel.Important);
            return false;
        }

        public unsafe byte[]? Get(string name)
        {
            if (!tryFind(name, out var entry))
                return null;

            byte[] data = GC.AllocateUninitializedArray<byte>(entry.Length);

            fixed (byte* p = data)
                return read(name, entry, p) ? data : null;
        }

        public Task<byte[]?> GetAsync(string name, CancellationToken cancellationToken = default)
            => tryFind(name, out _) ? Task.Run(() => Get(name), cancellationToken) : Task.FromResult<byte[]?>(null);

        Task<byte[]> IResourceStore<byte[]>.GetAsync(string name, CancellationToken cancellationToken) => GetAsync(name, cancellationToken)!;

        /// <summary>
        /// The resource in native memory, freed when the stream is disposed: texture and archive readers never put the
        /// whole file on the managed heap (large objects are only reclaimed by full collections).
        /// </summary>
        public unsafe Stream? GetStream(string name)
        {
            if (!tryFind(name, out var entry))
                return null;

            byte* p = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, entry.Length));

            if (!read(name, entry, p))
            {
                NativeMemory.Free(p);
                return null;
            }

            return new NativeBufferStream(p, entry.Length);
        }

        public IEnumerable<string> GetAvailableResources() =>
            entries.Keys.Select(n =>
            {
                n = n.Substring(n.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length + 1 : 0);

                int lastDot = n.LastIndexOf('.');
                char[] chars = n.ToCharArray();

                for (int i = 0; i < lastDot; i++)
                {
                    if (chars[i] == '.')
                        chars[i] = '/';
                }

                return new string(chars);
            });

        public void Dispose()
        {
        }

        private sealed unsafe class NativeBufferStream : UnmanagedMemoryStream
        {
            private byte* buffer;

            public NativeBufferStream(byte* buffer, int length)
                : base(buffer, length)
            {
                this.buffer = buffer;
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);

                byte* p = buffer;
                buffer = null;
                if (p != null)
                    NativeMemory.Free(p);
            }

            ~NativeBufferStream()
            {
                Dispose(false);
            }
        }
    }
}
