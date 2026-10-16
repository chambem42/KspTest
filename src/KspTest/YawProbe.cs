using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Why a vessel holds yaw (or roll) input through an entry: between TopKm and BottomKm, a row of yaw.csv every
    /// EveryFrames physics frames with the sideslip, AoA and bank, the stick, the body rates, FAR's applied aerodynamic
    /// moment about the CoM on each body axis (wings and voxel body apart), each control surface's deflection and its
    /// own yaw and roll moment, the rudder-like surfaces' shielding, the RCS torque and the CoM's offset from the
    /// root's centerline. Every 5 km, yaw_parts.txt gets the parts with the largest yaw moments.
    /// Body axes from the reference transform: nose = up, right = right, roof = -forward. Moments and rates are
    /// projected on right (pitch), roof (yaw) and nose (roll) with the same sign, so a moment and the rate it drives
    /// share a sign. Sideslip is positive with the airflow's velocity to the right of the nose.
    /// </summary>
    public class YawProbe : MonoBehaviour
    {
        public string Dir;
        public double TopKm = 80, BottomKm = 20;
        public int EveryFrames = 25;

        private readonly StringBuilder csv = new StringBuilder();
        private readonly StringBuilder partsTxt = new StringBuilder();
        private List<Part> surfaces;
        private List<string> surfaceNames;
        private int frame;
        private double nextPartsKm = double.NaN;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private void FixedUpdate()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.ReferenceTransform == null || v.rootPart == null)
                return;
            double km = v.altitude / 1000;
            if (km > TopKm || km < BottomKm || v.srfSpeed < 1)
                return;
            if (frame++ % EveryFrames != 0)
                return;

            Transform rt = v.ReferenceTransform;
            Vector3 nose = rt.up, right = rt.right, roof = -rt.forward;
            Vector3 pitchAxis = FarApi.PitchUpAxis(v);
            Vector3 root = v.rootPart.transform.position;
            if (surfaces == null)
                FindSurfaces(v, root, right);

            Vector3d vel = v.srf_velocity.normalized;
            double aoa = Math.Atan2(-Vector3d.Dot(vel, roof), Vector3d.Dot(vel, nose)) * Mathf.Rad2Deg;
            double beta = Math.Atan2(Vector3d.Dot(vel, right), Vector3d.Dot(vel, nose)) * Mathf.Rad2Deg;
            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            Vector3d liftUp = (up - Vector3d.Dot(up, vel) * vel).normalized;
            Vector3d roofP = ((Vector3d)roof - Vector3d.Dot(roof, vel) * vel).normalized;
            double bank = Math.Atan2(Vector3d.Dot(Vector3d.Cross(liftUp, roofP), vel), Vector3d.Dot(liftUp, roofP)) * Mathf.Rad2Deg;

            Vector3 w = v.rootPart.Rigidbody != null ? v.rootPart.Rigidbody.angularVelocity : Vector3.zero;
            var perPart = new Dictionary<Part, Vector3>();
            var perPartForce = new Dictionary<Part, Vector3>();
            Vector3 aero = FarApi.TrueAeroMomentKNm(v, out Vector3 wing, out Vector3 body, perPart, out Vector3 wingF, out Vector3 bodyF, perPartForce);
            // sideways in the flight-path frame: horizontal and square to the airflow, positive to the right of it
            Vector3d sideAxis = Vector3d.Cross(up, vel).normalized;
            // the non-gravitational acceleration KSP integrates (aerodynamic plus anything else), sideways
            double sideAccel = Vector3d.Dot(v.perturbation, sideAxis);
            // ground track heading of the airflow velocity (deg from north)
            Vector3d north = Vector3d.Exclude(up, v.mainBody.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;
            Vector3d horiz = Vector3d.Exclude(up, vel);
            double heading = Math.Atan2(Vector3d.Dot(horiz, east), Vector3d.Dot(horiz, north)) * Mathf.Rad2Deg;
            Vector3 rcs = RcsTorque(v);
            Vector3 com = v.CoM;
            FlightCtrlState c = v.ctrlState;

            if (csv.Length == 0)
            {
                csv.Append("ut,altKm,mach,qPa,aoaDeg,betaDeg,bankDeg,ctrlPitch,ctrlYaw,ctrlRoll,wPitchDps,wYawDps,wRollDps," +
                           "aeroPitch,aeroYaw,aeroRoll,wingYaw,bodyYaw,wingRoll,bodyRoll,rcsPitch,rcsYaw,rcsRoll,comRightM,comRoofM," +
                           "wingSideKN,bodySideKN,sideAccel,massT,headingDeg,mjTorquePitch,mjTorqueRoll,mjTorqueYaw,farPotPitch,farPotRoll,farPotYaw");
                foreach (string n in surfaceNames)
                    csv.Append(",").Append(n).Append("_defl,").Append(n).Append("_yaw,").Append(n).Append("_roll,")
                       .Append(n).Append("_shielded,").Append(n).Append("_exposed");
                csv.Append("\n");
            }
            csv.Append(string.Format(Inv, "{0:F2},{1:F3},{2:F3},{3:F1},{4:F3},{5:F3},{6:F3},{7:F3},{8:F3},{9:F3},{10:F4},{11:F4},{12:F4}",
                Planetarium.GetUniversalTime(), km, v.mach, v.dynamicPressurekPa * 1000, aoa, beta, bank, c.pitch, c.yaw, c.roll,
                Vector3.Dot(w, pitchAxis) * Mathf.Rad2Deg, Vector3.Dot(w, roof) * Mathf.Rad2Deg, Vector3.Dot(w, nose) * Mathf.Rad2Deg));
            csv.Append(string.Format(Inv, ",{0:F3},{1:F3},{2:F3},{3:F3},{4:F3},{5:F3},{6:F3},{7:F3},{8:F3},{9:F3},{10:F4},{11:F4}",
                Vector3.Dot(aero, pitchAxis), Vector3.Dot(aero, roof), Vector3.Dot(aero, nose),
                Vector3.Dot(wing, roof), Vector3.Dot(body, roof), Vector3.Dot(wing, nose), Vector3.Dot(body, nose),
                Vector3.Dot(rcs, pitchAxis), Vector3.Dot(rcs, roof), Vector3.Dot(rcs, nose),
                Vector3.Dot(com - root, right), Vector3.Dot(com - root, roof)));
            csv.Append(string.Format(Inv, ",{0:F4},{1:F4},{2:F5},{3:F3},{4:F4}",
                Vector3d.Dot(wingF, sideAxis), Vector3d.Dot(bodyF, sideAxis), sideAccel, v.totalMass, heading));
            // MechJeb's estimate of the torque available on each axis (MechJebModuleAttitudeController.torque: x pitch,
            // y roll, z yaw), which its attitude controller divides the torque it wants by to get an input
            Vector3d mjTorque = MechJebTorque(v);
            csv.Append(string.Format(Inv, ",{0:F2},{1:F2},{2:F2}", mjTorque.x, mjTorque.y, mjTorque.z));
            // what FAR's control surfaces report as their potential torque (ITorqueProvider, positive side, summed)
            Vector3 farPot = Vector3.zero;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    string ns = m.GetType().Namespace ?? "";
                    if (!(m is ITorqueProvider tp) || !(ns.StartsWith("ferram4") || ns.StartsWith("FerramAerospaceResearch")))
                        continue;
                    try
                    {
                        tp.GetPotentialTorque(out Vector3 pos, out _);
                        farPot += pos;
                    }
                    catch (Exception) { }
                }
            csv.Append(string.Format(Inv, ",{0:F2},{1:F2},{2:F2}", farPot.x, farPot.y, farPot.z));
            for (int i = 0; i < surfaces.Count; i++)
            {
                Part p = surfaces[i];
                PartModule m = p != null ? p.Modules.Cast<PartModule>().FirstOrDefault(x => x.GetType().Name == "FARControllableSurface") : null;
                if (m == null)
                {
                    csv.Append(",,,,,");
                    continue;
                }
                Vector3 mo = perPart.TryGetValue(p, out Vector3 had) ? had : Vector3.zero;
                csv.Append(string.Format(Inv, ",{0:F3},{1:F3},{2:F3},{3},{4:F3}",
                    Convert.ToDouble(Read(m, "AoAoffset")), Vector3.Dot(mo, roof), Vector3.Dot(mo, nose),
                    Convert.ToBoolean(Read(m, "isShielded")) ? 1 : 0,
                    Convert.ToDouble(Read(m, "NUFAR_areaExposedFactor")) / Math.Max(1e-9, Convert.ToDouble(Read(m, "S")))));
            }
            csv.Append("\n");

            if (double.IsNaN(nextPartsKm))
                nextPartsKm = Math.Floor(km / 5) * 5;
            if (km <= nextPartsKm)
            {
                nextPartsKm -= 5;
                partsTxt.Append(string.Format(Inv, "{0:F1} km  beta {1:F2}  ctrlYaw {2:F2}  aeroYaw {3:F2} kN m (wing {4:F2}, body {5:F2})  rcsYaw {6:F2}:",
                    km, beta, c.yaw, Vector3.Dot(aero, roof), Vector3.Dot(wing, roof), Vector3.Dot(body, roof), Vector3.Dot(rcs, roof)));
                foreach (var kv in perPart.OrderByDescending(x => Math.Abs(Vector3.Dot(x.Value, roof))).Take(10))
                    partsTxt.Append(string.Format(Inv, "  {0}@{1:F1},{2:F1}={3:F2}", kv.Key.partInfo.name,
                        Vector3.Dot(kv.Key.transform.position - root, right), Vector3.Dot(kv.Key.transform.position - root, nose),
                        Vector3.Dot(kv.Value, roof)));
                partsTxt.Append("\n");
                partsTxt.Append(string.Format(Inv, "      side force {0:F3} kN (wing {1:F3}, body {2:F3}), side accel {3:F4} m/s2 x {4:F2} t = {5:F3} kN:",
                    Vector3d.Dot(wingF + bodyF, sideAxis), Vector3d.Dot(wingF, sideAxis), Vector3d.Dot(bodyF, sideAxis),
                    sideAccel, v.totalMass, sideAccel * v.totalMass));
                foreach (var kv in perPartForce.OrderByDescending(x => Math.Abs(Vector3d.Dot(x.Value, sideAxis))).Take(10))
                    partsTxt.Append(string.Format(Inv, "  {0}@{1:F1},{2:F1}={3:F3}", kv.Key.partInfo.name,
                        Vector3.Dot(kv.Key.transform.position - root, right), Vector3.Dot(kv.Key.transform.position - root, nose),
                        Vector3d.Dot(kv.Value, sideAxis)));
                partsTxt.Append("\n");
                // each control surface's authority as FAR reports it, and what goes into it
                for (int i = 0; i < surfaces.Count; i++)
                {
                    Part p = surfaces[i];
                    PartModule m = p != null ? p.Modules.Cast<PartModule>().FirstOrDefault(x => x.GetType().Name == "FARControllableSurface") : null;
                    if (m == null)
                        continue;
                    string pot;
                    try
                    {
                        ((ITorqueProvider)m).GetPotentialTorque(out Vector3 pos, out _);
                        pot = string.Format(Inv, "{0:F2}/{1:F2}/{2:F2}", pos.x, pos.y, pos.z);
                    }
                    catch (Exception e) { pot = e.GetType().Name + ": " + e.Message; }
                    partsTxt.Append(string.Format(Inv,
                        "      surf {0}: axes p{1}/y{2}/r{3} maxdefl {4} frac {5} ctrlSlope {6:F4} finalSlope {7:F4} deflRad {8:F4} loc p{9:F2}/y{10:F2}/r{11:F2} liftDir {12} shielded {13} pot P/R/Y {14}\n",
                        surfaceNames[i], Read(m, "pitchaxis"), Read(m, "yawaxis"), Read(m, "rollaxis"), Read(m, "maxdeflect"), Read(m, "ctrlSurfFrac"),
                        Convert.ToDouble(Read(m, "ControlLiftSlope")), Convert.ToDouble(Read(m, "FinalLiftSlope")), Convert.ToDouble(Read(m, "ControlDeflectionRad")),
                        Convert.ToDouble(Read(m, "PitchLocation")), Convert.ToDouble(Read(m, "YawLocation")), Convert.ToDouble(Read(m, "RollLocation")),
                        Read(m, "liftDirection"), Read(m, "isShielded"), pot));
                }
                Flush();
            }
        }

        private void FindSurfaces(Vessel v, Vector3 root, Vector3 right)
        {
            surfaces = new List<Part>();
            surfaceNames = new List<string>();
            foreach (Part p in v.parts)
                if (p.Modules.Cast<PartModule>().Any(x => x.GetType().Name == "FARControllableSurface"))
                {
                    double x = Vector3.Dot(p.transform.position - root, right);
                    string side = Math.Abs(x) < 0.3 ? "C" : x > 0 ? "R" : "L";
                    string n = p.partInfo.name.Replace("B9.Aero.Wing.Procedural.", "") + side + (p.flightID % 1000);
                    surfaces.Add(p);
                    surfaceNames.Add(n);
                }
        }

        private static Vector3 RcsTorque(Vessel v)
        {
            Vector3 com = v.CoM, torque = Vector3.zero;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleRCS rcs) || rcs.thrustForces == null || rcs.thrusterTransforms == null)
                        continue;
                    for (int i = 0; i < rcs.thrusterTransforms.Count && i < rcs.thrustForces.Length; i++)
                    {
                        Transform t = rcs.thrusterTransforms[i];
                        if (t == null || rcs.thrustForces[i] <= 0f)
                            continue;
                        Vector3 f = -(rcs.useZaxis ? t.forward : t.up) * rcs.thrustForces[i];
                        torque += Vector3.Cross(t.position - com, f);
                    }
                }
            return torque;
        }

        private static object Read(object o, string name)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                       | System.Reflection.BindingFlags.NonPublic;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, any | System.Reflection.BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
                var pr = t.GetProperty(name, any | System.Reflection.BindingFlags.DeclaredOnly);
                if (pr != null) return pr.GetValue(o, null);
            }
            return double.NaN;
        }

        private void Flush()
        {
            if (Dir == null)
                return;
            try
            {
                File.WriteAllText(Path.Combine(Dir, "yaw.csv"), csv.ToString());
                File.WriteAllText(Path.Combine(Dir, "yaw_parts.txt"), partsTxt.ToString());
            }
            catch (Exception ex) { Log.Info("YawProbe: write failed: " + ex.Message); }
        }

        private void OnDestroy() => Flush();
    
        private static System.Reflection.MethodInfo _getMasterMechJeb;
        private static System.Reflection.FieldInfo _attitude, _torque;

        // MechJebModuleAttitudeController.torque of the vessel's master MechJeb, by reflection; NaN when not there.
        private static Vector3d MechJebTorque(Vessel v)
        {
            try
            {
                if (_getMasterMechJeb == null)
                {
                    _getMasterMechJeb = Reflect.FindType("MuMech.VesselExtensions")?.GetMethod("GetMasterMechJeb",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(Vessel) }, null);
                    _attitude = Reflect.FindType("MuMech.MechJebCore")?.GetField("Attitude");
                    _torque = Reflect.FindType("MuMech.MechJebModuleAttitudeController")?.GetField("torque");
                }
                object core = _getMasterMechJeb?.Invoke(null, new object[] { v });
                object att = core == null ? null : _attitude?.GetValue(core);
                if (att != null && _torque != null)
                    return (Vector3d)_torque.GetValue(att);
            }
            catch (Exception) { }
            return new Vector3d(double.NaN, double.NaN, double.NaN);
        }
}
}
