using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using UnityEngine;

namespace KspTest
{
    /// <summary>Landing gear in the editor, for fitting leg lengths without shooting views.</summary>
    public static class GearCommands
    {
        [ServeCommand("gear", "each wheel part: origin, its lowest rendered point, the drop between, scale and strut extension; the ground the lowest wheel sets")]
        public static IEnumerator Gear(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            yield return null;   // a frame for a field just set to move the mesh
            var legs = new List<object>();
            float ground = float.MaxValue;
            foreach (Part p in EditorLogic.fetch.ship.parts)
            {
                var wheel = p.Modules.Cast<PartModule>().FirstOrDefault(m => m.moduleName == "KSPWheelBase" || m.moduleName == "ModuleWheelBase");
                if (wheel == null) continue;
                float low = float.MaxValue;
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>())
                    if (r.enabled && r.gameObject.activeInHierarchy && (r is MeshRenderer || r is SkinnedMeshRenderer))
                        low = Mathf.Min(low, r.bounds.min.y);
                ground = Mathf.Min(ground, low);
                var adj = p.Modules.Cast<PartModule>().FirstOrDefault(m => m.moduleName == "KSPWheelAdjustableGear");
                legs.Add(new Dictionary<string, object>
                {
                    ["id"] = ServeSession.Id(p),
                    ["origin"] = ServeSession.Vec(p.transform.position),
                    ["lowest"] = (double)low,
                    ["drop"] = (double)(p.transform.position.y - low),
                    ["scale"] = wheel.Fields["scale"]?.GetValue(wheel)?.ToString(),
                    ["strutExtension"] = adj?.Fields["strutExtension"]?.GetValue(adj)?.ToString(),
                });
            }
            answer["legs"] = legs;
            answer["ground"] = ground == float.MaxValue ? (object)null : (double)ground;
            // the lowest of everything else (an engine pod, the belly): its clearance over that ground
            string lowestPart = null;
            float lowestOther = float.MaxValue;
            foreach (Part p in EditorLogic.fetch.ship.parts)
            {
                if (p.Modules.Cast<PartModule>().Any(m => m.moduleName == "KSPWheelBase" || m.moduleName == "ModuleWheelBase")) continue;
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>())
                    if (r.enabled && r.gameObject.activeInHierarchy && (r is MeshRenderer || r is SkinnedMeshRenderer) && r.GetComponentInParent<Part>() == p && r.bounds.min.y < lowestOther)
                    {
                        lowestOther = r.bounds.min.y;
                        lowestPart = ServeSession.Id(p);
                    }
            }
            answer["lowestOther"] = lowestPart;
            answer["clearance"] = ground == float.MaxValue || lowestPart == null ? (object)null : (double)(lowestOther - ground);
            // the lowest few parts by their meshes' vertices (a renderer's bounds are an axis-aligned box, loose on a
            // long procedural mesh): each one's clearance over the ground
            var lows = new List<KeyValuePair<string, float>>();
            foreach (Part p in EditorLogic.fetch.ship.parts)
            {
                if (p.Modules.Cast<PartModule>().Any(m => m.moduleName == "KSPWheelBase" || m.moduleName == "ModuleWheelBase")) continue;
                float lowP = float.MaxValue;
                foreach (MeshFilter mf in p.GetComponentsInChildren<MeshFilter>())
                {
                    var r = mf.GetComponent<Renderer>();
                    if (r == null || !r.enabled || !mf.gameObject.activeInHierarchy || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    if (mf.GetComponentInParent<Part>() != p) continue;   // a child part's mesh is its own
                    foreach (Vector3 v in mf.sharedMesh.vertices)
                        lowP = Mathf.Min(lowP, mf.transform.TransformPoint(v).y);
                }
                if (lowP < float.MaxValue) lows.Add(new KeyValuePair<string, float>(ServeSession.Id(p), lowP));
            }
            answer["lowestParts"] = lows.OrderBy(kv => kv.Value).Take(6)
                .Select(kv => (object)new Dictionary<string, object> { ["id"] = kv.Key, ["clearance"] = ground == float.MaxValue ? (object)null : (double)(kv.Value - ground) })
                .ToList();
        }
    }
}
