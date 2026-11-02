using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace KspTest.Xunit.Generator
{
    /// <summary>
    ///     A [KspFact] for every [UITest] in a dotnet test project's in-game assemblies (its KspTestPlugin items, which
    ///     KspTest.Xunit's build steps hand the compiler as AdditionalFiles): the same namespace, class and method name, so
    ///     the xUnit test's full name is the in-game test's.  The assemblies target the game and are read as metadata,
    ///     never loaded.  A test's local mods ([UITest(Mods)] entries that are paths) are made absolute from its in-game
    ///     project's folder, where they were written.
    /// </summary>
    [Generator]
    public sealed class UITestFactGenerator : IIncrementalGenerator
    {
        private static readonly DiagnosticDescriptor Unreadable = new DiagnosticDescriptor("KSPTEST001", "In-game assembly not readable",
            "KspTest: no tests from {0}: {1}", "KspTest", DiagnosticSeverity.Warning, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<(string Path, string ProjectDir)> plugins = context.AdditionalTextsProvider
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Select((pair, _) =>
                {
                    var options = pair.Right.GetOptions(pair.Left);
                    if (!options.TryGetValue("build_metadata.AdditionalFiles.KspTestPlugin", out string? flag) || flag != "true")
                        return (Path: "", ProjectDir: "");
                    options.TryGetValue("build_metadata.AdditionalFiles.KspTestProject", out string? project);
                    return (Path: pair.Left.Path, ProjectDir: string.IsNullOrEmpty(project) ? "" : System.IO.Path.GetDirectoryName(project) ?? "");
                })
                .Where(p => p.Path.Length > 0);

            context.RegisterSourceOutput(plugins, (spc, plugin) =>
            {
                List<Test> tests;
                try
                {
                    tests = Read(plugin.Path);
                }
                catch (Exception e) when (e is IOException || e is BadImageFormatException || e is UnauthorizedAccessException)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(Unreadable, Location.None, plugin.Path, e.Message));
                    return;
                }

                if (tests.Count > 0)
                    spc.AddSource(System.IO.Path.GetFileNameWithoutExtension(plugin.Path) + ".KspFacts.g.cs",
                        SourceText.From(Emit(tests, plugin.ProjectDir, System.IO.Path.GetFileName(plugin.Path)), Encoding.UTF8));
            });
        }

        private sealed class Test
        {
            public string Namespace = "";
            public List<string> Types = new List<string>();   // outermost first
            public string Method = "";
            public Dictionary<string, object?> Named = new Dictionary<string, object?>();

            public string Id => (Namespace.Length > 0 ? Namespace + "." : "") + string.Join(".", Types) + "." + Method;
            public string? Str(string name) => Named.TryGetValue(name, out object? v) ? v as string : null;
        }

        private static List<Test> Read(string path)
        {
            var tests = new List<Test>();
            using FileStream fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            MetadataReader md = pe.GetMetadataReader();
            foreach (TypeDefinitionHandle th in md.TypeDefinitions)
            {
                TypeDefinition type = md.GetTypeDefinition(th);
                foreach (MethodDefinitionHandle mh in type.GetMethods())
                {
                    MethodDefinition method = md.GetMethodDefinition(mh);
                    foreach (CustomAttributeHandle ah in method.GetCustomAttributes())
                    {
                        CustomAttribute a = md.GetCustomAttribute(ah);
                        if (AttributeType(md, a) != "KspTest.UITestAttribute")
                            continue;
                        var test = new Test { Method = md.GetString(method.Name) };
                        TypeDefinition t = type;
                        while (true)
                        {
                            test.Types.Insert(0, md.GetString(t.Name));
                            if (t.GetDeclaringType().IsNil)
                                break;
                            t = md.GetTypeDefinition(t.GetDeclaringType());
                        }

                        test.Namespace = md.GetString(t.Namespace);
                        CustomAttributeValue<string> value = a.DecodeValue(new TypeNames());
                        foreach (CustomAttributeNamedArgument<string> n in value.NamedArguments)
                            if (n.Name != null)
                                test.Named[n.Name] = n.Value;
                        tests.Add(test);
                    }
                }
            }

            return tests;
        }

        private static string AttributeType(MetadataReader md, CustomAttribute a)
        {
            EntityHandle parent;
            if (a.Constructor.Kind == HandleKind.MemberReference)
                parent = md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent;
            else if (a.Constructor.Kind == HandleKind.MethodDefinition)
                parent = md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType();
            else
                return "";
            if (parent.Kind == HandleKind.TypeReference)
            {
                TypeReference r = md.GetTypeReference((TypeReferenceHandle)parent);
                return md.GetString(r.Namespace) + "." + md.GetString(r.Name);
            }

            if (parent.Kind == HandleKind.TypeDefinition)
            {
                TypeDefinition d = md.GetTypeDefinition((TypeDefinitionHandle)parent);
                return md.GetString(d.Namespace) + "." + md.GetString(d.Name);
            }

            return "";
        }

        private static string Emit(List<Test> tests, string projectDir, string assembly)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated/> KspTest: the [UITest]s in ").Append(assembly).Append(", one [KspFact] each\n");
            foreach (IGrouping<string, Test> ns in tests.GroupBy(t => t.Namespace))
            {
                string indent = "";
                if (ns.Key.Length > 0)
                {
                    sb.Append("namespace ").Append(ns.Key).Append("\n{\n");
                    indent = "    ";
                }

                foreach (IGrouping<string, Test> cls in ns.GroupBy(t => string.Join(".", t.Types)))
                {
                    List<string> types = cls.First().Types;
                    string inner = indent;
                    foreach (string type in types)
                    {
                        sb.Append(inner).Append("public partial class ").Append(type).Append('\n').Append(inner).Append("{\n");
                        inner += "    ";
                    }

                    foreach (Test t in cls)
                    {
                        var args = new List<string> { "Test = " + Literal(t.Id) };
                        Add(args, "Kind", t.Str("Kind"));
                        Add(args, "Requires", t.Str("Requires"));
                        Add(args, "Mods", Mods(t.Str("Mods"), projectDir));
                        Add(args, "Save", t.Str("Save"));
                        Add(args, "NewGame", t.Str("NewGame"));
                        if (t.Named.TryGetValue("Isolated", out object? isolated) && isolated is bool b && b)
                            args.Add("Isolated = true");
                        sb.Append(inner).Append("[global::KspTest.Xunit.KspFact(").Append(string.Join(", ", args)).Append(")]\n");
                        sb.Append(inner).Append("public void ").Append(t.Method).Append("() => global::KspTest.Xunit.Ksp.Run(test: ")
                            .Append(Literal(t.Id)).Append(");\n");
                    }

                    for (int i = types.Count - 1; i >= 0; i--)
                    {
                        inner = inner.Substring(4);
                        sb.Append(inner).Append("}\n");
                    }
                }

                if (ns.Key.Length > 0)
                    sb.Append("}\n");
            }

            return sb.ToString();
        }

        // CKAN identifiers as they are; a local mod (a path to its GameData/ModName folder) from the in-game project's folder
        private static string? Mods(string? mods, string projectDir)
        {
            if (string.IsNullOrWhiteSpace(mods))
                return null;
            IEnumerable<string> entries = mods!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).Where(m => m.Length > 0)
                .Select(m => (m.Contains("/") || m.Contains("\\")) && projectDir.Length > 0 ? Path.GetFullPath(Path.Combine(projectDir, m)) : m);
            return string.Join(";", entries);
        }

        private static void Add(List<string> args, string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                args.Add(name + " = " + Literal(value!));
        }

        private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, true);

        // enough of a type for DecodeValue: names, with every enum taken as an int (UITest's StartState is)
        private sealed class TypeNames : ICustomAttributeTypeProvider<string>
        {
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetSystemType() => "System.Type";
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
            public string GetTypeFromSerializedName(string name) => name;
            public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
            public bool IsSystemType(string type) => type == "System.Type";
        }
    }
}
