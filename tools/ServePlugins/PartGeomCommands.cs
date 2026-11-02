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
    /// What FAR's voxelization takes a part to be: the meshes KSP finds as its model (FindModelComponents), FAR's
    /// GeometryPartModule bounds, and the projected areas FAR's aero module gives it.
    /// </summary>
    public static class PartGeomCommands
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static object Get(object o, string name)
        {
            for (Type t = o?.GetType(); t != null; t = t.BaseType)
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

        private static string Id(Part p) => p == null ? null : p.partInfo.name + "_" + p.craftID;

        [ServeCommand("partgeom", "match=TEXT: each matching editor part's model meshes as KSP finds them (owner part of each), " +
                                  "FAR's mesh bounds (part frame) and projected areas")]
        public static IEnumerator PartGeom(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            var ship = EditorLogic.fetch?.ship;
            string match = q["match"] ?? "";
            var rows = new List<object>();
            foreach (Part p in ship.parts.Where(p => Id(p).IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var meshes = p.FindModelComponents<MeshFilter>()
                              .Select(mf => mf.name + " <" + Id(mf.GetComponentInParent<Part>()) + ">").ToList();
                PartModule geo = p.Modules.Cast<PartModule>().FirstOrDefault(m => m.GetType().Name == "GeometryPartModule");
                PartModule aero = p.Modules.Cast<PartModule>().FirstOrDefault(m => m.GetType().Name == "FARAeroPartModule");
                object b = Get(geo, "overallMeshBounds");
                object areas = Get(aero, "ProjectedAreas");
                var a = new Dictionary<string, object>();
                if (areas != null)
                    foreach (string k in new[] {"iN", "iP", "jN", "jP", "kN", "kP", "totalArea"})
                        a[k] = Get(areas, k);
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = Id(p),
                    ["parent"] = Id(p.parent),
                    ["children"] = p.children.Select(Id).ToList(),
                    ["modelMeshes"] = meshes,
                    ["farBounds"] = b?.ToString(),
                    ["projectedAreas"] = a,
                });
            }

            answer["parts"] = rows;
            yield break;
        }
    }
}
