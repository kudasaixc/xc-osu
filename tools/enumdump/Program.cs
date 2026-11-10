using System.Reflection;
using System.Runtime.InteropServices;
string[] enums = { "BassFlags", "ChannelAttribute", "SyncFlags", "DataFlags", "PositionFlags", "LevelRetrievalFlags", "EffectType", "Configuration", "PlaybackState", "DeviceInfoFlags", "Errors", "StreamSystem", "DeviceInitFlags", "FXChannelFlags", "BQFType" };
foreach (var asm in new[] { typeof(ManagedBass.Bass).Assembly, typeof(ManagedBass.Fx.BassFx).Assembly, typeof(ManagedBass.Mix.BassMix).Assembly })
foreach (var t in asm.GetTypes().Where(t => t.IsEnum && enums.Contains(t.Name)))
{
    Console.WriteLine($"== {t.FullName}");
    foreach (var n in Enum.GetNames(t)) Console.WriteLine($"  {n} = 0x{Convert.ToInt64(Enum.Parse(t, n)):X}");
}
foreach (var tn in new[] { "ManagedBass.ChannelInfo", "ManagedBass.SampleInfo", "ManagedBass.DeviceInfo", "ManagedBass.BassInfo", "ManagedBass.FileProcedures", "ManagedBass.Fx.BQFParameters", "ManagedBass.Fx.PeakEQParameters" })
{
    var t = typeof(ManagedBass.Bass).Assembly.GetType(tn) ?? typeof(ManagedBass.Fx.BassFx).Assembly.GetType(tn);
    if (t == null) { Console.WriteLine("missing " + tn); continue; }
    Console.WriteLine($"== struct {tn} size={(t.IsValueType ? Marshal.SizeOf(t) : -1)} class={!t.IsValueType}");
    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) Console.WriteLine($"  {f.FieldType.Name} {f.Name} off={(t.IsValueType || t.IsLayoutSequential ? Marshal.OffsetOf(t, f.Name).ToInt32() : -1)}");
}
var fx = typeof(ManagedBass.Fx.BQFParameters);
var inst = Activator.CreateInstance(fx)!;
Console.WriteLine("BQF FXType=" + fx.GetProperty("FXType")!.GetValue(inst));
