// xc-osu! resource pack builder (xc softworks / kudasaixc)
//
// Reads every manifest resource of an assembly (osu.Game.Resources) and writes:
//   <out>/<Assembly>.<hash>.xcpack   one file: "XCRP" | u32 version | u32 index length | index JSON | data
//   <out>/<Assembly>.xcpack.json     { "file": "<pack file name>", "size": n, "sha256": "...", "entries": n }
//   <ilink>                          ILLink substitutions removing those resources from the trimmed assembly
// The game reads resources from the pack on demand (osu.Framework/Platform/Web/WebResourcePack.cs), so the 130 MB of
// textures / samples / tracks never sit in the wasm heap, where the .NET loader would otherwise copy the whole assembly.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: respack <assembly.dll> <out dir> <substitutions.xml>");
    return 2;
}

string dll = args[0], outDir = args[1], substitutions = args[2];
using var fs = File.OpenRead(dll);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
string asmName = md.GetString(md.GetAssemblyDefinition().Name);
var resDir = pe.PEHeaders.CorHeader!.ResourcesDirectory;
var image = pe.GetEntireImage().GetContent();
int resStart = pe.PEHeaders.TryGetDirectoryOffset(resDir, out int o) ? o : throw new Exception("no resources directory");

var entries = new List<(string name, byte[] data)>();
foreach (var h in md.ManifestResources)
{
    var r = md.GetManifestResource(h);
    if (!r.Implementation.IsNil) continue; // linked / forwarded resource: not stored in this file
    string resName = md.GetString(r.Name);
    // .resources tables are read by System.Resources.ResourceManager straight from the assembly (localisation): keep them there
    if (resName.EndsWith(".resources", StringComparison.Ordinal)) continue;
    int at = resStart + (int)r.Offset;
    int len = BitConverter.ToInt32(image.AsSpan(at, 4));
    entries.Add((resName, image.AsSpan(at + 4, len).ToArray()));
}

entries.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

var index = new List<object[]>();
long offset = 0;
foreach (var (name, data) in entries)
{
    index.Add(new object[] { name, offset, data.Length });
    offset += data.Length;
}

byte[] indexJson = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, prefix = asmName, entries = index });
Directory.CreateDirectory(outDir);
string tmp = Path.Combine(outDir, asmName + ".xcpack.tmp");
using (var w = new BinaryWriter(File.Create(tmp)))
{
    w.Write(Encoding.ASCII.GetBytes("XCRP"));
    w.Write(1);
    w.Write(indexJson.Length);
    w.Write(indexJson);
    foreach (var (_, data) in entries) w.Write(data);
}

string sha;
using (var s = File.OpenRead(tmp)) sha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
string file = $"{asmName}.{sha[..10]}.xcpack";
foreach (var old in Directory.GetFiles(outDir, asmName + ".*.xcpack")) if (Path.GetFileName(old) != file) File.Delete(old);
File.Move(tmp, Path.Combine(outDir, file), true);
long size = new FileInfo(Path.Combine(outDir, file)).Length;
File.WriteAllText(Path.Combine(outDir, asmName + ".xcpack.json"),
    JsonSerializer.Serialize(new { file, size, sha256 = sha, entries = entries.Count, dataBytes = offset }));

var xml = new StringBuilder("<linker>\n  <assembly fullname=\"" + asmName + "\">\n");
foreach (var (name, _) in entries) xml.Append("    <resource name=\"").Append(System.Security.SecurityElement.Escape(name)).Append("\" action=\"remove\" />\n");
xml.Append("  </assembly>\n</linker>\n");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(substitutions))!);
File.WriteAllText(substitutions, xml.ToString());

Console.WriteLine($"respack: {entries.Count} resources, {offset / 1048576.0:0.0} MB -> {file} ({size / 1048576.0:0.0} MB)");
return 0;
