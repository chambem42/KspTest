using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using UnityEngine;

namespace KspTest
{
    /// <summary>The install's loaded parts, searched: for picking donors without opening the editor's part list.</summary>
    public static class PartCatalog
    {
        [ServeCommand("catalog", "match=TEXT [module=NAME]: loaded parts whose name or title contains TEXT (and that carry the module): " +
                                 "name, title, mass, its model's size (m, from the prefab's renderers), modules, and an engine's thrust and configs")]
        public static IEnumerator Catalog(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string match = q["match"] ?? "";
            string module = q["module"];
            var list = new List<object>();
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
            {
                if (ap?.partPrefab == null) continue;
                if (match.Length > 0 && ap.name.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (ap.title ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var mods = ap.partPrefab.Modules.Cast<PartModule>().ToList();
                if (module != null && !mods.Any(m => m.moduleName.IndexOf(module, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                var b = new Bounds();
                bool any = false;
                foreach (Renderer r in ap.partPrefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                var e = new Dictionary<string, object>
                {
                    ["name"] = ap.name,
                    ["title"] = ap.title,
                    ["massT"] = (double)ap.partPrefab.mass,
                    ["size"] = any ? ServeSession.Vec(b.size) : null,
                    ["modules"] = mods.Select(m => (object)m.moduleName).Distinct().ToList(),
                };
                var eng = mods.OfType<ModuleEngines>().FirstOrDefault();
                if (eng != null) e["maxThrustKN"] = (double)eng.maxThrust;
                var cfg = mods.FirstOrDefault(m => m.moduleName == "ModuleEngineConfigs");
                if (cfg != null)
                {
                    var f = cfg.GetType().GetField("configs", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (f?.GetValue(cfg) is IEnumerable configs)
                        e["configs"] = configs.Cast<ConfigNode>().Select(n => (object)n.GetValue("name")).ToList();
                }
                list.Add(e);
                if (list.Count >= 200) break;
            }
            answer["parts"] = list;
            yield break;
        }
    }
}
