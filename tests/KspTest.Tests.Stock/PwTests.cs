using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspTest;
using UnityEngine;

namespace KspTest.Tests.Stock
{
    /// <summary>
    /// The Stearwing A300 rebuilt on B9 procedural wings (Stearwing A300 PW, generated from the stock craft: each side's
    /// wing assembly replaced by one procedural wing of the same planform, engine and gear moved onto it).
    /// </summary>
    public static class PwTests
    {
        private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                                            System.Reflection.BindingFlags.NonPublic;

        private static PartModule Module(Part p, string name) => p.Modules.Cast<PartModule>().FirstOrDefault(m => m.moduleName == name);

        private static object Field(object o, string name) =>
            o?.GetType().GetField(name, Any)?.GetValue(o) ?? o?.GetType().GetProperty(name, Any)?.GetValue(o, null);

        private static Bounds CraftBounds(List<Part> parts)
        {
            var b = new Bounds(parts[0].transform.position, Vector3.zero);
            foreach (Part p in parts)
                foreach (var r in p.GetComponentsInChildren<Renderer>())
                    if (r.enabled) b.Encapsulate(r.bounds);
            return b;
        }

        private static bool IsCtrlSrf(Part p) => Field(Module(p, "WingProcedural"), "isCtrlSrf") is bool b && b;

        /// <summary>
        /// Opens B9's procedural-wing panel on a part, as pressing J over it does: the panel's target part, edit mode on,
        /// the window shown and sized.
        /// </summary>
        private static void OpenPanel(Part p)
        {
            PartModule wp = Module(p, "WingProcedural");
            Type t = wp.GetType();
            t.GetField("uiInstanceIDTarget", Any | System.Reflection.BindingFlags.Static).SetValue(null, p.GetInstanceID());
            t.GetField("uiWindowActive", Any | System.Reflection.BindingFlags.Static).SetValue(null, true);
            t.GetField("uiEditMode", Any | System.Reflection.BindingFlags.Static).SetValue(null, true);
            t.GetField("uiAdjustWindow", Any | System.Reflection.BindingFlags.Static).SetValue(null, true);
        }

        private static IEnumerator Shots(TestContext ctx, string name, string[] views, double distance)
        {
            Bounds b = CraftBounds(EditorLogic.fetch.ship.parts);
            foreach (string view in views)
            {
                Nav.EditorCamera(view.Trim(), b, 20, 0.9, 20, distance);
                yield return Wait.Seconds(1);
                yield return Snap.Shot(ctx, $"{name}-{view.Trim()}");
            }
        }

