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
    /// The pitching moment of everything queued on the active vessel's parts in one physics step - Part.AddForceAtPosition,
    /// AddForce (at the part's centre of mass) and AddTorque, whoever calls them - about the vessel's CoM on its pitch-up
    /// axis, by part and by the module type that queued it. What the physics really gets, to set against FAR's own account.
    /// </summary>
    public static class QueuedTorque
    {
        private static Harmony harmony;
        private static readonly Dictionary<string, double> step = new Dictionary<string, double>(), last = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> stepParts = new Dictionary<string, double>(), lastParts = new Dictionary<string, double>();
        private static int stepFrame = -1;
        private static readonly TimingManager.UpdateAction early = Early;

        public static bool Arm()
        {
            if (harmony != null)
                return true;
            harmony = new Harmony("KSPUITest.QueuedTorque");
            Type p = typeof(Part);
            harmony.Patch(AccessTools.Method(p, "AddForceAtPosition", new[] { typeof(Vector3d), typeof(Vector3d) }),
                          new HarmonyMethod(typeof(QueuedTorque), nameof(AtPosition)));
            harmony.Patch(AccessTools.Method(p, "AddForce", new[] { typeof(Vector3d) }), new HarmonyMethod(typeof(QueuedTorque), nameof(Force)));
            harmony.Patch(AccessTools.Method(p, "AddTorque", new[] { typeof(Vector3d) }), new HarmonyMethod(typeof(QueuedTorque), nameof(Torque)));
            // where the queue is applied: FlightIntegrator.Integrate (KSPCF transpiles its replacement into it, and
            // ModularFlightIntegrator's override calls it as base)
            MethodInfo integrate = AccessTools.Method(typeof(FlightIntegrator), "Integrate", new[] { typeof(Part) });
            if (integrate != null)
            {
                harmony.Patch(integrate, new HarmonyMethod(typeof(QueuedTorque), nameof(Integrating)));
                IntegrateHooked = integrate.DeclaringType?.Name + "." + integrate.Name;
            }
            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.ObscenelyEarly, early);
            return true;
        }

        internal static string IntegrateHooked = "none";

        // What the integrator does with each part's queue: applied when the part has a rigidbody, dropped otherwise.
        private static void Integrating(Part part)
        {
            Vessel v = part?.vessel;
            if (v == null || v != FlightGlobals.ActiveVessel || part.forces == null)
                return;
            Vector3d axis = (Vector3d)FarApi.PitchUpAxis(v), com = v.CoMD;
            double m = 0;
            foreach (Part.ForceHolder f in part.forces)
                m += Vector3d.Dot(Vector3d.Cross(f.pos - com, f.force), axis);
            // and what goes on at the part's centre of mass, and as torque
            if (part.rb != null)
                m += Vector3d.Dot(Vector3d.Cross((Vector3d)part.rb.worldCenterOfMass - com, part.force), axis) + Vector3d.Dot(part.torque, axis);
            if (m == 0)
                return;
            bool applied = part.rb != null;
            string k = (applied ? "applied" : "DROPPED") + ":" + part.partInfo?.name + "#" + part.flightID % 10000;
            stepParts.TryGetValue(k, out double had);
            stepParts[k] = had + m;
            string t = applied ? "=applied" : "=DROPPED";
            step.TryGetValue(t, out double ht);
            step[t] = ht + m;
            // the same about the bodies' own mass-weighted centre (what the angular momentum is taken about), and the
            // total force, for the term an offset between the two centres adds
            if (applied && !double.IsNaN(trueCom.x))
            {
                double mt = 0;
                Vector3d fsum = part.force;
                foreach (Part.ForceHolder f in part.forces)
                {
                    mt += Vector3d.Dot(Vector3d.Cross(f.pos - trueCom, f.force), axis);
                    fsum += f.force;
                }
                mt += Vector3d.Dot(Vector3d.Cross((Vector3d)part.rb.worldCenterOfMass - trueCom, part.force), axis) + Vector3d.Dot(part.torque, axis);
                step.TryGetValue("=appliedAboutBodies", out double hb);
                step["=appliedAboutBodies"] = hb + mt;
                stepForce += fsum;
            }
        }

        // the bodies' mass-weighted centre this physics step (set in Early, before the integrator), and the total force applied
        private static Vector3d trueCom = new Vector3d(double.NaN, 0, 0), stepForce;

        /// <summary>The bodies' mass-weighted centre less the vessel's CoMD (m), and the last step's total applied force (kN).</summary>
        public static string CentreCheck(Vessel v)
        {
            if (v == null || double.IsNaN(trueCom.x)) return "n/a";
            Vector3d d = trueCom - v.CoMD;
            return string.Format(CultureInfo.InvariantCulture, "bodies' CoM - CoMD {0:F3} m ({1:F2}, {2:F2}, {3:F2}); total force {4:F0} kN",
                d.magnitude, d.x, d.y, d.z, lastForce.magnitude);
        }

        private static Vector3d lastForce;

        private static Vector3d BodiesCom(Vessel v)
        {
            var bodies = new HashSet<Rigidbody>();
            foreach (Part p in v.parts)
                if (p.Rigidbody != null) bodies.Add(p.Rigidbody);
            double m = 0;
            Vector3d mr = Vector3d.zero;
            foreach (Rigidbody rb in bodies) { m += rb.mass; mr += (Vector3d)rb.worldCenterOfMass * rb.mass; }
            return m > 0 ? mr / m : new Vector3d(double.NaN, 0, 0);
        }

        // the root's pitch rate (rad/s, about the pitch-up axis) and the applied total, each physics step
        private static readonly List<double> rates = new List<double>(), uts = new List<double>(), appliedTotals = new List<double>();

        // A test torque on the root part (kN m about the pitch-up axis) for PulseSteps more physics steps, and the rates
        // around it, to see whether the vessel answers a torque at all.
        private static double pulseKNm;
        private static int pulseSteps, pulseDone;
        private static readonly List<double> pulseRates = new List<double>(), pulseUts = new List<double>();

        /// <summary>Applies kNm to the root part about the pitch-up axis for steps physics steps, from the next one.</summary>
        public static void Pulse(double kNm, int steps)
        {
            pulseKNm = kNm;
            pulseSteps = steps;
            pulseDone = 0;
            pulseRates.Clear();
            pulseUts.Clear();
        }

        /// <summary> The pitch acceleration (rad/s2) fitted over the pulse's steps, NaN before it is done. </summary>
        public static double PulseAccel()
        {
            int n = pulseRates.Count;
            if (n < 5 || pulseDone < pulseSteps) return double.NaN;
            double mt = pulseUts.Average(), mr = pulseRates.Average(), sxx = 0, sxy = 0;
            for (int i = 0; i < n; ++i) { sxx += (pulseUts[i] - mt) * (pulseUts[i] - mt); sxy += (pulseUts[i] - mt) * (pulseRates[i] - mr); }
            return sxx > 0 ? sxy / sxx : double.NaN;
        }

        // A sequence of test torques, each on a chosen part for some steps with a gap after: where on the vessel a torque
        // goes in, and whether the vessel answers it there as it does at the root.
        private sealed class SeqPulse
        {
            internal string Label;
            internal Part Target;
            internal double KNm;
            internal int Steps, Gap, Done, GapDone;
            internal readonly List<double> Rates = new List<double>(), Uts = new List<double>();
        }

        /// <summary>
        /// The vessel's angular momentum about its centre of mass on an axis (t m2/s): every rigidbody's spin (its inertia
        /// tensor on its angular velocity) plus its mass moving about the centre of mass. Its rate is the net external
        /// torque (kN m) however the vessel flexes and wherever the torque goes in, if the physics conserves it.
        /// </summary>
        public static double AngularMomentum(Vessel v, Vector3d axis)
        {
            // each body once: a physicsless part's Rigidbody is its parent's
            var bodies = new HashSet<Rigidbody>();
            foreach (Part p in v.parts)
                if (p.Rigidbody != null) bodies.Add(p.Rigidbody);
            double m = 0;
            Vector3d mr = Vector3d.zero, mv = Vector3d.zero;
            foreach (Rigidbody rb in bodies)
            {
                m += rb.mass;
                mr += (Vector3d)rb.worldCenterOfMass * rb.mass;
                mv += (Vector3d)rb.velocity * rb.mass;
            }
            if (m <= 0) return double.NaN;
            Vector3d com = mr / m, vcom = mv / m;
            Vector3d L = Vector3d.zero;
            foreach (Rigidbody rb in bodies)
            {
                Quaternion q = rb.rotation * rb.inertiaTensorRotation;
                Vector3 local = Quaternion.Inverse(q) * rb.angularVelocity;
                L += (Vector3d)(q * Vector3.Scale(rb.inertiaTensor, local));
                L += Vector3d.Cross((Vector3d)rb.worldCenterOfMass - com, ((Vector3d)rb.velocity - vcom) * rb.mass);
            }
            return Vector3d.Dot(L, axis);
        }

        /// <summary>
        /// What else the vessel's physics is joined to: rigidbodies under its parts beyond each part's own (kinematic or
        /// not), and joints from any of them whose other end is not one of the vessel's part rigidbodies (null: the world).
        /// Either can take momentum out of the vessel.
        /// </summary>
        public static string ForeignBodies(Vessel v)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var own = new HashSet<Rigidbody>();
            foreach (Part p in v.parts)
                if (p.Rigidbody != null) own.Add(p.Rigidbody);
            var sb = new StringBuilder();
            int extra = 0, kinematic = 0, foreignJoints = 0;
            foreach (Part p in v.parts)
            {
                foreach (Rigidbody rb in p.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb.isKinematic) ++kinematic;
                    if (rb == p.Rigidbody) continue;
                    ++extra;
                    if (sb.Length < 3000)
                        sb.AppendFormat(inv, " extra rb {0} on {1}: kinematic {2} mass {3:G3};", rb.name, p.partInfo?.name, rb.isKinematic, rb.mass);
                }
                foreach (Joint j in p.GetComponentsInChildren<Joint>(true))
                {
                    Rigidbody a = j.GetComponent<Rigidbody>(), b = j.connectedBody;
                    if (a != null && own.Contains(a) && b != null && own.Contains(b)) continue;
                    ++foreignJoints;
                    if (sb.Length < 6000)
                    {
                        sb.AppendFormat(inv, " joint {0} on {1}: {2} -> {3} ({4})", j.GetType().Name, p.partInfo?.name,
                            a == null ? "none" : a.name + (a.isKinematic ? "[kin]" : ""), b == null ? "WORLD" : b.name + (b.isKinematic ? "[kin]" : ""),
                            own.Contains(b) ? "vessel" : "foreign");
                        if (j is ConfigurableJoint cj)
                            sb.AppendFormat(inv, " lin {0}{1}{2} ang {3}{4}{5} dX {6:G3}/{7:G3}/{8:G3} dYZ {9:G3}/{10:G3} slerp {11:G3}/{12:G3} mode {13} target {14} anchor {15} connAnchor {16} auto {17} brk {18:G3}/{19:G3} enabled {20}",
                                cj.xMotion.ToString()[0], cj.yMotion.ToString()[0], cj.zMotion.ToString()[0],
                                cj.angularXMotion.ToString()[0], cj.angularYMotion.ToString()[0], cj.angularZMotion.ToString()[0],
                                cj.angularXDrive.positionSpring, cj.angularXDrive.positionDamper, cj.angularXDrive.maximumForce,
                                cj.angularYZDrive.positionSpring, cj.angularYZDrive.positionDamper,
                                cj.slerpDrive.positionSpring, cj.slerpDrive.positionDamper, cj.rotationDriveMode,
                                cj.targetRotation.eulerAngles, cj.anchor, cj.connectedAnchor, cj.autoConfigureConnectedAnchor,
                                cj.breakForce, cj.breakTorque, cj.enableCollision);
                        sb.Append(" owners: ").Append(JointOwners(v, j)).Append(';');
                    }
                }
            }
            return string.Format(inv, "{0} part rbs, {1} extra rbs, {2} kinematic, {3} joints off the vessel's part rbs |{4}",
                own.Count, extra, kinematic, foreignJoints, sb);
        }

        // Every component on the vessel's parts and the vessel's own object whose fields hold this joint (directly, in a
        // PartJoint, or in a collection of either): who made it, or at least who keeps it.
        private static string JointOwners(Vessel v, Joint target)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var found = new List<string>();
            bool Holds(object o, int depth)
            {
                if (o == null) return false;
                if (ReferenceEquals(o, target)) return true;
                if (o is PartJoint pj) return pj.Joint == target || (pj.joints != null && pj.joints.Contains(target as ConfigurableJoint));
                if (depth > 0 && o is System.Collections.IEnumerable e && !(o is string))
                {
                    int n = 0;
                    foreach (object x in e) { if (Holds(x, depth - 1)) return true; if (++n > 2000) break; }
                }
                return false;
            }
            var objects = new List<GameObject> { v.gameObject };
            objects.AddRange(v.parts.Select(p => p.gameObject));
            foreach (GameObject go in objects)
                foreach (Component c in go.GetComponents<Component>())
                {
                    if (c == null || c is Joint || c is Transform || c is Rigidbody || c is Collider) continue;
                    for (Type t = c.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(Component); t = t.BaseType)
                        foreach (FieldInfo f in t.GetFields(all | BindingFlags.DeclaredOnly))
                        {
                            Type ft = f.FieldType;
                            if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;
                            object val;
                            try { val = f.GetValue(c); } catch { continue; }
                            if (Holds(val, 2))
                                found.Add(c.GetType().Name + "." + f.Name + " on " + go.name);
                        }
                }
            return found.Count == 0 ? "none found" : string.Join(", ", found.Distinct().Take(8));
        }

        // the vessel's angular momentum each physics step, for its rate against the applied total (Report)
        private static readonly List<double> momenta = new List<double>();

        private static readonly Queue<SeqPulse> sequence = new Queue<SeqPulse>();
        private static readonly List<string> sequenceResults = new List<string>();

        /// <summary>
        /// Queues a pulse: kNm about the pitch-up axis on target (part.AddTorque, applied by the integrator as any
        /// module's torque is) for steps physics steps, then gap steps of nothing. Results in SequenceReport.
        /// </summary>
        public static void QueuePulse(string label, Part target, double kNm, int steps, int gap)
        {
            sequence.Enqueue(new SeqPulse { Label = label, Target = target, KNm = kNm, Steps = steps, Gap = gap });
        }

        public static bool SequenceDone => sequence.Count == 0;

        /// <summary>Each finished pulse: its label, the torque, and the root's pitch acceleration fitted over it (rad/s2).</summary>
        public static string SequenceReport()
        {
            string s = string.Join(" | ", sequenceResults);
            sequenceResults.Clear();
            return s;
        }

        private static void StepSequence(Vessel v)
        {
            if (sequence.Count == 0 || v == null || v.rootPart == null || v.rootPart.rb == null)
                return;
            SeqPulse p = sequence.Peek();
            Vector3d ax = (Vector3d)FarApi.PitchUpAxis(v);
            if (p.Done < p.Steps)
            {
                // the vessel's angular momentum: its fitted rate is the net torque the vessel took (kN m)
                p.Rates.Add(AngularMomentum(v, ax));
                p.Uts.Add(Planetarium.GetUniversalTime());
                if (p.KNm != 0 && p.Target != null && p.Target.vessel == v)
                    p.Target.AddTorque(ax * p.KNm);
                ++p.Done;
                if (p.Done == p.Steps)
                {
                    int n = p.Rates.Count;
                    double mt = p.Uts.Average(), mr = p.Rates.Average(), sxx = 0, sxy = 0;
                    for (int i = 0; i < n; ++i) { sxx += (p.Uts[i] - mt) * (p.Uts[i] - mt); sxy += (p.Uts[i] - mt) * (p.Rates[i] - mr); }
                    double a = sxx > 0 ? sxy / sxx : double.NaN;
                    sequenceResults.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1:F0} kN m on {2} (m {3:F2}): dL/dt {4:F0} kN m",
                        p.Label, p.KNm, p.Target?.partInfo?.name ?? "-", p.Target?.rb != null ? p.Target.rb.mass : double.NaN, a));
                }
                return;
            }
            if (++p.GapDone >= p.Gap)
                sequence.Dequeue();
        }

        // A new physics step: the last one's totals are complete.
        private static void Early()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            lastForce = stepForce;
            stepForce = Vector3d.zero;
            trueCom = v != null ? BodiesCom(v) : new Vector3d(double.NaN, 0, 0);
            StepSequence(v);
            if (pulseDone < pulseSteps && v != null && v.rootPart != null && v.rootPart.rb != null)
            {
                Vector3d ax = (Vector3d)FarApi.PitchUpAxis(v);
                pulseRates.Add(Vector3d.Dot((Vector3d)v.rootPart.rb.angularVelocity, ax));
                pulseUts.Add(Planetarium.GetUniversalTime());
                v.rootPart.AddTorque(ax * pulseKNm);
                ++pulseDone;
            }
            if (v != null && v.rootPart != null && v.rootPart.rb != null)
            {
                rates.Add(Vector3d.Dot((Vector3d)v.rootPart.rb.angularVelocity, (Vector3d)FarApi.PitchUpAxis(v)));
                momenta.Add(AngularMomentum(v, (Vector3d)FarApi.PitchUpAxis(v)));
                uts.Add(Planetarium.GetUniversalTime());
                step.TryGetValue("=applied", out double ap);
                appliedTotals.Add(ap);
                if (rates.Count > 200) { rates.RemoveAt(0); momenta.RemoveAt(0); uts.RemoveAt(0); appliedTotals.RemoveAt(0); }
            }
            last.Clear();
            foreach (var kv in step) last[kv.Key] = kv.Value;
            lastParts.Clear();
            foreach (var kv in stepParts) lastParts[kv.Key] = kv.Value;
            step.Clear();
            stepParts.Clear();
        }

        private static string Caller()
        {
            // the module (or class) that queued it: the first frame outside Part
            var st = new System.Diagnostics.StackTrace(2, false);
            for (int i = 0; i < st.FrameCount && i < 6; ++i)
            {
                MethodBase m = st.GetFrame(i)?.GetMethod();
                Type t = m?.DeclaringType;
                if (t != null && t != typeof(Part) && !t.Name.StartsWith("<"))
                    return t.Name;
            }
            return "?";
        }

        private static void Add(Part part, double moment)
        {
            if (part == null || part.vessel != FlightGlobals.ActiveVessel || double.IsNaN(moment))
                return;
            string c = "queued";
            step.TryGetValue(c, out double had);
            step[c] = had + moment;
            string pk = c + ":" + part.partInfo?.name + "#" + part.flightID % 10000;
            stepParts.TryGetValue(pk, out double hp);
            stepParts[pk] = hp + moment;
        }

        private static void AtPosition(Part __instance, Vector3d vec, Vector3d pos)
        {
            Vessel v = __instance?.vessel;
            if (v == null || v != FlightGlobals.ActiveVessel) return;
            Add(__instance, Vector3d.Dot(Vector3d.Cross(pos - v.CoMD, vec), (Vector3d)FarApi.PitchUpAxis(v)));
        }

        private static void Force(Part __instance, Vector3d vec)
        {
            Vessel v = __instance?.vessel;
            if (v == null || v != FlightGlobals.ActiveVessel) return;
            Rigidbody rb = __instance.Rigidbody;
            Vector3d at = rb != null ? (Vector3d)rb.worldCenterOfMass : __instance.transform.position;
            Add(__instance, Vector3d.Dot(Vector3d.Cross(at - v.CoMD, vec), (Vector3d)FarApi.PitchUpAxis(v)));
        }

        private static void Torque(Part __instance, Vector3d vec)
        {
            Vessel v = __instance?.vessel;
            if (v == null || v != FlightGlobals.ActiveVessel) return;
            Add(__instance, Vector3d.Dot(vec, (Vector3d)FarApi.PitchUpAxis(v)));
        }

        /// <summary>Drops the steps buffered so far, so Balance and Report cover only the steps from here.</summary>
        public static void ResetWindow()
        {
            rates.Clear();
            momenta.Clear();
            uts.Clear();
            appliedTotals.Clear();
        }

        /// <summary>
        /// Over the buffered steps: the mean moment the integrator applied (kN m about the pitch-up axis) and the fitted
        /// rate of the vessel's angular momentum (kN m). Equal when the vessel takes everything it is given. NaN under
        /// 10 steps.
        /// </summary>
        public static void Balance(out double applied, out double dLdt, out int steps)
        {
            steps = uts.Count;
            applied = dLdt = double.NaN;
            if (steps < 10) return;
            double mt = uts.Average(), mL = momenta.Average(), sxx = 0, sxL = 0;
            for (int i = 0; i < steps; ++i) { sxx += (uts[i] - mt) * (uts[i] - mt); sxL += (uts[i] - mt) * (momenta[i] - mL); }
            // appliedTotals[i] is the step before uts[i]'s: the first has nothing behind it in the window
            applied = appliedTotals.Skip(1).Average();
            dLdt = sxx > 0 ? sxL / sxx : double.NaN;
        }

        /// <summary> The last complete physics step: the total, by caller, and the parts with the largest moments. </summary>
        public static string Report()
        {
            if (harmony == null) return "not armed";
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendFormat(inv, "integrate hook {0} |", IntegrateHooked);
            // the pitch acceleration over the last steps (least squares on the rate) against the mean applied moment
            int n = rates.Count;
            if (n >= 20)
            {
                double mt = uts.Average(), mr = rates.Average(), sxx = 0, sxy = 0;
                for (int i = 0; i < n; ++i) { sxx += (uts[i] - mt) * (uts[i] - mt); sxy += (uts[i] - mt) * (rates[i] - mr); }
                double alpha = sxx > 0 ? sxy / sxx : double.NaN;
                double mL = momenta.Average(), sxL = 0;
                for (int i = 0; i < n; ++i) sxL += (uts[i] - mt) * (momenta[i] - mL);
                double dLdt = sxx > 0 ? sxL / sxx : double.NaN;
                sb.AppendFormat(inv, " over {0} steps ({1:F2} s): pitch accel {2:E2} rad/s2, mean applied {3:F0} kN m, implied I {4:E2} kN m s2, angular momentum rate {5:F0} kN m |",
                    n, uts[n - 1] - uts[0], alpha, appliedTotals.Average(), appliedTotals.Average() / alpha, dLdt);
            }
            foreach (var kv in last.OrderByDescending(x => Math.Abs(x.Value)))
                sb.AppendFormat(inv, " {0} {1:F0}", kv.Key, kv.Value);
            sb.Append(" | top parts:");
            foreach (var kv in lastParts.Where(x => x.Key.StartsWith("DROPPED")).OrderByDescending(x => Math.Abs(x.Value)).Take(10)
                                        .Concat(lastParts.Where(x => !x.Key.StartsWith("DROPPED")).OrderByDescending(x => Math.Abs(x.Value)).Take(6)))
                sb.AppendFormat(inv, " {0} {1:F0}", kv.Key, kv.Value);
            return sb.ToString();
        }
    }
}
