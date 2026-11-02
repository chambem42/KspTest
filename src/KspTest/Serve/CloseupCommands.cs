using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace KspTest
{
    /// <summary>Close-ups of chosen parts in the editor, for checking joints and finish rather than the silhouette.</summary>
    public static class CloseupCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [ServeCommand("closeup", "match=TEXT[+TEXT...] (parts whose craft-file id contains any of them) view=side|front|top|bottom|rear|quarter|" +
                                 "quarterlow|DX,DY,DZ (looking along that direction; craft axes: x right, y up, z forward) [pad=1.15] [fov=25] [scenery=off] " +
                                 "[at=X,Y,Z (aim there, editor coordinates, instead of the parts' middle)] [radius=M (frame that much " +
                                 "around it instead of the parts' extent)] [name=X]: the parts framed from that side, the UI hidden, a screenshot; " +
                                 "the camera put back after")]
        public static IEnumerator Closeup(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string[] matches = (q["match"] ?? throw new ArgumentException("match= needed")).Split('+');
            var parts = EditorLogic.fetch.ship.parts.Where(p => matches.Any(m => ServeSession.Id(p).IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (parts.Count == 0) throw new ArgumentException("no part matches " + q["match"]);
            var b = new Bounds();
            bool any = false;
            foreach (Part p in parts)
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || !r.gameObject.activeInHierarchy || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                    if (r.GetComponentInParent<Part>() != p) continue;   // a child part's meshes are its own
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
            if (!any) throw new InvalidOperationException("the parts have no visible mesh");
            foreach (Part p in EditorLogic.fetch.ship.parts)
            {
                p.SetHighlightType(Part.HighlightType.Disabled);
                p.SetHighlight(false, false);
            }
            Vector3 look = Direction(q["view"] ?? "quarter");
            Vector3 up = Mathf.Abs(Vector3.Dot(look, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            float fov = float.Parse(q["fov"] ?? "25", Inv), pad = float.Parse(q["pad"] ?? "1.15", Inv);
            var saved = CameraState.Take();
            if (q["at"] != null)
            {
                var a = q["at"].Split(',').Select(x => float.Parse(x, Inv)).ToArray();
                b.center = new Vector3(a[0], a[1], a[2]);
            }
            float radius = q["radius"] != null ? float.Parse(q["radius"], Inv) : b.extents.magnitude * pad;
            float dist = radius / Mathf.Sin(fov / 2 * Mathf.Deg2Rad);
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (mb != null && (mb.GetType().Name == "SPHCamera" || mb.GetType().Name == "VABCamera"))
                    mb.enabled = false;
            var hidden = new List<Camera>();
            if (q["scenery"] == "off")
                foreach (var c in Camera.allCameras)
                    if (c != null && c.name == "sceneryCam") { c.enabled = false; hidden.Add(c); }
            foreach (var c in Camera.allCameras)
            {
                if (c == null || c.name.StartsWith("UI")) continue;
                if (c.transform.parent != null) c.transform.SetParent(null, true);
                c.fieldOfView = fov;
                c.transform.position = b.center - look * dist;
                c.transform.rotation = Quaternion.LookRotation(look, up);
                c.nearClipPlane = 0.3f;   // the rest of the craft can reach toward the camera past the parts framed
                c.farClipPlane = Mathf.Max(c.farClipPlane, dist + radius * 4 + 300);
            }
            GameEvents.onHideUI.Fire();
            var gui = ShotGui.HideOthers();
            try
            {
                yield return Wait.Seconds(0.5);
                int before = ctx.Screenshots.Count;
                yield return Snap.Shot(ctx, "closeup-" + (q["name"] ?? string.Join("_", matches).Replace('.', '-')));
                answer["files"] = ctx.Screenshots.Skip(before).Select(f => (object)Path.GetFullPath(Path.Combine(ctx.ResultsDir, f))).ToList();
            }
            finally
            {
                gui.Dispose();
                GameEvents.onShowUI.Fire();
                foreach (var c in hidden) c.enabled = true;
                saved.Restore();
            }
            answer["parts"] = parts.Select(p => (object)ServeSession.Id(p)).ToList();
            answer["center"] = ServeSession.Vec(b.center);
            answer["size"] = ServeSession.Vec(b.size);
        }

        // where the camera looks (craft axes in the SPH: x right, y up, z forward to the nose)
        private static Vector3 Direction(string view)
        {
            switch (view)
            {
                case "side": return Vector3.right;                       // from the left side
                case "right": return Vector3.left;
                case "front": return Vector3.back;                       // from ahead, looking aft
                case "rear": return Vector3.forward;
                case "top": return Vector3.down;
                case "bottom": return Vector3.up;
                case "quarter": return new Vector3(1, -0.6f, -1).normalized;     // from ahead, left and above
                case "quarterlow": return new Vector3(1, 0.4f, -1).normalized;   // from ahead, left and below
                case "quarterrear": return new Vector3(1, -0.6f, 1).normalized;
            }
            var v = view.Split(',').Select(x => float.Parse(x, Inv)).ToArray();
            return new Vector3(v[0], v[1], v[2]).normalized;
        }
    }
}
