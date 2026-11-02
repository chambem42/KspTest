using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KspTest.ServePlugins
{
    /// <summary>FAR's editor-sim body (voxel section) drag at a skin friction coefficient of one's choosing: the editor
    /// sim fixes it at 0.005, flight computes it from the Reynolds number (about 0.0015 at Mach 2 high up).</summary>
    public static class FarSectionFrictionCommands
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static object F(object o, string name) => o?.GetType().GetField(name, Any)?.GetValue(o);

        [ServeCommand("farsectsf", "mach=2 [aoa=0] [sf=0.005,0]: FAR's editor-sim body drag, lift and pitching moment summed over " +
                                   "its aero sections (q = 1: m2, m2, m3) at each skin friction coefficient")]
        public static IEnumerator FarSectSf(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            yield return null;
            float mach = float.Parse(q["mach"] ?? "2", Inv);
            double aoa = double.Parse(q["aoa"] ?? "0", Inv) * Math.PI / 180;
            float[] sfs = (q["sf"] ?? "0.005,0").Split(',').Select(s => float.Parse(s, Inv)).ToArray();

            Type guiType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
            object gui = guiType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            var sections = F(Reflect.Get(gui, "_instantSim"), "_currentAeroSections") as IList;
            if (sections == null || sections.Count == 0)
            {
                answer["error"] = "no aero sections (no craft, or FAR still voxelizing)";
                yield break;
            }

            bool sph = EditorDriver.editorFacility == EditorFacility.SPH;
            Vector3d forward = sph ? Vector3.forward : Vector3.up;
            Vector3d up = sph ? Vector3.up : -Vector3.forward;
            Vector3d vel = (forward * Math.Cos(aoa) - up * Math.Sin(aoa)).normalized;
            Vector3d liftDown = (-forward * Math.Sin(aoa) - up * Math.Cos(aoa)).normalized;

            Type cqType = Reflect.FindType("ferram4.FARCenterQuery");
            // newer FAR adds an overload taking a per-part dictionary: the 7-argument one
            MethodInfo predict = sections[0].GetType().GetMethods(Any)
                                            .First(m => m.Name == "PredictionCalculateAeroForces" && m.GetParameters().Length == 7);
            var rows = new List<Dictionary<string, object>>();
            foreach (float sf in sfs)
            {
                double drag = 0, lift = 0;
                foreach (object sec in sections)
                {
                    object center = Activator.CreateInstance(cqType);
                    predict.Invoke(sec, new object[] { 2f, mach, 10000f, 0f, sf, (Vector3)vel, center });
                    var force = (Vector3d)F(center, "force") * 1000;
                    drag += -Vector3d.Dot(force, vel);
                    lift += -Vector3d.Dot(force, liftDown);
                }
                rows.Add(new Dictionary<string, object> { ["sf"] = sf, ["drag"] = Math.Round(drag, 4), ["lift"] = Math.Round(lift, 4) });
            }
            answer["sections"] = sections.Count;
            answer["rows (q = 1: m2)"] = rows;
        }

        [ServeCommand("farvesselsf", "mach=0.8: the active flight vessel's body skin friction factor (m2, times Cf for D/q) summed over " +
                                     "FAR's aero sections at the Mach, and how many sections")]
        public static IEnumerator FarVesselSf(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            yield return null;
            float mach = float.Parse(q["mach"] ?? "0.8", Inv);
            Vessel v = FlightGlobals.ActiveVessel ?? throw new InvalidOperationException("no vessel in flight");
            Type aeroType = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.FARVesselAero");
            object aero = v.GetComponent(aeroType);
            var sections = F(aero, "_currentAeroSections") as IList;
            if (sections == null)
            {
                answer["error"] = "no aero sections";
                yield break;
            }
            double sum = 0;
            foreach (object sec in sections)
            {
                object curve = F(sec, "xForceSkinFriction");
                sum += (double)curve.GetType().GetMethod("Evaluate", new[] { typeof(double) })
                                    .Invoke(curve, new object[] { (double)mach });
            }
            answer["sections"] = sections.Count;
            answer["skinFrictionM2"] = Math.Round(sum, 4);
        }

        [ServeCommand("farwetted", "the editor craft's voxel wetted area (the six side areas FAR's sections take skin friction on), " +
                                   "per part and summed for parts with and without a FAR wing module (m2)")]
        public static IEnumerator FarWetted(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            yield return null;
            Type guiType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
            object gui = guiType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            var xs = F(Reflect.Get(gui, "_vehicleAero"), "_vehicleCrossSection") as Array;
            var perPart = new Dictionary<Part, double>();
            foreach (object x in xs)
            {
                if (!(F(x, "partSideAreaValues") is IDictionary d)) continue;
                foreach (DictionaryEntry e in d)
                {
                    object a = e.Value;
                    double s = 0;
                    foreach (string k in new[] { "iN", "iP", "jN", "jP", "kN", "kP" }) s += Convert.ToDouble(F(a, k));
                    var p = (Part)e.Key;
                    perPart[p] = (perPart.TryGetValue(p, out double o) ? o : 0) + s;
                }
            }
            bool IsWing(Part p) => p.Modules.Contains("FARWingAerodynamicModel") || p.Modules.Contains("FARControllableSurface");
            answer["wingParts"] = Math.Round(perPart.Where(kv => IsWing(kv.Key)).Sum(kv => kv.Value), 1);
            answer["otherParts"] = Math.Round(perPart.Where(kv => !IsWing(kv.Key)).Sum(kv => kv.Value), 1);
            answer["parts"] = perPart.OrderByDescending(kv => kv.Value)
                                     .Select(kv => $"{ServeSession.Id(kv.Key)} {kv.Value:0.0}").ToList();
        }
    }
}
