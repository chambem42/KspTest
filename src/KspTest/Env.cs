using System;
using System.Collections.Generic;

namespace KspTest
{
    /// <summary>What optional mods are loaded. Tags are matched against UITestAttribute.Requires.</summary>
    public static class Env
    {
        private static readonly Dictionary<string, string> TagToAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "RP-1", "RP0" },
            { "RP1", "RP0" },
            { "MechJeb", "MechJeb2" },
            { "FAR", "FerramAerospaceResearch" },
            { "Kerbalism", "Kerbalism" },
            { "kOS", "kOS" },
            { "ClickThroughBlocker", "ClickThroughBlocker" },
        };

        /// <summary>Matches the KSPAssembly name (RP0.dll registers as "RP-0"), the dll file name, or the CLR assembly name.</summary>
        public static bool HasAssembly(string name)
        {
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                if (string.Equals(a.name, name, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(a.dllName, name, StringComparison.OrdinalIgnoreCase)) return true;
                try { if (string.Equals(a.assembly?.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            }
            return false;
        }

        public static bool HasRP1 => HasAssembly("RP0");
        public static bool HasMechJeb => HasAssembly("MechJeb2");
        public static bool HasFAR => HasAssembly("FerramAerospaceResearch");

        public static bool HasTag(string tag)
        {
            return TagToAssembly.TryGetValue(tag.Trim(), out var asm) ? HasAssembly(asm) : HasAssembly(tag.Trim());
        }

        /// <summary>Returns the missing tags from a comma-separated Requires list (empty when satisfied).</summary>
        public static List<string> Missing(string requires)
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(requires)) return missing;
            foreach (var t in requires.Split(','))
                if (t.Trim().Length > 0 && !HasTag(t)) missing.Add(t.Trim());
            return missing;
        }
    }
}
