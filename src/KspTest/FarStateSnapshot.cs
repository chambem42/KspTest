using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Every value field of FAR's per-part aerodynamic state on a vessel - wing and control-surface modules, their wing
    /// interaction objects and scratch, and the voxel parts' FARAeroPartModule - as path -> value, so two snapshots
    /// taken with nothing but a prediction between them show whatever the prediction left behind. Per-slot scratch
    /// arrays contribute only slot 0 (the flight's); other slots are a prediction's own. The vessel's FARVesselAero
    /// (its voxel sections and the rest) is included, all but lastSimResults, the prediction API's own memo.
    /// </summary>
    public static class FarStateSnapshot
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const int MaxDepth = 4;
        private const int MaxElements = 256;
        private static readonly HashSet<string> Skipped = new HashSet<string> { "lastSimResults" };

        public static Dictionary<string, string> Take(Vessel v)
        {
            var into = new Dictionary<string, string>();
            if (v == null) return into;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsFarState(m.GetType())) continue;
                    Walk(m, $"{p.partInfo.name}#{p.flightID % 10000}.{m.GetType().Name}", 0, into, new HashSet<object>(ReferenceComparer.Instance));
                }
            foreach (VesselModule vm in v.GetComponents<VesselModule>())
                if (vm.GetType().Name == "FARVesselAero")
                    Walk(vm, "vessel#0.FARVesselAero", 0, into, new HashSet<object>(ReferenceComparer.Instance));
            return into;
        }

        /// <summary>Paths whose value differs (or exists in only one), with before -> after.</summary>
        public static List<string> Diff(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            var changed = new List<string>();
            foreach (var kv in before)
                if (!after.TryGetValue(kv.Key, out string a) || a != kv.Value)
                    changed.Add($"{kv.Key}: {kv.Value} -> {a ?? "(gone)"}");
            foreach (var kv in after)
                if (!before.ContainsKey(kv.Key))
                    changed.Add($"{kv.Key}: (new) -> {kv.Value}");
            return changed;
        }

        // "partName#1234.Module.field: a -> b": the part (names can hold dots) and the field path after it.
        private static readonly System.Text.RegularExpressions.Regex PartPrefix =
            new System.Text.RegularExpressions.Regex(@"^(.*?#\d+)\.");

        public static string PartOf(string change)
        {
            var m = PartPrefix.Match(change);
            return m.Success ? m.Groups[1].Value : change;
        }

        public static string FieldOf(string change)
        {
            string path = change.Substring(0, change.IndexOf(": ", StringComparison.Ordinal));
            var m = PartPrefix.Match(path);
            return m.Success ? path.Substring(m.Length) : path;
        }

        /// <summary>Changed paths grouped by field name (the part prefix dropped), most first: "stall x37, Cl x37, ...".</summary>
        public static string Summarize(List<string> changed, int max = 25)
        {
            var byField = changed.Select(FieldOf)
                                 .GroupBy(f => f).OrderByDescending(g => g.Count())
                                 .Select(g => $"{g.Key} x{g.Count()}").ToList();
            return string.Join(", ", byField.Take(max)) + (byField.Count > max ? $", ... ({byField.Count} fields)" : "");
        }

        private static bool IsFarState(Type t)
        {
            for (; t != null && t != typeof(PartModule) && t != typeof(object); t = t.BaseType)
                if (t.Name == "FARWingAerodynamicModel" || t.Name == "FARAeroPartModule")
                    return true;
            return false;
        }

        // FAR's own objects (namespaces ferram4 / FerramAerospaceResearch) are walked into; Unity and KSP objects are not.
        private static bool IsFarType(Type t) =>
            t.Namespace != null && (t.Namespace.StartsWith("ferram4") || t.Namespace.StartsWith("FerramAerospaceResearch"));

        private static void Walk(object o, string path, int depth, Dictionary<string, string> into, HashSet<object> seen)
        {
            if (o == null || depth > MaxDepth || !seen.Add(o)) return;
            for (Type t = o.GetType(); t != null && t != typeof(PartModule) && t != typeof(VesselModule) && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(Fields))
                {
                    if (f.IsLiteral || f.IsStatic || Skipped.Contains(f.Name)) continue;
                    object val;
                    try { val = f.GetValue(o); }
                    catch (Exception) { continue; }
                    Record(val, f.FieldType, $"{path}.{f.Name}", depth, into, seen);
                }
        }

        private static void Record(object val, Type declared, string path, int depth, Dictionary<string, string> into, HashSet<object> seen)
        {
            if (val == null)
            {
                if (!typeof(UnityEngine.Object).IsAssignableFrom(declared)) into[path] = "null";
                return;
            }
            Type t = val.GetType();
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal))
            {
                into[path] = Format(val);
                return;
            }
            if (t == typeof(Vector3) || t == typeof(Vector3d) || t == typeof(Quaternion) || t == typeof(Vector2) || t == typeof(Vector4))
            {
                into[path] = Format(val);
                return;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(t) || typeof(Delegate).IsAssignableFrom(t))
                return;
            if (t.IsArray)
            {
                var arr = (Array)val;
                // A per-slot array: only the flight's slot.
                int n = arr.Length == 64 ? 1 : Math.Min(arr.Length, MaxElements);
                for (int i = 0; i < n; ++i)
                    Record(arr.GetValue(i), t.GetElementType(), $"{path}[{i}]", depth + 1, into, seen);
                return;
            }
            if (val is IList list && IsFarType(t.IsGenericType ? t.GetGenericArguments()[0] : t))
            {
                for (int i = 0; i < Math.Min(list.Count, MaxElements); ++i)
                    Record(list[i], typeof(object), $"{path}[{i}]", depth + 1, into, seen);
                return;
            }
            if (t.IsValueType || IsFarType(t))
            {
                if (IsFarType(t) && typeof(PartModule).IsAssignableFrom(t))
                    return;   // another part's module: snapshotted on its own
                Walk(val, path, depth + 1, into, seen);
            }
        }

        private static string Format(object v)
        {
            switch (v)
            {
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case Vector3 a: return $"({a.x.ToString("R", CultureInfo.InvariantCulture)},{a.y.ToString("R", CultureInfo.InvariantCulture)},{a.z.ToString("R", CultureInfo.InvariantCulture)})";
                case Vector3d a: return $"({a.x.ToString("R", CultureInfo.InvariantCulture)},{a.y.ToString("R", CultureInfo.InvariantCulture)},{a.z.ToString("R", CultureInfo.InvariantCulture)})";
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
