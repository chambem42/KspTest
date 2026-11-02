using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KspTest.ServePlugins
{
    /// <summary>
    /// FAR's wing parts on the editor craft, by reflection (any FAR build): each one's own and effective aspect ratio and,
    /// on a build with whole-wing aspect ratios (fix/spanwise-aspect-ratio), the wing group it took its aspect ratio from.
    /// </summary>
    public static class FarWingsCommands
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static object Get(object o, string name)
        {
            if (o == null)
                return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (f != null)
                    return f.GetValue(o);
                PropertyInfo p = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0)
                    return p.GetValue(o, null);
            }
            return null;
        }

        private static double? D(object v) => v == null ? (double?)null : Convert.ToDouble(v);

        private static string Id(Part p) => p == null ? null : p.partInfo.name + "_" + p.craftID;

        [ServeCommand("farwings", "[match=TEXT]: FAR's wing parts on the editor craft: span (b_2), MAC, area, shielded, own and " +
                                  "effective aspect ratio; on a whole-wing build, the wing group each took it from (its members, " +
                                  "tip to tip, area, aspect ratio) and whether it counts as a strip (Sliver)")]
        public static IEnumerator FarWings(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            var ship = EditorLogic.fetch?.ship;
            if (ship == null)
            {
                answer["error"] = "no editor craft";
                yield break;
            }

            string match = q["match"];
            var rows = new List<object>();
            var groups = new Dictionary<object, int>();
            var groupRows = new List<object>();
            foreach (Part p in ship.parts)
            {
                PartModule w = p.Modules.Cast<PartModule>().FirstOrDefault(m => m.GetType().Name is "FARWingAerodynamicModel" or "FARControllableSurface");
                if (w == null || (match != null && Id(p).IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                var row = new Dictionary<string, object>
                {
                    ["id"] = Id(p),
                    ["module"] = w.GetType().Name,
                    // in the root part's frame, as the craft file places it
                    ["pos"] = Pos(ship.parts[0].transform.InverseTransformPoint(p.transform.position)),
                    ["parent"] = Id(p.parent),
                    ["b_2"] = D(Get(w, "b_2_actual")),
                    ["MAC"] = D(Get(w, "MAC_actual")),
                    ["S"] = D(Get(w, "S")),
                    ["shielded"] = Get(w, "isShielded"),
                    ["transformed_AR"] = D(Get(w, "transformed_AR")),
                    ["effective_AR"] = D(Get(w, "effective_AR")),
                };
                object wi = Get(w, "WingInteraction") ?? Get(w, "wingInteraction");
                row["ARFactor"] = D(Get(wi, "ARFactor"));
                object g = Get(wi, "wingGroup");
                if (g != null)
                {
                    if (!groups.TryGetValue(g, out int gi))
                    {
                        gi = groups.Count;
                        groups[g] = gi;
                        var members = (Get(g, "Members") as IEnumerable)?.Cast<PartModule>().Select(m => Id(m.part)).ToList();
                        double? ar = null;
                        try
                        {
                            ar = D(g.GetType().GetMethod("AspectRatio", Any)?.Invoke(g, null));
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[farwings] AspectRatio: " + e.InnerException?.Message);
                        }
                        groupRows.Add(new Dictionary<string, object>
                        {
                            ["group"] = gi,
                            ["generation"] = Get(g, "Generation"),
                            ["members"] = members,
                            ["panels"] = Get(g, "PanelCount"),
                            ["TipToTip"] = D(Get(g, "TipToTip")),
                            ["B2"] = D(Get(g, "B2")),
                            ["Area"] = D(Get(g, "Area")),
                            ["ControlArea"] = D(Get(g, "ControlArea")),
                            ["BodyStrip"] = D(Get(g, "BodyStrip")),
                            ["RootOnBody"] = Get(g, "RootOnBody"),
                            ["RootOnCenter"] = Get(g, "RootOnCenter"),
                            ["Straddles"] = Get(g, "Straddles"),
                            ["AspectRatio"] = ar,
                        });
                    }
                    row["group"] = gi;
                }
                Type wg = wi?.GetType().GetNestedType("WingGroup", Any);
                MethodInfo sliver = wg?.GetMethod("Sliver", Any);
                if (sliver != null)
                    row["sliver"] = sliver.Invoke(null, new object[] { w });
                object host = Get(wi, "hostWing");
                if (host != null && groups.TryGetValue(host, out int hi))
                    row["hostGroup"] = hi;
                rows.Add(row);
            }

            answer["wings"] = rows;
            answer["groups"] = groupRows;
            yield break;
        }

        private static double[] Pos(Vector3 v) => new[] { Math.Round(v.x, 3), Math.Round(v.y, 3), Math.Round(v.z, 3) };
    }
}
