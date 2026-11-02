using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using UnityEngine;

namespace KspTest.AtmosphereAutopilot
{
    /// <summary>AtmosphereAutopilot's cruise engaged on the craft already flying (after `takeoff back=off`), no air start: does
    /// AA hold a craft it has flown from the ground where it loses one placed in the air?</summary>
    public static class AaFlyCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static object ActivateAa(Vessel v, string controller)
        {
            System.Reflection.Assembly asm = null;
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                if (a.name == "AtmosphereAutopilot") asm = a.assembly;
            if (asm == null) throw new InvalidOperationException("AtmosphereAutopilot not loaded");
            const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                                       System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            Type aaType = asm.GetType("AtmosphereAutopilot.AtmosphereAutopilot");
            object aa = aaType.GetProperty("Instance", Any).GetValue(null, null);
            var lists = (System.Collections.IDictionary)aaType.GetField("autopilot_module_lists", Any).GetValue(aa);
            var mods = (System.Collections.IDictionary)lists[v];
            object top = mods[asm.GetType("AtmosphereAutopilot.TopModuleManager")];
            return top.GetType().GetMethod("activateAutopilot", Any).Invoke(top, new object[] { asm.GetType("AtmosphereAutopilot." + controller) });
        }

        [ServeCommand("aafly", "in flight: AtmosphereAutopilot's cruise on the active vessel to alt=3000 at mach=0.5 (course: the current heading), " +
                               "full throttle under its speed control, for holdSec=180: a sample every sampleS=5 s [t, alt, Mach, speed, AoA, " +
                               "pitch input, vertical speed]; parts lost. hyperWarp=1. back=on returns to the SPH")]
        public static IEnumerator AaFly(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            double D(string k, double d) => q[k] != null ? double.Parse(q[k], Inv) : d;
            double alt = D("alt", 3000), mach = D("mach", 0.5), hold = D("holdSec", 180), sampleS = D("sampleS", 5), hyper = D("hyperWarp", 1);
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !HighLogic.LoadedSceneIsFlight) throw new InvalidOperationException("not in flight (run takeoff back=off first)");
            CelestialBody body = v.mainBody;
            double Sound(double a)
            {
                double pr = body.GetPressure(a);
                return body.GetSpeedOfSound(pr, body.GetDensity(pr, body.GetTemperature(a)));
            }
            Vector3d Up() => (v.CoMD - body.position).normalized;
            Vector3d north = Vector3d.Exclude(Up(), body.transform.up).normalized;
            Vector3d fwd = Vector3d.Exclude(Up(), (Vector3d)v.ReferenceTransform.up).normalized;
            double course = (Math.Atan2(Vector3d.Dot(fwd, Vector3d.Cross(Up(), north)), Vector3d.Dot(fwd, north)) * 180 / Math.PI + 360) % 360;
            Ctrl.Detach();
            Ctrl.SetThrottle(1f);
            Ctrl.RetractGear();
            // mode=fbw: AA's standard fly-by-wire with the stick centered (it holds the attitude's rates at zero) instead of the cruise
            bool fbw = string.Equals(q["mode"], "fbw", StringComparison.OrdinalIgnoreCase);
            if (fbw) ActivateAa(v, "StandardFlyByWire");
            else Aa.Cruise(v, alt, mach * Sound(alt), course);
            answer["mode"] = fbw ? "fly-by-wire, stick centered" : "cruise";
            answer["course"] = Math.Round(course, 1);
            if (hyper > 1) Flight.HyperWarp(hyper, 1.0);
            int parts = v.parts.Count;
            double t0 = Planetarium.GetUniversalTime(), next = t0;
            var samples = new List<object>();
            bool broken = false;
            try
            {
                while (Planetarium.GetUniversalTime() - t0 < hold)
                {
                    yield return null;
                    v = FlightGlobals.ActiveVessel;
                    if (v == null || v.parts.Count < parts) { broken = true; break; }
                    double t = Planetarium.GetUniversalTime();
                    if (t >= next)
                    {
                        next += sampleS;
                        samples.Add(new List<object> { Math.Round(t - t0, 1), Math.Round(v.altitude), Math.Round(v.srfSpeed / Sound(v.altitude), 3),
                                                       Math.Round(v.srfSpeed, 1), Math.Round(Flight.AoA, 2), Math.Round(v.ctrlState.pitch, 2),
                                                       Math.Round(v.verticalSpeed, 1), Math.Round(90 - Vector3.Angle(v.ReferenceTransform.right, Up()), 1),
                                                       Math.Round(v.ctrlState.roll, 2), Math.Round(v.ctrlState.yaw, 2),
                                                       Math.Round(90 - Vector3.Angle(v.ReferenceTransform.up, Up()), 1) });
                    }
                }
            }
            finally
            {
                if (hyper > 1) Flight.HyperWarpOff();
                if (FlightGlobals.ActiveVessel != null) Aa.Off(FlightGlobals.ActiveVessel);
            }
            answer["samples [t, alt, Mach, speed, AoA, pitch input, vs, bank (+ right wing up), roll input, yaw input, pitch attitude]"] = samples;
            answer["broken"] = broken ? $"{parts - (FlightGlobals.ActiveVessel?.parts.Count ?? 0)} parts lost" : null;
            if (string.Equals(q["back"], "on", StringComparison.OrdinalIgnoreCase))
            {
                yield return Nav.EnsureState(ctx, StartState.Sph, Nav.LoadedCraft);
                Ugui.DismissPopups();
            }
        }
    }
}
