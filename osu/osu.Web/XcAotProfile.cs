// xc-osu! AOT profile collection (xc softworks / kudasaixc)
//
// The AOT build compiles whole assemblies, but generic instantiations it cannot see at compile time (Transform<Vector2>,
// Transform<float, DefaultEasingFunction, SomeDrawable>, Dictionary<SomeStruct, ...> ...) still run in the interpreter:
// ~4000 such methods per session, many of them on the update thread every frame. A build made with the Mono AOT
// profiler (tools/aotpub.sh XC_AOT_PROFILER=1, page parameter ?aotprofile) records every method the interpreter had to
// run; tools/perfbench.mjs saves it and the next AOT build compiles those instantiations too (tools/aotpub.sh
// XC_AOT_PROFILE=<file>, which passes it to the compiler without "profile-only", so nothing else changes).
//
// Mono takes the first execution of XcSaveAotProfileNow as the moment to save (the name must be unique: the profiler
// matched a bare "Write" against System.Threading.Volatile.Write), then hands the data to Send (signature fixed by it).

using System;

namespace osu.Web
{
    public static class XcAotProfile
    {
        /// <summary>Calling this the first time makes the AOT profiler save what it recorded (configured in main.js).</summary>
        /// <remarks>Never inlined: the profiler also fires when the method is inlined into a caller being compiled.</remarks>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void XcSaveAotProfileNow() => Console.WriteLine("[aotprof] requested");

        public static unsafe void Send(ref byte data, int length, string extra)
        {
            byte[] bytes;
            fixed (byte* p = &data)
                bytes = new ReadOnlySpan<byte>(p, length).ToArray();

            string b64 = Convert.ToBase64String(bytes);
            const int chunk = 6000;
            int parts = (b64.Length + chunk - 1) / chunk;

            for (int i = 0; i < parts; i++)
                Console.WriteLine($"[aotprof] {i}/{parts} {b64.Substring(i * chunk, Math.Min(chunk, b64.Length - i * chunk))}");

            Console.WriteLine($"[aotprof] end {length}");
        }
    }
}
