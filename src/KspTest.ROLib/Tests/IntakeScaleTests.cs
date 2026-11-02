using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KspTest.Tests.ROLib
{
    /// <summary>
    /// A fixed-mesh intake scaled up (ModuleROIntakeScale, or ProceduralParts' ModuleIntakeScale before it) against the bigger
    /// intake it stands in for: the same craft with each, measured in the editor (AJEInlet area, part mass) and in an air start
    /// (AJEInlet's flight stats, FAR's drag on the vessel and on the intake), the pairs compared.
    /// </summary>
    public static class IntakeScaleTests
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Dorito's (crafts/Dorito.craft, a Mirage 2000 derivative) 1.25 m shock cone, then the 0.70 m one in its place scaled 1.786 (1.25 / 0.70) on all axes through its PAW:
        /// both measured (the stock drag cube too), every pair within tol, masses within massTol (RO's cone masses are hand-picked: 0.86 to 1.25 m is d^2,
        /// the 0.70 m is about 6% heavier than that). Vessel drag and the engine at full throttle; the intake's own drag at
        /// part throttle (intakeThrottle), where FAR's ram drag on it is not zero (it is above half throttle).
        /// Params: from, to, scale, point=ALT:MACH, aoa, tol, massTol (fractions), intakeThrottle.
        /// </summary>
        [UITest(Kind = "Check", Requires = "FAR,AJE,RealismOverhaul,KSPWheel", StartsAt = StartState.Sph, Craft = "Dorito", LaunchSite = "Runway", TimeoutSec = 600)]
        public static IEnumerator IntakeScale_ScaledConeMatchesBigger(TestContext ctx)
        {
            string[] pt = ctx.Param("point", "3000:0.5").Split(':');
            var o = new Options
            {
                From = ctx.Param("from", "RO-shockConeIntake125"), To = ctx.Param("to", "RO-shockConeIntakeBomarc"), Scale = ctx.ParamFloat("scale", 1.786f),
                AltM = double.Parse(pt[0], Inv), Mach = double.Parse(pt[1], Inv), AoADeg = ctx.ParamDouble("aoa", 2), Tol = ctx.ParamDouble("tol", 0.03),
                MassTol = ctx.ParamDouble("massTol", 0.07), IntakeThrottle = ctx.ParamDouble("intakeThrottle", 0.25),
            };
            string craft = ctx.Craft;
            var baseline = new Dictionary<string, object>();
            var scaled = new Dictionary<string, object>();

            ctx.Step($"Intake scale: {o.From} in the SPH");
            yield return InEditor(ctx, craft);
            EditorStats(EditorPart(o.From), baseline);
            Record(ctx, "baseline", baseline);
            yield return Fly(ctx, craft, o.From, o, baseline);
            Record(ctx, "baseline", baseline);

            ctx.Step($"Intake scale: {o.From} replaced by {o.To}");
            string variant = WriteVariant(craft, o.From, o.To);
            ctx.Record("variantCraft", variant);
            yield return InEditor(ctx, variant);
            Part intake = EditorPart(o.To);
            PartModule scaler = Scaler(intake) ?? throw new UITestFailure($"{o.To} has no scaling module (scaleWidth/scaleHeight/scaleLength)");
            ctx.Record("scaler", scaler.moduleName);
            ctx.Step($"Intake scale: {o.To} scaled to {o.Scale} through its PAW");
            foreach (string f in ScaleFields)
                yield return Paw.SetFloatEdit(intake, f, o.Scale);
            Paw.CloseAll();
            yield return Settled();
            yield return Wait.Until(() => EditorPart(o.To).GetComponent("DragCubeTool") == null, 30, "scaled drag cube rendered", shot: false);
            EditorStats(EditorPart(o.To), scaled);
            Record(ctx, "scaled", scaled);
            yield return Fly(ctx, variant, o.To, o, scaled);
            Record(ctx, "scaled", scaled);

            var rows = new List<object>();
            var failed = new List<string>();
            foreach (string key in baseline.Keys.Where(k => k.EndsWith("*")))
            {
                if (!(baseline[key] is double b) || !scaled.TryGetValue(key, out object so) || !(so is double s)) continue;
                double rel = Math.Abs(s - b) / Math.Max(Math.Abs(b), 1e-9);
                double tol = MassKeys.Contains(key) ? o.MassTol : o.Tol;
                bool ok = rel <= tol;
                rows.Add(new List<object> { key.TrimEnd('*'), Round(b), Round(s), Math.Round(rel, 4), tol, ok ? "ok" : "FAIL" });
                if (!ok) failed.Add($"{key.TrimEnd('*')} {Round(s)} vs {Round(b)} ({rel:P1})");
            }
            // scaled up, the smaller intake's node and so the part itself move out to where the bigger one sat
            if (baseline["editor.intakePosM"] is List<double> bp && scaled["editor.intakePosM"] is List<double> sp)
            {
                double moved = Math.Sqrt(bp.Zip(sp, (x, y) => (x - y) * (x - y)).Sum());
                ctx.Record("editor.intakePosDiffM", Round(moved));
                if (moved > 0.01) failed.Add($"scaled {o.To} sits {moved:0.###} m from where {o.From} was");
            }
            // the scaled intake flies with the cube of its scaled shape, in the editor and in flight
            if (scaled["editor.renderedCubeAreaM2*"] is double rc)
                foreach (string key in new[] { "editor.dragCubeAreaM2", "flight.dragCubeAreaM2" })
                {
                    double used = scaled.TryGetValue(key, out object u) && u is double d ? d : double.NaN;
                    double rel = Math.Abs(used - rc) / rc;
                    rows.Add(new List<object> { $"scaled {key} vs its render", Round(rc), Round(used), Math.Round(rel, 4), o.Tol, rel <= o.Tol ? "ok" : "FAIL" });
                    if (!(rel <= o.Tol)) failed.Add($"scaled {o.To}'s {key} {Round(used)} is not its scaled shape's {Round(rc)}");
                }
            foreach (var (label, run) in new[] { ("baseline", baseline), ("scaled", scaled) })
                if (!(run.TryGetValue("flight.thrustKN*", out object t) && t is double kn && kn > 0))
                    failed.Add($"{label}: no thrust in the air start, so the intake's airflow went unmeasured");
            ctx.Record("compare [what, baseline, scaled, relative difference, tolerance, ok]", rows);
            if (failed.Count > 0) throw new UITestFailure(string.Join("; ", failed));
        }

        private sealed class Options
        {
            public string From, To;
            public float Scale;
            public double AltM, Mach, AoADeg;
            /// <summary>Largest relative difference a pair may have; MassTol for the masses.</summary>
            public double Tol, MassTol;
            /// <summary>The throttle the intake's own drag is measured at.</summary>
            public double IntakeThrottle;
            /// <summary>Physics frames held before sampling, and sampled.</summary>
            public int SettleFrames = 25, Frames = 50;
        }

        private static readonly string[] ScaleFields = { "scaleWidth", "scaleHeight", "scaleLength" };
        private static readonly HashSet<string> MassKeys = new HashSet<string> { "editor.dryMassT*", "flight.intakeMassT*" };

        /// <summary>Keys ending in * are the compared ones.</summary>
        private static void Record(TestContext ctx, string prefix, Dictionary<string, object> run)
        {
            foreach (var kv in run) ctx.Values[prefix + "." + kv.Key.TrimEnd('*')] = kv.Value;
        }


        // ---- editor ----

        private static IEnumerator InEditor(TestContext ctx, string craft)
        {
            yield return Nav.EnsureState(ctx, StartState.Sph, craft);
            Ugui.DismissPopups();
            yield return Settled();
        }

        private static IEnumerator Settled()
        {
            yield return Wait.Frames(5);
            foreach (var ready in ServeSession.EditorReady)
                yield return Wait.Until(ready.Value, 60, ready.Key + " ready", shot: false);
        }

        private static Part EditorPart(string name) =>
            EditorLogic.fetch?.ship?.parts.FirstOrDefault(p => p.partInfo?.name == name)
            ?? throw new UITestFailure($"no {name} on the editor's craft");

        private static PartModule Scaler(Part p) =>
            p.Modules.Cast<PartModule>().FirstOrDefault(m => ScaleFields.All(f => m.Fields[f] != null));

        /// <summary>Keys ending in * are compared; the rest are for the record.</summary>
        private static void EditorStats(Part p, Dictionary<string, object> into)
        {
            float prefab = p.partInfo.partPrefab.mass;
            into["editor.areaM2*"] = Inlet(p) is PartModule inlet ? Num(inlet, "Area") : double.NaN;
            into["editor.dryMassT*"] = (double)(prefab + p.GetModuleMass(prefab, ModifierStagingSituation.CURRENT));
            into["editor.costFunds"] = Round(p.partInfo.cost + p.GetModuleCosts(p.partInfo.cost, ModifierStagingSituation.CURRENT));
            // The part's cube rendered now, as a scaled part's is, is what is compared: an unscaled part keeps its load-time
            // (PartDatabase) cube, and KSP renders that one with side faces about 30% smaller than it renders the same part
            // in the editor. The cube in use is for the record, and for a scaled part has to be its own render (not stock size).
            DragCube rendered = DragCubeSystem.Instance.RenderProceduralDragCube(p);
            into["editor.renderedCubeAreaM2*"] = rendered.Area.Sum(a => (double)a);
            into["editor.renderedCubeFacesM2"] = rendered.Area.Select(a => Round(a)).ToList();
            into["editor.dragCubeAreaM2"] = DragCubeArea(p);
            if (p.DragCubes.Cubes.Count > 0) into["editor.dragCubeFacesM2"] = p.DragCubes.Cubes[0].Area.Select(a => Round(a)).ToList();
            into["editor.shipMassT"] = Round(EditorLogic.fetch.ship.GetTotalMass());
            Vector3 pos = EditorLogic.RootPart.transform.InverseTransformPoint(p.transform.position);
            into["editor.intakePosM"] = new List<double> { Round(pos.x), Round(pos.y), Round(pos.z) };   // in the root part's frame
            if (Scaler(p) is PartModule s)
                into["editor.scale"] = string.Join(" x ", ScaleFields.Select(f => s.Fields[f].GetValue<float>(s).ToString("0.###", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// The stock drag cube's six face areas summed (m2): what stock aero, thermal exposure and occlusion see. FAR builds
        /// its drag from the meshes instead, so the flight drag says nothing about the cube.
        /// </summary>
        private static double DragCubeArea(Part p) =>
            p.DragCubes.Cubes.Count > 0 ? p.DragCubes.Cubes[0].Area.Sum(a => (double)a) : double.NaN;

        private static PartModule Inlet(Part p) =>
            p.Modules.Cast<PartModule>().FirstOrDefault(m => m.GetType().Name == "AJEInlet") ?? p.FindModuleImplementing<ModuleResourceIntake>();

        // ---- flight ----

        private static IEnumerator Fly(TestContext ctx, string craft, string intakeName, Options o, Dictionary<string, object> into)
        {
            ctx.Step($"Intake scale: {intakeName} air-started at {o.AltM / 1000:0.#} km, Mach {o.Mach}");
            yield return Nav.EnsureState(ctx, StartState.Flight, craft, "Runway");
            yield return Wait.Seconds(3);
            bool[] predictors = FarFlightProbe.QuietOtherPredictors();
            try
            {
                Vessel v = FlightGlobals.ActiveVessel ?? throw new UITestFailure("no vessel in flight");
                Ctrl.Sas(false);
                Ctrl.Stage();
                yield return Wait.Seconds(1);
                foreach (ModuleEngines e in Engines(FlightGlobals.ActiveVessel).Where(e => !e.EngineIgnited))
                {
                    Log.Info($"intake scale: {e.part.partInfo.title} not lit by staging; activating it");
                    e.Activate();
                }
                var c = new FarFlightProbe.Condition
                {
                    Name = "intake scale", AltM = o.AltM, Mach = o.Mach, AoADeg = o.AoADeg, Lat = 0, Lon = LevelDash.DaylightLon(v.mainBody, o.AltM),
                };
                // as LevelDash: the controls taken after the placement (which puts the vessel on rails and back)
                yield return FarFlightProbe.Place(c);
                Ctrl.Attach();
                Ctrl.Set(pitch: 0, yaw: 0, roll: 0, throttle: 1);
                yield return FarFlightProbe.SpoolAt(c, 1.0);
                into["flight.enginesSpooled"] = EngineStates(FlightGlobals.ActiveVessel);
                yield return FarFlightProbe.Place(c);
                Ctrl.Set(throttle: 1);
                FarFlightProbe.SetEngineThrottle(FlightGlobals.ActiveVessel, 1.0);   // the placement leaves them spooling up again

                var samples = new List<FarFlightProbe.Sample>();
                double cosine = 0, tpr = 0, areaRatio = 0, massFlow = 0, usable = 0, thrust = 0;
                int n = 0;
                for (int i = 0; i < o.Frames; ++i)
                {
                    int before = samples.Count;
                    yield return FarFlightProbe.Hold(c, i == 0 ? o.SettleFrames : 0, 1, samples, pin: true, predict: false);
                    if (samples.Count == before) continue;
                    v = FlightGlobals.ActiveVessel;
                    PartModule inlet = Inlet(FlightPart(v, intakeName));
                    cosine += Num(inlet, "cosine");
                    tpr += Num(inlet, "overallTPR");
                    foreach (ModuleEngines e in Engines(v).Where(e => HasField(e, "areaRatio")))
                    {
                        areaRatio += Num(e, "areaRatio");
                        massFlow += Num(e, "massFlowGui");
                    }
                    usable += Convert.ToDouble(Reflect.Call(inlet, "UsableArea"));
                    thrust += v.parts.SelectMany(x => x.Modules.OfType<ModuleEngines>()).Sum(e => e.finalThrust);
                    ++n;
                }
                if (n == 0) throw new UITestFailure("no flight samples (FAR not loaded?)");
                Part ip = FlightPart(FlightGlobals.ActiveVessel, intakeName);
                double q = samples.Average(s => s.QkPa), drag = samples.Average(s => s.Flight[0]);
                into["flight.mach"] = Round(samples.Average(s => s.Mach));
                into["flight.qKPa"] = Round(q);
                into["flight.aoaDeg"] = Round(samples.Average(s => s.AoADeg));
                into["flight.areaM2*"] = Num(Inlet(ip), "Area");
                into["flight.usableAreaM2*"] = usable / n;
                into["flight.cosine*"] = cosine / n;
                into["flight.overallTPR*"] = tpr / n;
                into["flight.engineAreaRatio*"] = areaRatio / n;   // SolverEngines: the intakes' area over what the engine needs
                into["flight.engineMassFlowKgS*"] = massFlow / n;
                into["flight.thrustKN*"] = thrust / n;
                into["flight.vesselDragKN"] = Round(drag);
                into["flight.vesselDragAreaM2*"] = drag / q;        // drag / q: what a slightly different speed leaves alone
                into["flight.intakeMassT*"] = (double)ip.mass;
                into["flight.dragCubeAreaM2"] = DragCubeArea(ip);
                into["flight.engines"] = EngineStates(FlightGlobals.ActiveVessel);

                // The intake's own drag: FAR's ram drag on it scales with (1 - 2 x throttle) and is zero above half throttle,
                // so it is measured throttled back, placed at the condition again on the same flight.
                ctx.Step($"Intake scale: {intakeName} drag at {o.IntakeThrottle:P0} throttle");
                yield return FarFlightProbe.Place(c);
                Ctrl.Set(throttle: (float)o.IntakeThrottle);
                FarFlightProbe.SetEngineThrottle(FlightGlobals.ActiveVessel, o.IntakeThrottle);
                var partSamples = new List<FarFlightProbe.Sample>();
                double intakeDrag = 0;
                int m = 0;
                for (int i = 0; i < o.Frames; ++i)
                {
                    int before = partSamples.Count;
                    yield return FarFlightProbe.Hold(c, i == 0 ? o.SettleFrames : 0, 1, partSamples, pin: true, predict: false);
                    if (partSamples.Count == before) continue;
                    v = FlightGlobals.ActiveVessel;
                    intakeDrag += PartDragKN(FlightPart(v, intakeName), v);
                    ++m;
                }
                if (m == 0) throw new UITestFailure($"no flight samples at {o.IntakeThrottle:P0} throttle");
                double qp = partSamples.Average(s => s.QkPa);
                into["flight.partThrottle.engineThrottle"] = Round(FarFlightProbe.EngineThrottleOf(FlightGlobals.ActiveVessel));
                into["flight.partThrottle.qKPa"] = Round(qp);
                into["flight.partThrottle.intakeDragKN"] = Round(intakeDrag / m);
                into["flight.partThrottle.intakeDragAreaM2*"] = intakeDrag / m / qp;
            }
            finally
            {
                Ctrl.Detach();
                FarFlightProbe.ResumeOtherPredictors(predictors);
            }
            ctx.Step($"Intake scale: back to the SPH");
            yield return InEditor(ctx, craft);
        }

        private static Part FlightPart(Vessel v, string name) =>
            v.parts.FirstOrDefault(x => x.partInfo.name == name) ?? throw new UITestFailure($"{name} gone in flight");

        private static IEnumerable<ModuleEngines> Engines(Vessel v) => v.parts.SelectMany(x => x.Modules.OfType<ModuleEngines>());

        private static List<string> EngineStates(Vessel v) =>
            Engines(v).Select(e => $"{e.part.partInfo.name}: ignited {e.EngineIgnited}, flameout {e.flameout}, status '{e.status}', " +
                                   $"throttle {e.currentThrottle:0.##} (asked {v.ctrlState.mainThrottle:0.##}), thrust {e.finalThrust:0.#} kN").ToList();

        /// <summary>FAR's force on the part along the airflow (kN; intake ram drag included), NaN without FAR.</summary>
        private static double PartDragKN(Part p, Vessel v)
        {
            PartModule far = p.Modules.Cast<PartModule>().FirstOrDefault(m => m.GetType().Name == "FARAeroPartModule");
            if (far == null) return double.NaN;
            Vector3 f = (Vector3)Reflect.Get(far, "worldSpaceAeroForce");
            return -Vector3.Dot(f, ((Vector3)v.srf_velocity).normalized);
        }

        // ---- the craft with the other intake ----

        /// <summary>
        /// The craft with every from part renamed to (same attachment and modules: the intakes share them), saved beside it.
        /// Each is placed where its own node meets its parent's, as the editor would attach it: the smaller intake's nodes
        /// sit closer in, and scaled up its node (and so the part) moves out to where the bigger one's was.
        /// </summary>
        private static string WriteVariant(string craft, string from, string to)
        {
            string text = File.ReadAllText(craft);
            var ids = Regex.Matches(text, @"^\s*part = " + Regex.Escape(from) + @"_(\d+)\s*$", RegexOptions.Multiline)
                           .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            if (ids.Count == 0) throw new UITestFailure($"no {from} in {Path.GetFileName(craft)}");
            foreach (string id in ids)
                text = Regex.Replace(text, @"(?<![\w-])" + Regex.Escape(from + "_" + id) + @"(?![0-9])", to + "_" + id);   // attN's "<part>_<id>_0|..." too
            string name = Path.GetFileNameWithoutExtension(craft) + " - intake scale";
            text = Regex.Replace(text, @"^ship = .*$", "ship = " + name, RegexOptions.Multiline);
            string path = Path.Combine(Path.GetDirectoryName(craft), name + ".craft");
            File.WriteAllText(path, text);

            Part fromPrefab = PartLoader.getPartInfoByName(from)?.partPrefab ?? throw new UITestFailure($"no part {from}");
            Part toPrefab = PartLoader.getPartInfoByName(to)?.partPrefab ?? throw new UITestFailure($"no part {to}");
            ConfigNode ship = ConfigNode.Load(path);
            ConfigNode[] parts = ship.GetNodes("PART");
            foreach (ConfigNode p in parts)
            {
                string part = p.GetValue("part");
                if (part == null || !part.StartsWith(to + "_")) continue;
                ConfigNode parent = parts.FirstOrDefault(q => q.GetValues("link").Contains(part));
                if (parent == null) continue;   // the root: nothing to meet
                // the node holding the parent: an attN naming it ("<node>,<parent>_<n>|..."); none for a surface attachment
                string parentName = parent.GetValue("part");
                string nodeId = p.GetValues("attN").Select(v => v.Split(',')).FirstOrDefault(a => a.Length > 1 && a[1].StartsWith(parentName + "_"))?[0];
                AttachNode fromNode = nodeId != null ? fromPrefab.FindAttachNode(nodeId) : fromPrefab.srfAttachNode;
                AttachNode toNode = nodeId != null ? toPrefab.FindAttachNode(nodeId) : toPrefab.srfAttachNode;
                if (fromNode == null || toNode == null) throw new UITestFailure($"{from} and {to} do not share the node '{nodeId ?? "srfAttach"}' {part} hangs on");
                Vector3 shift = KSPUtil.ParseQuaternion(p.GetValue("rot")) * (fromNode.originalPosition - toNode.originalPosition);
                p.SetValue("pos", KSPUtil.WriteVector(KSPUtil.ParseVector3(p.GetValue("pos")) + shift));
                p.SetValue("attPos0", KSPUtil.WriteVector(KSPUtil.ParseVector3(p.GetValue("attPos0")) +
                                                          Quaternion.Inverse(KSPUtil.ParseQuaternion(parent.GetValue("rot"))) * shift));
            }
            ship.Save(path);
            return path;
        }

        /// <summary>A numeric field by name, the most derived declaration first (AJEInlet's airFlow hides ModuleResourceIntake's).</summary>
        private static double Num(object o, string field)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
                if (t.GetField(field, flags) is FieldInfo fi)
                    return Convert.ToDouble(fi.GetValue(o));
            throw new UITestFailure($"{o.GetType().Name}.{field} not found");
        }

        private static bool HasField(object o, string field)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
                if (t.GetField(field, flags) != null) return true;
            return false;
        }

        private static double Round(double d) => Math.Round(d, 4);
    }
}
