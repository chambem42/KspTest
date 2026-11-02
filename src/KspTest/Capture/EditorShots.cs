using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspTest
{
    /// <summary>Three-view pictures of the editor craft, with what it takes to lay them over a drawing in FAR's frame.</summary>
    public static class EditorShots
    {
        /// <summary>
        /// The editor craft shot from the given views (plan, side, front) with a long lens, the UI hidden (other mods'
        /// windows too), no part highlighted; records under "&lt;label&gt;." the scale and the center of mass in each shot
        /// (plan: .topshot, else .&lt;view&gt;shot; screen coordinates from the top left, and where 1 m to the craft's
        /// right, ahead and up lands) and each wheel's drop below its origin (.gearN). noScenery: the hangar hidden. Leaves
        /// the editor's camera controllers off.
        /// </summary>
        public static IEnumerator Views(TestContext ctx, string label, string[] views, bool noScenery)
        {
            var inv = CultureInfo.InvariantCulture;
            var parts = EditorLogic.fetch.ship.parts;
            foreach (Part p in parts)
            {
                p.SetHighlightType(Part.HighlightType.Disabled);
                p.SetHighlight(false, false);
            }

            Bounds b = CraftBounds(parts);
            Vector3 nose = Vector3.ProjectOnPlane(EditorLogic.RootPart.transform.up, Vector3.up);
            if (nose.sqrMagnitude < 1e-4f) nose = Vector3.ProjectOnPlane(EditorLogic.RootPart.transform.forward, Vector3.up);
            nose.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, nose).normalized;
            float length = 2 * (Mathf.Abs(b.extents.x * nose.x) + Mathf.Abs(b.extents.z * nose.z));
            float span = 2 * (Mathf.Abs(b.extents.x * right.x) + Mathf.Abs(b.extents.z * right.z));

            // the center of mass, the origin of FAR's editor frame
            Vector3 com = Vector3.zero;
            float mass = 0;
            foreach (Part p in parts)
            {
                float m = p.mass + p.GetResourceMass();
                com += m * p.transform.TransformPoint(p.CoMOffset);
                mass += m;
            }
            com /= Mathf.Max(mass, 1e-6f);

            // each wheel part's lowest point below its origin, for fitting leg lengths (planecraft's KF gear)
            int gi = 0;
            foreach (Part p in parts)
            {
                if (!p.Modules.Cast<PartModule>().Any(m => m.moduleName == "KSPWheelBase" || m.moduleName == "ModuleWheelBase"))
                    continue;
                float low = float.MaxValue;
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>())
                    if (r.enabled && r.gameObject.activeInHierarchy && (r is MeshRenderer || r is SkinnedMeshRenderer))
                        low = Mathf.Min(low, r.bounds.min.y);
                Vector3 o = p.transform.position;
                ctx.Record(label + ".gear" + gi++, string.Format(inv, "{0} origin {1:F3} {2:F3} {3:F3} drop {4:F3}",
                    p.partInfo.name, o.x, o.y, o.z, o.y - low));
            }

            // the editor's camera controllers recompute the pose every frame: off for the rest of the session
            foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
                if (mb != null && (mb.GetType().Name == "SPHCamera" || mb.GetType().Name == "VABCamera"))
                    mb.enabled = false;
            Camera unity = EditorLogic.fetch.editorCamera ?? Camera.main;
            float height = 2 * b.extents.y;
            // noScenery: the hangar left out (a craft wider than the hangar has its walls over the wings)
            var hidden = new List<Camera>();
            if (noScenery)
                foreach (var c in Camera.allCameras)
                    if (c != null && c.name == "sceneryCam") { c.enabled = false; hidden.Add(c); }
            GameEvents.onHideUI.Fire();
            // other mods' windows ignore onHideUI: hidden while the views are shot
            var gui = ShotGui.HideOthers();
            // plan from above, nose up; side from the left, nose left, and front from ahead, the craft's right on the left of
            // the picture (as a three-view drawing has them)
            foreach (string view in views)
            {
                Vector3 look, up;
                float across, upward, depth;   // the craft's extent across and up the picture and along the view, m
                switch (view)
                {
                    case "side": look = right; up = Vector3.up; across = length; upward = height; depth = span; break;
                    case "front": look = -nose; up = Vector3.up; across = span; upward = height; depth = length; break;
                    default: look = Vector3.down; up = nose; across = span; upward = length; depth = height; break;
                }
                float size = 0.55f * Mathf.Max(upward, across / unity.aspect);   // half the height in view, m
                // the editor keeps its cameras perspective (an orthographic flag does not stick), so a long lens from
                // far off: 6 deg of view puts the camera 19 craft sizes away, near enough to a parallel view
                const float fov = 6;
                float dist = size / Mathf.Tan(fov / 2 * Mathf.Deg2Rad);
                Vector3 pos = b.center - look * dist;
                foreach (var c in Camera.allCameras)
                {
                    if (c == null || c.name.StartsWith("UI")) continue;
                    if (c.transform.parent != null) c.transform.SetParent(null, true);
                    c.fieldOfView = fov;
                    c.transform.position = pos;
                    c.transform.rotation = Quaternion.LookRotation(look, up);
                    // a near plane just short of the craft leaves out the hangar between it and the camera
                    c.nearClipPlane = Mathf.Max(1, dist - depth / 2 - 5);
                    c.farClipPlane = Mathf.Max(c.farClipPlane, dist + 300);
                }

                yield return Wait.Seconds(1);
                Vector3 s = unity.WorldToScreenPoint(com);
                Vector3 sr = unity.WorldToScreenPoint(com + right);
                Vector3 sn = unity.WorldToScreenPoint(com + nose);
                Vector3 su = unity.WorldToScreenPoint(com + Vector3.up);
                ctx.Record(label + (view == "plan" ? ".topshot" : "." + view + "shot"), string.Format(inv,
                    "screen {0}x{1} ppm {2:F3} com {3:F1} {4:F1} right {5:F2} {6:F2} nose {7:F2} {8:F2} up {9:F2} {10:F2} span {11:F2} length {12:F2} height {13:F2}",
                    Screen.width, Screen.height, Screen.height / (2 * size), s.x, Screen.height - s.y,
                    sr.x - s.x, s.y - sr.y, sn.x - s.x, s.y - sn.y, su.x - s.x, s.y - su.y, span, length, height));
                foreach (var c in Camera.allCameras)
                    if (c != null)
                        ctx.Record(label + "." + view + ".camera." + c.name, string.Format(inv, "enabled {0} fov {1:F1} pos {2} target {3} com {4}",
                            c.enabled, c.fieldOfView, c.transform.position, b.center, com));
                yield return Snap.Shot(ctx, $"{label}-{view}");
            }
            gui.Dispose();
            GameEvents.onShowUI.Fire();
            foreach (var c in hidden) c.enabled = true;
        }

        /// <summary>What the parts' enabled renderers cover, in world space.</summary>
        public static Bounds CraftBounds(List<Part> parts)
        {
            var b = new Bounds(parts[0].transform.position, Vector3.zero);
            foreach (Part p in parts)
                foreach (var r in p.GetComponentsInChildren<Renderer>())
                    if (r.enabled) b.Encapsulate(r.bounds);
            return b;
        }
    }
}
