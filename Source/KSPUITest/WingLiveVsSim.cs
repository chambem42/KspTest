using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// Each FAR wing as the flight computed it (captured right after its own FixedUpdate, before anything else can
    /// overwrite its fields) against FAR's simulation of the vessel at the same state (FARAPI.CalculateVesselAeroForces
    /// at the surface velocity and altitude, the surfaces as they are): lift and drag coefficients, aerodynamic centre
    /// and pitching moment about the CoM. Main thread.
    /// </summary>
    public static class WingLiveVsSim
    {
        private struct State
        {
            internal double Cl, Cd, Stall;
            internal Vector3d Ac, LiftDir;
            internal Vector3 Force;
        }

        private static Harmony harmony;
        private static readonly Dictionary<PartModule, State> live = new Dictionary<PartModule, State>();
        private static FieldInfo fCl, fCd, fStall, fAc, fForce, fLiftDir;

        /// <summary>Starts capturing every wing's flight state after each of its FixedUpdates.</summary>
        public static bool Arm()
        {
            if (harmony != null)
                return true;
            Type t = AccessTools.TypeByName("ferram4.FARWingAerodynamicModel");
            MethodInfo fixedUpdate = t == null ? null : AccessTools.DeclaredMethod(t, "FixedUpdate");
            if (fixedUpdate == null)
                return false;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            fCl = t.GetField("Cl", any);
            fCd = t.GetField("Cd", any);
            fStall = t.GetField("stall", any);
            fAc = t.GetField("AerodynamicCenter", any);
            fForce = t.GetField("worldSpaceForce", any);
            fLiftDir = t.GetField("liftDirection", any);
            harmony = new Harmony("KSPUITest.WingLiveVsSim");
            harmony.Patch(fixedUpdate, postfix: new HarmonyMethod(typeof(WingLiveVsSim), nameof(After)));
            return true;
        }

        private static void After(PartModule __instance)
        {
            if (__instance == null || __instance.vessel != FlightGlobals.ActiveVessel)
                return;
            live[__instance] = Read(__instance);
        }

        private static State Read(PartModule m) => new State
        {
            Cl = Convert.ToDouble(fCl?.GetValue(m) ?? double.NaN),
            Cd = Convert.ToDouble(fCd?.GetValue(m) ?? double.NaN),
            Stall = Convert.ToDouble(fStall?.GetValue(m) ?? double.NaN),
            Ac = fAc != null ? (Vector3d)fAc.GetValue(m) : Vector3d.zero,
            LiftDir = fLiftDir != null ? (Vector3d)fLiftDir.GetValue(m) : Vector3d.zero,
            Force = fForce != null ? (Vector3)fForce.GetValue(m) : Vector3.zero,
        };

        /// <summary>
        /// Every FAR control surface: whether it has its own rigidbody and its mass (t) and largest principal inertia
        /// (t m2); the pitching couple FAR puts on it (its force at its aerodynamic centre about its own rigidbody's centre
        /// of mass, kN m about the vessel's pitch-up axis); what its attach joints pass to the parent (Joint.currentTorque
        /// and currentForce summed, the torque about the pitch-up axis, kN m, and the force, kN); how far it has turned on
        /// them from where it sat at load (deg) and its spin relative to the root (deg/s); and the joints' angular drive
        /// spring and the count. One line per surface.
        /// </summary>
        public static string SurfaceJoints(Vessel v)
        {
            if (v == null || v.rootPart == null)
                return "no vessel";
            var inv = CultureInfo.InvariantCulture;
            Vector3 axis = FarApi.PitchUpAxis(v);
            Quaternion rootRot = v.rootPart.transform.rotation;
            Vector3 rootSpin = v.rootPart.rb != null ? v.rootPart.rb.angularVelocity : Vector3.zero;
            var sb = new StringBuilder();
            foreach (Part p in v.parts)
            {
                PartModule m = p.Modules.Cast<PartModule>().FirstOrDefault(x => x.GetType().Name == "FARControllableSurface");
                if (m == null)
                    continue;
                live.TryGetValue(m, out State st);
                Rigidbody rb = p.rb;
                Vector3d cm = rb != null ? (Vector3d)rb.worldCenterOfMass : (Vector3d)p.transform.position;
                double couple = Vector3d.Dot(Vector3d.Cross(st.Ac - cm, (Vector3d)st.Force), (Vector3d)axis);
                double jointTorque = double.NaN, jointForce = double.NaN, spring = double.NaN;
                int count = 0;
                if (p.attachJoint != null && p.attachJoint.joints != null)
                {
                    Vector3 t = Vector3.zero, f = Vector3.zero;
                    foreach (ConfigurableJoint j in p.attachJoint.joints)
                    {
                        if (j == null) continue;
                        t += j.currentTorque;
                        f += j.currentForce;
                        spring = j.angularXDrive.positionSpring;
                        ++count;
                    }
                    jointTorque = Vector3.Dot(t, axis);   // t and kN, so kN m
                    jointForce = f.magnitude;
                }
                // every joint on the vessel that touches this part's rigidbody (its attach joint, KJR's extra ones), what
                // they pass to the rest of the vessel about its CoM, and how they are held
                double allMoment = 0, allForce = 0;
                int all = 0;
                string proj = "";
                if (rb != null)
                {
                    Vector3d com = v.CoMD;
                    Vector3 fSum = Vector3.zero;
                    foreach (Part q in v.parts)
                        foreach (ConfigurableJoint j in q.gameObject.GetComponents<ConfigurableJoint>())
                        {
                            if (j == null) continue;
                            bool mine = j.GetComponent<Rigidbody>() == rb, other = j.connectedBody == rb;
                            if (!mine && !other) continue;
                            // currentForce/Torque: what the joint applies to its own body; the other body gets the opposite
                            Vector3 f = mine ? -j.currentForce : j.currentForce, t = mine ? -j.currentTorque : j.currentTorque;
                            Vector3 anchor = j.transform.TransformPoint(j.anchor);
                            allMoment += Vector3d.Dot(Vector3d.Cross((Vector3d)anchor - com, (Vector3d)f) + (Vector3d)t, (Vector3d)axis);
                            fSum += f;
                            ++all;
                            if (proj.Length < 400)
                                // projection; the solver's mass scales (1 each conserves momentum); each angular drive's
                                // spring and largest force, and the joint's break torque
                                proj += string.Format(inv, "{0}/{1:F0}/{2:F2} ms {3:G3}/{4:G3} dX {5:G3}/{6:G3} dYZ {7:G3}/{8:G3} dS {9:G3}/{10:G3} brk {11:G3} mot {12}{13}{14} ",
                                    j.projectionMode == JointProjectionMode.None ? "none" : "proj", j.projectionAngle, j.projectionDistance,
                                    j.massScale, j.connectedMassScale,
                                    j.angularXDrive.positionSpring, j.angularXDrive.maximumForce,
                                    j.angularYZDrive.positionSpring, j.angularYZDrive.maximumForce,
                                    j.slerpDrive.positionSpring, j.slerpDrive.maximumForce, j.breakTorque,
                                    j.angularXMotion.ToString()[0], j.angularYMotion.ToString()[0], j.angularZMotion.ToString()[0]);
                        }
                    allForce = fSum.magnitude;
                }
                Quaternion rel = Quaternion.Inverse(rootRot) * p.transform.rotation;
                float twist = Quaternion.Angle(rel, p.orgRot);
                float spin = rb != null ? (rb.angularVelocity - rootSpin).magnitude * Mathf.Rad2Deg : float.NaN;
                Vector3 inertia = rb != null ? rb.inertiaTensor : Vector3.zero;
                // FAR's force on the part and its moment about the vessel CoM, at its aerodynamic centre and at the part's own
                // centre of mass (no flap shift)
                Vector3d vcom = v.CoMD;
                double farMoment = Vector3d.Dot(Vector3d.Cross(st.Ac - vcom, (Vector3d)st.Force), (Vector3d)axis);
                double atPart = Vector3d.Dot(Vector3d.Cross(cm - vcom, (Vector3d)st.Force), (Vector3d)axis);
                // the part's own inertia about the vessel's pitch axis (t m2), and what the couple alone would do to it:
                // its angular acceleration (rad/s2) and the spin it adds in one physics step (rad/s, against maxAngVel)
                double iPitch = double.NaN;
                if (rb != null)
                {
                    Quaternion q = rb.rotation * rb.inertiaTensorRotation;
                    iPitch = 0;
                    for (int i = 0; i < 3; ++i)
                    {
                        float c = Vector3.Dot(q * (i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward), axis);
                        iPitch += inertia[i] * c * c;
                    }
                }
                double alpha = couple / iPitch;
                sb.AppendFormat(inv, "{0}: m {1:F2} FAR F {2:F0} M@ac {3:F0} M@part {4:F0} couple {5:F0} | Ipitch {17:G3} couple/I {18:G3} rad/s2 dw/step {19:G3} rad/s | attach T {6:F0} F {7:F0} | all {8} joints M {9:F0} F {10:F0} [{11}] spin {12:F2} twist {13:F2} I {14:G3} rb.maxAngVel {15:G3} angDrag {16:G3} || ",
                    p.flightID % 10000, rb != null ? rb.mass : p.mass, st.Force.magnitude, farMoment, atPart, couple,
                    jointTorque, jointForce, all, allMoment, allForce, proj.Trim(), spin, twist, inertia.magnitude,
                    rb != null ? rb.maxAngularVelocity : float.NaN, rb != null ? rb.angularDrag : float.NaN,
                    iPitch, alpha, alpha * Time.fixedDeltaTime);
            }
            return sb.ToString();
        }

        /// <summary>
        /// FAR's wing interaction for every control surface among parts: its own area (m2), mean chord and semispan (m),
        /// the wings FAR found ahead of and behind it with each one's area and influence, and the upstream values FAR last
        /// derived from them (effective area, chord and influence). Where a surface's camber lift and forward shift come from.
        /// </summary>
        public static string Interactions(IEnumerable<Part> parts)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            double D(object o, string f) { try { return Convert.ToDouble(o.GetType().GetField(f, any)?.GetValue(o) ?? o.GetType().GetProperty(f, any)?.GetValue(o, null) ?? double.NaN); } catch { return double.NaN; } }
            // pid: the part's index in the craft's part list ("i<n>"), the same in every session that loads the craft
            int index = -1;
            foreach (Part p in parts)
            {
                ++index;
                PartModule m = p.Modules.Cast<PartModule>().FirstOrDefault(x => x.GetType().Name == "FARControllableSurface");
                if (m == null) continue;
                object wi = null;
                for (Type t = m.GetType(); t != null && wi == null; t = t.BaseType)
                    wi = t.GetField("wingInteraction", any | BindingFlags.DeclaredOnly)?.GetValue(m);
                sb.AppendFormat(inv, "{0} #{1} pid i{6}: S {2:F2} MAC {3:F2} b2 {4:F2} m {5:F2}", p.partInfo?.name, p.flightID % 10000,
                    D(m, "S"), D(m, "MAC_actual"), D(m, "b_2_actual"), p.mass, index);
                // the deflection it has now (deg; positive raises its angle of attack) and how it takes pitch
                sb.AppendFormat(inv, " defl {0:F2} pitchLoc {1:F2} pitchaxis {2:F0} maxdeflect {3:F1}",
                    D(m, "AoAoffset"), D(m, "PitchLocation"), D(m, "pitchaxis"), D(m, "maxdeflect"));
                if (wi != null)
                {
                    foreach (string side in new[] { "Forward", "Backward" })
                    {
                        var list = wi.GetType().GetField("nearbyWingModules" + side + "List", any)?.GetValue(wi) as System.Collections.IList;
                        var infl = wi.GetType().GetField("nearbyWingModules" + side + "Influence", any)?.GetValue(wi) as System.Collections.IList;
                        sb.AppendFormat(inv, " | {0}:", side.ToLowerInvariant());
                        if (list == null || list.Count == 0) { sb.Append(" none"); continue; }
                        for (int i = 0; i < list.Count; ++i)
                        {
                            var w = list[i] as PartModule;
                            double f = infl != null && i < infl.Count ? Convert.ToDouble(infl[i]) : double.NaN;
                            sb.AppendFormat(inv, " [{0} S {1:F1} MAC {2:F2} infl {3:F2}]", w != null ? w.part.partInfo?.name + "#" + w.part.flightID % 10000 : "null",
                                w != null ? D(w, "S") : double.NaN, w != null ? D(w, "MAC_actual") : double.NaN, f);
                        }
                    }
                    sb.AppendFormat(inv, " | upstream: area {0:F1} MAC {1:F2} influence {2:F2} liftslope {3:F3}",
                        D(wi, "EffectiveUpstreamArea"), D(wi, "EffectiveUpstreamMAC"), D(wi, "EffectiveUpstreamInfluence"), D(wi, "EffectiveUpstreamLiftSlope"));
                    // a FAR that follows the upstream chain for the camber effect: the chord and area it uses
                    double cm = D(wi, "CamberUpstreamMAC");
                    if (!double.IsNaN(cm))
                        sb.AppendFormat(inv, " | camber chain: area {0:F1} MAC {1:F2} AR {2:F2}", D(wi, "CamberUpstreamArea"), cm, D(wi, "CamberUpstreamAR"));
                    // the camber shift as last applied (m)
                    object shift = null;
                    for (Type t = m.GetType(); t != null && shift == null; t = t.BaseType)
                        shift = t.GetField("camberShift", any | BindingFlags.DeclaredOnly)?.GetValue(m);
                    if (shift is Vector3d sv)
                        sb.AppendFormat(inv, " | shift {0:F2} m", sv.magnitude);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// The flight's wings (as last captured) against FAR's simulation at the vessel's state now: the wings' total
        /// pitching moment each way (kN m about the CoM, nose up positive), then the wings that differ most: Cl and
        /// the aerodynamic centre's distance ahead of the CoM, live/simulated, and the moment difference.
        /// </summary>
        public static string Compare(Vessel v)
        {
            if (v == null || harmony == null)
                return "not armed";
            Vector3 axis = FarApi.PitchUpAxis(v);
            Vector3d com = v.CoM;
            Vector3 nose = v.ReferenceTransform.up;
            // snapshot the live states before the simulation overwrites the wings' fields
            var flown = new Dictionary<PartModule, State>(live);
            if (!FarApi.SimulateNow(v))
                return "no FARAPI";
            double q = v.dynamicPressurekPa;
            Vector3d vHat = ((Vector3d)v.srf_velocity).normalized;
            double liveTotal = 0, simTotal = 0;
            var rows = new List<KeyValuePair<double, string>>();
            var inv = CultureInfo.InvariantCulture;
            foreach (var kv in flown)
            {
                PartModule m = kv.Key;
                if (m == null || m.part == null || m.vessel != v)
                    continue;
                State a = kv.Value, b = Read(m);
                double s = Convert.ToDouble(AccessTools.Field(m.GetType(), "S")?.GetValue(m) ?? 0.0);
                Vector3d simForce = b.LiftDir * (b.Cl * s * q) - vHat * (b.Cd * s * q);
                double ml = Vector3.Dot(Vector3.Cross((Vector3)(a.Ac - com), a.Force), axis);
                double ms = Vector3d.Dot(Vector3d.Cross(b.Ac - com, simForce), (Vector3d)axis);
                liveTotal += ml;
                simTotal += ms;
                rows.Add(new KeyValuePair<double, string>(ms - ml, string.Format(inv,
                    "{0}: Cl {1:F3}/{2:F3} Cd {3:F4}/{4:F4} ac {5:F1}/{6:F1} st {7:F2}/{8:F2} M {9:F0}/{10:F0}",
                    m.part.flightID % 10000, a.Cl, b.Cl, a.Cd, b.Cd, Vector3d.Dot(a.Ac - com, (Vector3d)nose),
                    Vector3d.Dot(b.Ac - com, (Vector3d)nose), a.Stall, b.Stall, ml, ms)));
            }
            rows.Sort((x, y) => Math.Abs(y.Key).CompareTo(Math.Abs(x.Key)));
            var sb = new StringBuilder();
            sb.AppendFormat(inv, "wings live {0:F0} sim {1:F0} kN m | ", liveTotal, simTotal);
            for (int i = 0; i < Math.Min(8, rows.Count); ++i)
                sb.Append(rows[i].Value).Append(" | ");
            return sb.ToString();
        }
    }
}
