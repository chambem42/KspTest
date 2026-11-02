using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using UnityEngine;

namespace KspTest
{
    /// <summary>How far the editor's craft can pitch up on its main wheels before something else touches the ground.</summary>
    public static class PitchLimitCommands
    {
        [ServeCommand("pitchlimit", "the main wheels' contact (the aft-most wheels' lowest point), the pitch-up angle about it at which each other " +
                                    "part's mesh first reaches the ground (the lowest few), and the center of mass's distance ahead of the contact")]
        public static IEnumerator PitchLimit(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            yield return null;
            var ship = EditorLogic.fetch.ship;
            bool IsWheel(Part p) => p.Modules.Cast<PartModule>().Any(m => m.moduleName == "KSPWheelBase" || m.moduleName == "ModuleWheelBase");
            IEnumerable<Vector3> Verts(Part p)
            {
                foreach (MeshFilter mf in p.GetComponentsInChildren<MeshFilter>())
                {
                    var r = mf.GetComponent<Renderer>();
                    if (r == null || !r.enabled || !mf.gameObject.activeInHierarchy || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    if (mf.GetComponentInParent<Part>() != p) continue;
                    foreach (Vector3 v in mf.sharedMesh.vertices) yield return mf.transform.TransformPoint(v);
                }
            }
            // the wheels' contacts: each wheel part's lowest vertex; the mains are those aft of the craft's middle
            var contacts = ship.parts.Where(IsWheel).Select(p => (p, low: Verts(p).OrderBy(v => v.y).FirstOrDefault())).ToList();
            float ground = contacts.Min(c => c.low.y);
            var mains = contacts.Where(c => c.low.z < contacts.Average(k => k.low.z)).ToList();
            if (mains.Count == 0) mains = contacts;
            float zc = mains.Average(c => c.low.z), yc = mains.Average(c => c.low.y);
            var limits = new List<KeyValuePair<string, float>>();
            foreach (Part p in ship.parts)
            {
                if (IsWheel(p)) continue;
                float best = 90f;
                foreach (Vector3 v in Verts(p))
                {
                    float dz = zc - v.z;   // aft of the contact
                    if (dz <= 0.05f) continue;
                    best = Mathf.Min(best, Mathf.Atan2(v.y - yc, dz) * Mathf.Rad2Deg);
                }
                if (best < 90f) limits.Add(new KeyValuePair<string, float>(ServeSession.Id(p), best));
            }
            Vector3 com = Vector3.zero;
            float m = 0;
            foreach (Part p in ship.parts)
            {
                float pm = p.mass + p.GetResourceMass();
                com += p.transform.TransformPoint(p.CoMOffset) * pm;
                m += pm;
            }
            com /= m;
            answer["mainContact"] = new List<object> { (double)zc, (double)yc };
            answer["groundSpread"] = (double)(contacts.Max(c => c.low.y) - ground);
            answer["comAheadOfMains"] = (double)(com.z - zc);
            answer["comHeight"] = (double)(com.y - yc);
            answer["limits"] = limits.OrderBy(kv => kv.Value).Take(6)
                .Select(kv => (object)new Dictionary<string, object> { ["id"] = kv.Key, ["deg"] = (double)kv.Value }).ToList();
        }
    }
}
