// Rewrites the AltCover visit calls in instrumented assemblies to KspTest.Coverage.FastVisit.Visit (same signature).
//   CovPatch [-r=DIR]... <dll>...    each rewritten in place; prints the calls redirected per assembly. -r: a folder whose
//                                    folders with DLLs (it too) resolve the assemblies' references (the game's, say)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static int Main(string[] args)
    {
        var refDirs = new List<string>();
        foreach (string r in args.Where(a => a.StartsWith("-r=")).Select(a => a.Substring(3)))
            if (Directory.Exists(r))
                refDirs.AddRange(new[] { r }.Concat(Directory.EnumerateDirectories(r, "*", SearchOption.AllDirectories))
                    .Where(d => Directory.EnumerateFiles(d, "*.dll").Any()));
        foreach (string path in args.Where(a => !a.StartsWith("-r=")))
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            foreach (string d in refDirs)
                resolver.AddSearchDirectory(d);
            string tmp = path + ".patched";
            int n = 0;
            using (ModuleDefinition module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver }))
            {
                var asm = new AssemblyNameReference("KspTest.Coverage.FastVisit", new Version(1, 0, 0, 0));
                var type = new TypeReference("KspTest.Coverage", "FastVisit", module, asm);
                var visit = new MethodReference("Visit", module.TypeSystem.Void, type) { HasThis = false };
                visit.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                visit.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
                bool added = false;
                foreach (TypeDefinition t in module.GetTypes())
                    foreach (MethodDefinition m in t.Methods.Where(m => m.HasBody))
                        foreach (Instruction i in m.Body.Instructions)
                            if (i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "Visit" &&
                                r.DeclaringType.FullName == "AltCover.Recorder.Instance" && r.Parameters.Count == 2 &&
                                r.Parameters[0].ParameterType.FullName == "System.String" && r.Parameters[1].ParameterType.FullName == "System.Int32")
                            {
                                if (!added)
                                {
                                    module.AssemblyReferences.Add(asm);
                                    added = true;
                                }

                                i.Operand = visit;
                                n++;
                            }

                module.Write(tmp);
            }

            File.Copy(tmp, path, true);
            File.Delete(tmp);
            Console.WriteLine($"{Path.GetFileName(path)}: {n} visit calls redirected");
            if (n == 0)
                return 1;
        }

        return 0;
    }
}
