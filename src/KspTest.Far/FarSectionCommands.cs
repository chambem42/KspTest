using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KspTest
{
    /// <summary>FAR's body (voxel cross-section) forces one aero section at a time, as the editor sim computes them: where
    /// each section sits, its axis, the parts it pushes on and its drag, lift and pitching moment.</summary>
    public static class FarSectionCommands
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static object F(object o, string name) => o?.GetType().GetField(name, Any)?.GetValue(o);

        [ServeCommand("farsections", "mach=0.8 [aoa=0] [top=25] [sf=0.005]: FAR's editor-sim body forces per aero section (editor craft): s (m aft of " +
                                     "the nose), the section axis' tilt from the vehicle axis (deg, + nose up), potential-flow and crossflow " +
                                     "factors, flatness, the parts and their shares, and drag / lift / pitching moment (coefficients on q = 1, m2 and m3); " +
                                     "totals, and the sections sorted by drag")]
        public static IEnumerator FarSections(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            yield return null;
            double D(string k, double d) => q[k] == null ? d : double.Parse(q[k], Inv);
            float mach = (float)D("mach", 0.8);
            double aoa = D("aoa", 0) * Math.PI / 180;
            int top = (int)D("top", 25);
            // the skin friction coefficient: the editor sim's 0.005 unless given (flight computes it from Reynolds number)
            float sf = (float)D("sf", 0.005);

            Type guiType = Reflect.FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
            object gui = guiType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            object sim = Reflect.Get(gui, "_instantSim");
            var sections = F(sim, "_currentAeroSections") as IList;
            if (sections == null || sections.Count == 0)
            {
                answer["error"] = "no aero sections (no craft, or FAR still voxelizing)";
                yield break;
            }

            // the editor sim's axes: world +z forward and +y up in the SPH; in the VAB the nose is +y and up is -z
            bool sph = EditorDriver.editorFacility == EditorFacility.SPH;
            Vector3d forward = sph ? Vector3.forward : Vector3.up;
            Vector3d up = sph ? Vector3.up : -Vector3.forward;
            Vector3d vel = (forward * Math.Cos(aoa) - up * Math.Sin(aoa)).normalized;
            Vector3d liftDown = (-forward * Math.Sin(aoa) - up * Math.Cos(aoa)).normalized;
            Vector3d sideways = Vector3d.Cross(vel, liftDown).normalized;

            // the nose: the most forward part point along the axis
            List<Part> ship = EditorLogic.fetch.ship.parts;
            double nose = ship.Max(p => Vector3d.Dot(p.transform.position, forward));
            Vector3d com = Vector3d.zero; double mass = 0;
            foreach (Part p in ship)
            {
                double m = p.mass + p.GetResourceMass();
                com += m * (Vector3d)p.transform.TransformPoint(p.CoMOffset);
                mass += m;
            }
            com /= mass;

            Type cqType = Reflect.FindType("ferram4.FARCenterQuery");
            // newer FAR adds an overload taking a per-part dictionary: the 7-argument one
            MethodInfo predict = sections[0].GetType().GetMethods(Any)
                                            .First(m => m.Name == "PredictionCalculateAeroForces" && m.GetParameters().Length == 7);
            var rows = new List<Dictionary<string, object>>();
            double sumD = 0, sumL = 0, sumM = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                object sec = sections[i];
                object center = Activator.CreateInstance(cqType);
                predict.Invoke(sec, new object[] { 2f, mach, 10000f, 0f, sf, (Vector3)vel, center });
                var force = (Vector3d)F(center, "force") * 1000;
                var torque = (Vector3d)F(center, "torque") * 1000;
                // moment about the CoM: torque about the origin minus r_CoM x F
                Vector3d mCom = -(torque - Vector3d.Cross(com, force));
                double drag = -Vector3d.Dot(force, vel), lift = -Vector3d.Dot(force, liftDown), pm = Vector3d.Dot(mCom, sideways);
                sumD += drag; sumL += lift; sumM += pm;

                // where it sits and its axis, from its part data (part space -> world)
                var pd = F(sec, "partData") as IList;
                Vector3d pos = Vector3d.zero, xr = Vector3d.zero; double wsum = 0;
                var shares = new List<string>();
                foreach (object d in pd)
                {
                    var mod = F(d, "aeroModule") as PartModule;
                    if (mod == null) continue;
                    float w = (float)F(d, "dragFactor");
                    Transform t = mod.part.partTransform;
                    pos += w * (Vector3d)t.TransformPoint((Vector3)F(d, "centroidPartSpace"));
                    xr += w * (Vector3d)t.TransformDirection((Vector3)F(d, "xRefVectorPartSpace"));
                    wsum += w;
                    shares.Add($"{ServeSession.Id(mod.part)}:{w:0.00}");
                }
                if (wsum > 0) { pos /= wsum; xr = xr.normalized; }
                double s = nose - Vector3d.Dot(pos, forward);
                double tilt = Math.Atan2(Vector3d.Dot(xr, up), Vector3d.Dot(xr, forward)) * 180 / Math.PI;
                rows.Add(new Dictionary<string, object>
                {
                    ["i"] = i, ["s"] = Math.Round(s, 2), ["tiltDeg"] = Math.Round(tilt, 2),
                    ["pfn"] = Math.Round(Convert.ToDouble(F(sec, "potentialFlowNormalForce")), 3),
                    ["xflow"] = Math.Round(Convert.ToDouble(F(sec, "viscCrossflowDrag")), 3),
                    ["flat"] = Math.Round(Convert.ToDouble(F(sec, "flatnessRatio")), 2),
                    ["merged"] = Math.Round(Convert.ToDouble(F(sec, "mergeFactor")), 0),
                    ["drag"] = Math.Round(drag, 4), ["lift"] = Math.Round(lift, 4), ["pitchM"] = Math.Round(pm, 3),
                    ["parts"] = string.Join(" ", shares),
                });
            }
            answer["sections"] = rows.Count;
            answer["totals (m2, m2, m3 at q = 1)"] = new Dictionary<string, object> { ["drag"] = Math.Round(sumD, 3), ["lift"] = Math.Round(sumL, 3), ["pitchM"] = Math.Round(sumM, 2) };
            answer["byDrag"] = rows.OrderByDescending(r => Math.Abs((double)r["drag"])).Take(top).ToList();
            answer["all"] = rows.Select(r => new List<object> { r["i"], r["s"], r["tiltDeg"], r["drag"], r["lift"], r["pitchM"], r["flat"] }).ToList();
        }
    }
}
