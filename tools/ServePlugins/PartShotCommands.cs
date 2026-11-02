using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KspTest.ServePlugins
{
    /// <summary>A screenshot of the editor craft with where each part landed on it, for labelling the picture afterwards.</summary>
    public static class PartShotCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [ServeCommand("partshot", "view=top|DX,DY,DZ [radius=M] [at=X,Y,Z] [fov=25] [scenery=off] [name=X]: closeup's framing of the whole craft, " +
                                  "a screenshot, and each part's mesh center in it (pixels from the top left), mass, cost, and an intake's " +
                                  "area and scale")]
        public static IEnumerator PartShot(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            List<Part> parts = EditorLogic.fetch.ship.parts;
            var bounds = new Dictionary<Part, Bounds>();
            var all = new Bounds();
            bool any = false;
            foreach (Part p in parts)
            {
                bool has = false;
                var b = new Bounds();
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || !r.gameObject.activeInHierarchy || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                    if (r.GetComponentInParent<Part>() != p) continue;
                    if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
                }
                if (!has) continue;
                bounds[p] = b;
                if (!any) { all = b; any = true; } else all.Encapsulate(b);
            }
            foreach (Part p in parts)
            {
                p.SetHighlightType(Part.HighlightType.Disabled);
                p.SetHighlight(false, false);
            }
            Vector3 look = Direction(q["view"] ?? "top");
            Vector3 up = Mathf.Abs(Vector3.Dot(look, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            float fov = float.Parse(q["fov"] ?? "25", Inv);
            if (q["at"] != null)
            {
                var a = q["at"].Split(',').Select(x => float.Parse(x, Inv)).ToArray();
                all.center = new Vector3(a[0], a[1], a[2]);
            }
            float radius = q["radius"] != null ? float.Parse(q["radius"], Inv) : all.extents.magnitude * 1.05f;
            float dist = radius / Mathf.Sin(fov / 2 * Mathf.Deg2Rad);
            var saved = CameraState.Take();
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (mb != null && (mb.GetType().Name == "SPHCamera" || mb.GetType().Name == "VABCamera"))
                    mb.enabled = false;
            var hidden = new List<Camera>();
            if (q["scenery"] == "off")
                foreach (var c in Camera.allCameras)
                    if (c != null && c.name == "sceneryCam") { c.enabled = false; hidden.Add(c); }
            Camera main = null;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || c.name.StartsWith("UI")) continue;
                if (c.transform.parent != null) c.transform.SetParent(null, true);
                c.fieldOfView = fov;
                c.transform.position = all.center - look * dist;
                c.transform.rotation = Quaternion.LookRotation(look, up);
                c.nearClipPlane = 0.3f;
                c.farClipPlane = Mathf.Max(c.farClipPlane, dist + radius * 4 + 300);
                if (main == null || c.name == "Main Camera") main = c;
            }
            GameEvents.onHideUI.Fire();
            var gui = ShotGui.HideOthers();
            try
            {
                yield return Wait.Seconds(0.5);
                answer["screen"] = new List<object> { Screen.width, Screen.height };
                answer["camera"] = main?.name;
                var list = new List<object>();
                foreach (Part p in parts)
                {
                    var row = new Dictionary<string, object> { ["id"] = ServeSession.Id(p), ["name"] = p.partInfo.name, ["title"] = p.partInfo.title };
                    if (bounds.TryGetValue(p, out Bounds b) && main != null)
                    {
                        Vector3 s = main.WorldToScreenPoint(b.center);
                        row["px"] = new List<object> { Math.Round(s.x, 1), Math.Round(Screen.height - s.y, 1) };
                        // the mesh's extent on screen: the bounds' corners projected
                        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
                        for (int i = 0; i < 8; ++i)
                        {
                            Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                            Vector3 sc = main.WorldToScreenPoint(c);
                            x0 = Mathf.Min(x0, sc.x); x1 = Mathf.Max(x1, sc.x);
                            y0 = Mathf.Min(y0, Screen.height - sc.y); y1 = Mathf.Max(y1, Screen.height - sc.y);
                        }
                        row["box"] = new List<object> { Math.Round(x0, 1), Math.Round(y0, 1), Math.Round(x1, 1), Math.Round(y1, 1) };
                    }
                    float prefabMass = p.partInfo.partPrefab.mass;
                    row["massT"] = Math.Round(prefabMass + p.GetModuleMass(prefabMass, ModifierStagingSituation.CURRENT), 5);
                    row["cost"] = Math.Round(p.partInfo.cost + p.GetModuleCosts(p.partInfo.cost, ModifierStagingSituation.CURRENT), 2);
                    PartModule inlet = p.Modules.Cast<PartModule>().FirstOrDefault(m => m.GetType().Name == "AJEInlet");
                    if (inlet != null) row["areaM2"] = Math.Round(Num(inlet, "Area"), 5);
                    if (p.FindModuleImplementing<ModuleResourceIntake>() is ModuleResourceIntake mri) row["stockArea"] = Math.Round(mri.area, 6);
                    PartModule scaler = p.Modules.Cast<PartModule>().FirstOrDefault(m => m.moduleName == "ModuleROIntakeScale");
                    if (scaler != null)
                        row["scale"] = new List<object> { "scaleWidth", "scaleHeight", "scaleLength" }.Select(f => (object)Math.Round(scaler.Fields[(string)f].GetValue<float>(scaler), 4)).ToList();
                    list.Add(row);
                }
                answer["parts"] = list;
                int before = ctx.Screenshots.Count;
                yield return Snap.Shot(ctx, "partshot-" + (q["name"] ?? "craft"));
                answer["files"] = ctx.Screenshots.Skip(before).Select(f => (object)Path.GetFullPath(Path.Combine(ctx.ResultsDir, f))).ToList();
            }
            finally
            {
                gui.Dispose();
                GameEvents.onShowUI.Fire();
                foreach (var c in hidden) c.enabled = true;
                saved.Restore();
            }
        }

        private static double Num(object o, string field)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
                if (t.GetField(field, flags) is FieldInfo fi)
                    return Convert.ToDouble(fi.GetValue(o));
            return double.NaN;
        }

        private static Vector3 Direction(string view)
        {
            if (view == "top") return Vector3.down;
            var v = view.Split(',').Select(x => float.Parse(x, Inv)).ToArray();
            return new Vector3(v[0], v[1], v[2]).normalized;
        }
    }
}
