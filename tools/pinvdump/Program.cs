using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
// usage: pinvdump <dir> : prints module!entry for every DllImport method referenced (MemberRef) by any other assembly in dir
var dir = args[0];
var used = new HashSet<string>();
var imports = new List<(string asm, string type, string name, string module, string entry)>();
foreach (var f in Directory.GetFiles(dir, "*.dll"))
{
    try {
        using var pe = new PEReader(File.OpenRead(f));
        if (!pe.HasMetadata) continue;
        var md = pe.GetMetadataReader();
        foreach (var h in md.MemberReferences) {
            var mr = md.GetMemberReference(h);
            string parent = "";
            if (mr.Parent.Kind == HandleKind.TypeReference) { var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent); parent = md.GetString(tr.Name); }
            used.Add(parent + "." + md.GetString(mr.Name));
        }
        foreach (var th in md.TypeDefinitions) {
            var td = md.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods()) {
                var m = md.GetMethodDefinition(mh);
                var imp = m.GetImport();
                if (imp.Module.IsNil) continue;
                var mod = md.GetString(md.GetModuleReference(imp.Module).Name);
                var entry = imp.Name.IsNil ? md.GetString(m.Name) : md.GetString(imp.Name);
                imports.Add((Path.GetFileName(f), md.GetString(td.Name), md.GetString(m.Name), mod, entry));
            }
        }
    } catch { }
}
foreach (var i in imports.Where(i => args.Length < 2 || i.module.Contains(args[1])).OrderBy(i => i.module).ThenBy(i => i.entry))
    Console.WriteLine($"{(used.Contains(i.type + "." + i.name) ? "USED" : "    ")} {i.module} {i.entry} {i.type}.{i.name}");