        /// <summary>
        /// Gives Stearwing A300 PW its control surfaces the way a player would, and saves it: the procedural wing's
        /// panel opened and "Split trailing edge into control surface" clicked (a control along the whole trailing edge,
        /// each side), the new control's panel opened and the control bisected at bisect of its span from the root
        /// (B9's BisectAtFraction; the inboard piece a flap, the outboard an aileron), then FAR's axes set: ailerons
        /// roll 100 only, flaps nothing, the FAT-455 tail fins pitch 100 only with 40 deg travel, the Big-S tail fin yaw
        /// 100 only. Writes the craft to the save's Ships/SPH and to the results as craftName (default
        /// "Stearwing A300 PW"). Params: bisect (0.6), craftName, distance (camera, m; default 95).
        /// </summary>
        [UITest("Pw_BuildStearwing", StartsAt = StartState.Sph, Craft = "Stearwing A300 PW base", CraftDir = "SPH", TimeoutSec = 600)]
        public static IEnumerator Pw_BuildStearwing(TestContext ctx)
        {
            var inv = CultureInfo.InvariantCulture;
            double bisect = ctx.ParamDouble("bisect", 0.6), distance = ctx.ParamDouble("distance", 95);
            string craftName = ctx.Param("craftName", "Stearwing A300 PW");
            string[] views = { "top", "quarter" };
            Ugui.DismissPopups();
            yield return Wait.Seconds(3);
            var ship = EditorLogic.fetch.ship;
            Vector3 right = ship.parts[0].transform.right;
            Vector3 centre = ship.parts[0].transform.position;
            double Span(Part p) => Vector3.Dot(p.transform.position - centre, right);

            ctx.Step("Split the trailing edge");
            Part wing = ship.parts.Where(p => Module(p, "WingProcedural") != null && !IsCtrlSrf(p)).OrderBy(Span).First();
            Assert.True(wing.symmetryCounterparts.Count == 1, $"the wing has its mirror ({wing.symmetryCounterparts.Count})");
            OpenPanel(wing);
            yield return Wait.Seconds(1);
            // The stock Stearwing's fuel is in its wings: the procedural wings get a liquid-fuel tank (the panel's tank
            // setup STR -> LF, both wings), filled below.
            yield return Imgui.Click("Next tank setup");
            yield return Wait.Seconds(1);
            ctx.Record("tankSetup", Field(Module(wing, "WingProcedural"), "fuelSelectedTankSetup"));
            yield return Imgui.Click("Split trailing edge into control surface");
            yield return Wait.Seconds(2);
            var ctrls = ship.parts.Where(IsCtrlSrf).ToList();
            ctx.Record("afterSplit.controls", ctrls.Count);
            Assert.True(ctrls.Count == 2, $"split made a control on each side ({ctrls.Count})");
            yield return Shots(ctx, "split", views, distance);

            ctx.Step("Bisect the control");
            Part ctrl = ctrls.First(c => c.parent == wing);
            OpenPanel(ctrl);
            yield return Wait.Seconds(1);
            PartModule ctrlWp = Module(ctrl, "WingProcedural");
            ctx.Record("panel", string.Format(inv, "target {0}, control {1}, wing {2}; windows: {3}",
                ctrlWp.GetType().GetField("uiInstanceIDTarget", Any | System.Reflection.BindingFlags.Static).GetValue(null), ctrl.GetInstanceID(), wing.GetInstanceID(),
                string.Join(" | ", Imgui.VisibleWindows().Select(w => w.Title))));
            yield return Snap.Shot(ctx, "control-panel");
            PartModule cm = Module(ctrl, "WingProcedural");
            var api = cm.GetType().GetMethod("BisectAtFraction", Any);
            Assert.True(api != null, "B9 has BisectAtFraction");
            api.Invoke(cm, new object[] { (float)bisect });
            yield return Wait.Seconds(2);
            ctrls = ship.parts.Where(IsCtrlSrf).ToList();
            ctx.Record("afterBisect.controls", ctrls.Count);
            Assert.True(ctrls.Count == 4, $"bisect made an outboard control on each side ({ctrls.Count})");
            foreach (Part c in ctrls)
                ctx.Record($"control.{c.flightID % 10000}", string.Format(inv, "span {0:+0.00;-0.00} m, length {1}", Span(c), Field(Module(c, "WingProcedural"), "sharedBaseLength")));

            ctx.Step("FAR control axes");
            double outer = ctrls.Max(c => Math.Abs(Span(c)));
            var roles = new List<string>();
            var roleOf = new Dictionary<Part, string>();
            foreach (Part p in ship.parts)
            {
                PartModule far = FarApi.ControlSurface(p);
                if (far == null) continue;
                string role;
                if (IsCtrlSrf(p) && Math.Abs(Span(p)) > outer - 0.5) { FarApi.SetControlAxes(far, 0, 0, 100); role = "aileron"; }
                else if (IsCtrlSrf(p)) { FarApi.SetControlAxes(far, 0, 0, 0); role = "flap"; }
                else if (p.partInfo.name == "airlinerTailFin") { FarApi.SetControlAxes(far, 100, 0, 0, 40); role = "elevator"; }
                else if (p.partInfo.name == "wingShuttleRudder") { FarApi.SetControlAxes(far, 0, 100, 0); role = "rudder"; }
                else { FarApi.SetControlAxes(far, 0, 0, 0); role = "off"; }
                roleOf[p] = role;
                roles.Add(string.Format(inv, "{0} {1:+0.0;-0.0} m {2}", p.partInfo.name, Span(p), role));
            }
            ctx.Record("surfaces", string.Join(", ", roles));
            // Outlined by role for the pictures: ailerons blue, flaps green, elevators orange, rudder magenta.
            var colours = new Dictionary<string, Color> { { "aileron", Color.blue }, { "flap", Color.green }, { "elevator", new Color(1f, 0.5f, 0f) }, { "rudder", Color.magenta } };
            var lit = new List<Part>();
            foreach (var kv in roleOf)
            {
                Part p = kv.Key;
                if (!colours.TryGetValue(kv.Value, out Color col)) continue;
                p.SetHighlightType(Part.HighlightType.AlwaysOn);
                p.SetHighlightColor(col);
                p.SetHighlight(true, false);
                lit.Add(p);
            }
            yield return Shots(ctx, "built", views, distance);
            foreach (Part p in lit)
            {
                p.SetHighlight(false, false);
                p.SetHighlightType(Part.HighlightType.OnMouseOver);
                p.SetHighlightDefault();
            }

            // The stock Stearwing carries 900 units of liquid fuel a side in its wings: fill the procedural wings'
            // tanks to that (fuelPerWing), or to what they hold if less.
            double perWing = ctx.ParamDouble("fuelPerWing", 900);
            foreach (Part p in ship.parts.Where(p => Module(p, "WingProcedural") != null && !IsCtrlSrf(p)))
            {
                PartResource lf = p.Resources.Get("LiquidFuel");
                if (lf == null) { ctx.Record("fuel." + Span(p).ToString("+0;-0", inv), "no liquid fuel tank"); continue; }
                double capacity = lf.maxAmount;
                lf.amount = Math.Min(perWing, capacity);
                ctx.Record("fuel." + Span(p).ToString("+0;-0", inv), string.Format(inv, "{0:F0} of {1:F0} units", lf.amount, capacity));
            }
            ctx.Record("mass", string.Format(inv, "{0:F2} t", ship.GetTotalMass()));

            ctx.Step("Save " + craftName);
            ship.shipName = craftName;
            ConfigNode node = ship.SaveShip();
            string path = ctx.CraftPath(craftName, "SPH");
            node.Save(path);
            node.Save(System.IO.Path.Combine(ctx.ResultsDir, craftName + ".craft"));
            ctx.Record("saved", path);
        }

        /// <summary>
        /// Screenshots of the stock Stearwing and of Stearwing A300 PW in the SPH from above, three-quarter and head on,
        /// and the procedural wings' shape fields and FAR's wing figures for them. Params: views ("+"-separated, default
        /// top+quarter+nose), crafts ("+"-separated, default both).
        /// </summary>
        [UITest("Pw_StearwingShots", StartsAt = StartState.Sph, Craft = "Stearwing A300 PW base", CraftDir = "SPH", TimeoutSec = 600)]
        public static IEnumerator Pw_StearwingShots(TestContext ctx)
        {
            var inv = CultureInfo.InvariantCulture;
            string[] views = (ctx.Param("views") ?? "top+quarter+nose").Split('+');
            string[] crafts = (ctx.Param("crafts") ?? "Stearwing A300 PW+Stearwing A300").Split('+');
            foreach (string craft in crafts)
            {
                ctx.Step("Craft " + craft);
                yield return Nav.EnsureState(ctx, StartState.Sph, ctx.CraftPath(craft, "SPH"));
                Ugui.DismissPopups();
                yield return Wait.Seconds(5);
                var parts = EditorLogic.fetch.ship.parts;
                ctx.Record(craft + ".parts", parts.Count);
                foreach (Part p in parts)
                {
                    PartModule wp = Module(p, "WingProcedural");
                    if (wp == null) continue;
                    string[] fields = { "sharedBaseLength", "sharedBaseWidthRoot", "sharedBaseWidthTip", "sharedBaseOffsetRoot", "sharedBaseOffsetTip",
                                        "sharedEdgeWidthLeadingRoot", "sharedEdgeWidthTrailingRoot", "sharedEdgeWidthTrailingTip", "isCtrlSrf" };
                    PartModule far = Module(p, "FARWingAerodynamicModel") ?? Module(p, "FARControllableSurface");
                    string farStats = far == null ? "no FAR wing" : string.Format(inv, "FAR b_2 {0}, MAC {1}, S {2}, taper {3}, sweep {4}",
                        Field(far, "b_2"), Field(far, "MAC"), Field(far, "S"), Field(far, "TaperRatio"), Field(far, "MidChordSweep"));
                    ctx.Record($"{craft}.{p.partInfo.name}.{p.flightID % 10000}",
                        string.Join(", ", fields.Select(f => f + " " + Convert.ToString(Field(wp, f), inv))) + "; " + farStats);
                }
                Bounds b = CraftBounds(parts);
                ctx.Record(craft + ".bounds", string.Format(inv, "{0:F2} x {1:F2} x {2:F2} m", b.size.x, b.size.y, b.size.z));
                foreach (string view in views)
                {
                    Nav.EditorCamera(view.Trim(), b, 20, 0.9, 20, ctx.ParamDouble("distance", 95));
                    yield return Wait.Seconds(1);
                    yield return Snap.Shot(ctx, $"{craft}-{view.Trim()}");
                }
            }
        }
    }
}
