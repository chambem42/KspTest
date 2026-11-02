using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspTest
{
    /// <summary>A takeoff run for the craft in the editor, from a Serve session, and back to the editor.</summary>
    public static class TakeoffCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // the vessel set at the runway spawn point: on rails, turned so its nose runs down the runway and its top stays
        // up, its lowest wheel 0.3 m above the spawn point's surface; off rails and settled
        private static IEnumerator ToRunway(Vessel v, Dictionary<string, object> answer)
        {
            var spawn = PSystemSetup.Instance?.GetSpaceCenterFacility("Runway")?.GetSpawnPoint("Runway");
            if (spawn == null) throw new InvalidOperationException("no runway spawn point");
            spawn.GetSpawnPointLatLonAlt(out double lat, out double lon, out double alt);
            CelestialBody body = v.mainBody;
            Transform t = spawn.GetSpawnPointTransform();
            Vector3d up = body.GetSurfaceNVector(lat, lon);
            Vector3d dir = Math.Abs(Vector3d.Dot(t.forward, up)) < Math.Abs(Vector3d.Dot(t.up, up)) ? (Vector3d)t.forward : (Vector3d)t.up;
            dir = Vector3d.Exclude(up, dir).normalized;

            // the wheels' lowest point below the root, along the vessel's top
            Transform rt = v.ReferenceTransform;
            Vector3 top = -rt.forward, nose = rt.up;
            float below = 0;
            foreach (Part p in v.parts)
                foreach (MeshFilter mf in p.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null || !mf.sharedMesh.isReadable || mf.GetComponentInParent<Part>() != p) continue;
                    foreach (Vector3 vert in mf.sharedMesh.vertices)
                        below = Mathf.Min(below, Vector3.Dot(mf.transform.TransformPoint(vert) - v.transform.position, top));
                }
            Quaternion turn = Quaternion.LookRotation((Vector3)dir, (Vector3)up) * Quaternion.Inverse(Quaternion.LookRotation(nose, top));
            Vector3d surface = body.GetWorldSurfacePosition(lat, lon, alt);
            Vector3d rootAt = surface + up * (-below + 0.3);

            v.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
            v.GoOnRails();
            yield return new WaitForFixedUpdate();
            v.SetRotation(turn * v.transform.rotation);
            v.SetPosition(rootAt);
            v.latitude = lat;
            v.longitude = lon;
            v.altitude = alt - below + 0.3;
            v.srfRelRotation = Quaternion.Inverse(body.bodyTransform.rotation) * v.transform.rotation;
            yield return new WaitForFixedUpdate();
            v.IgnoreGForces(240);
            v.GoOffRails();
            yield return Wait.GameSeconds(6);
            answer["runwayHeadingDir"] = $"{Vector3d.Dot(dir, body.transform.up):F3}";
            answer["movedM"] = Math.Round((v.transform.position - (Vector3)rootAt).magnitude, 2);
        }

        [ServeCommand("takeoff", "the editor's craft on the runway (a KCT sim in RP-1), engines staged at throttle=1 against the brakes for spoolSec=6, " +
                                 "released; from vr=95 m/s the nose raised at rateDegS=3 to pitchDeg=9 (pitch held there); ends at climbM=30 above " +
                                 "the start or timeoutSec=150: VR's time and distance, liftoff's speed, distance and pitch, the most pitch, parts lost, " +
                                 "a sample every sampleS=1 s (with heading, sideways drift and sideslip). steer=on flies it as a pilot would: the rudder " +
                                 "back onto the centerline, the wings held level; yaw=V holds that yaw input instead. back=off stays in flight; moveToRunway=on moves a craft that starts elsewhere onto the runway")]
        public static IEnumerator Takeoff(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string craftPath = Nav.LoadedCraft ?? throw new InvalidOperationException("load a craft first");
            double D(string k, double d) => q[k] != null ? double.Parse(q[k], Inv) : d;
            double vr = D("vr", 95), rate = D("rateDegS", 3), pitchTarget = D("pitchDeg", 9), climbM = D("climbM", 30);
            double timeout = D("timeoutSec", 150), spool = D("spoolSec", 6), sampleS = D("sampleS", 1);
            float throttle = (float)D("throttle", 1);
            bool steer = string.Equals(q["steer"], "on", StringComparison.OrdinalIgnoreCase);
            float yawFixed = (float)D("yaw", 0);       // a constant yaw input instead (which way the craft turns for it)
            answer["craft"] = craftPath;

            ctx.Step("Serve takeoff: to the runway");
            yield return Nav.EnsureState(ctx, StartState.Flight, craftPath, "Runway");
            yield return Wait.Seconds(5);
            Vessel v = FlightGlobals.ActiveVessel;
            // RP-1's KCT sim starts it on the launch pad, whose structures a wing meets within 70 m: moved to the runway's
            // start, its nose down the runway, its wheels just above it
            answer["startedAt"] = v.landedAt;
            if ((v.landedAt ?? "").IndexOf("Runway", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // a craft launched from the VAB (Nav picked the editor from the file's folder before it read the file's
                // type) starts on the pad: say so, unless asked to move it
                if (!string.Equals(q["moveToRunway"], "on", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"launched at {v.landedAt}, not the runway (moveToRunway=on moves it there)");
                ctx.Step("Serve takeoff: onto the runway");
                yield return ToRunway(v, answer);
                v = FlightGlobals.ActiveVessel;
                answer["landedAt"] = v.landedAt;
            }
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m is ModuleReactionWheel rw)
                    {
                        rw.wheelState = ModuleReactionWheel.WheelState.Disabled;
                        rw.authorityLimiter = 0;
                    }
            Ctrl.Sas(false);
            Ctrl.Brakes(true);
            Ctrl.Attach();
            Ctrl.Set(pitch: 0, yaw: 0, roll: 0, throttle: throttle);
            Ctrl.Stage();
            int parts = v.parts.Count;
            var ids = v.parts.Select(ServeSession.Id).ToList();
            answer["massT"] = Math.Round(v.totalMass, 2);

            CelestialBody body = v.mainBody;
            Vector3d Up() => (v.CoMD - body.position).normalized;
            double Pitch() => 90 - Vector3.Angle(v.ReferenceTransform.up, Up());
            // bank: the wings' right axis against the horizon
            double Roll() => 90 - Vector3.Angle(v.ReferenceTransform.right, Up());
            double Agl() => v.altitude - v.terrainAltitude;
            Vector3d start = v.CoMD;
            double Dist() => Vector3d.Exclude(Up(), v.CoMD - start).magnitude;
            // the heading at the start, level: later headings (deg, + to the right) and the drift sideways (m, + right)
            Vector3d fwd0 = Vector3d.Exclude(Up(), (Vector3d)v.ReferenceTransform.up).normalized;
            Vector3d right0 = Vector3d.Cross(Up(), fwd0).normalized;     // Unity: up x forward = right
            double Heading()
            {
                Vector3d f = Vector3d.Exclude(Up(), (Vector3d)v.ReferenceTransform.up).normalized;
                return Math.Atan2(Vector3d.Dot(f, right0), Vector3d.Dot(f, fwd0)) * 180 / Math.PI;
            }
            double Drift() => Vector3d.Dot(v.CoMD - start, right0);
            // the pitch controls' mean deflection (deg, FAR's AoAoffset) ahead of and behind the center of mass
            // the yaw controls' mean deflection (deg, FAR's AoAoffset)
            double Rudder()
            {
                double sum = 0; int n = 0;
                foreach (Part p in v.parts)
                    foreach (PartModule m in p.Modules)
                        if (m.moduleName == "FARControllableSurface")
                            try
                            {
                                if (Convert.ToDouble(Reflect.Get(m, "yawaxis")) == 0) continue;
                                sum += Convert.ToDouble(Reflect.Get(m, "AoAoffset")); n++;
                            }
                            catch { }
                return n == 0 ? double.NaN : sum / n;
            }
            double Elev(bool ahead)
            {
                double sum = 0; int n = 0;
                foreach (Part p in v.parts)
                    foreach (PartModule m in p.Modules)
                        if (m.moduleName == "FARControllableSurface")
                        {
                            try
                            {
                                if (Convert.ToDouble(Reflect.Get(m, "pitchaxis")) == 0) continue;
                                bool isAhead = Vector3.Dot(p.transform.position - v.CoM, v.ReferenceTransform.up) > 0;
                                if (isAhead != ahead) continue;
                                sum += Convert.ToDouble(Reflect.Get(m, "AoAoffset")); n++;
                            }
                            catch { }
                        }
                return n == 0 ? double.NaN : sum / n;
            }
            // sideslip: the velocity's angle off the nose in the craft's own plane (deg, + wind from the right)
            double Beta()
            {
                Vector3d vel = v.srf_velocity;
                if (vel.magnitude < 1) return 0;
                return Math.Asin(Mathf.Clamp((float)(Vector3d.Dot(vel.normalized, (Vector3d)v.ReferenceTransform.right)), -1f, 1f)) * 180 / Math.PI;
            }
            double agl0 = Agl(), pitch0 = Pitch();
            answer["restPitchDeg"] = Math.Round(pitch0, 2);

            ctx.Step("Serve takeoff: spool up");
            yield return Wait.GameSeconds(spool);
            ctx.Step("Serve takeoff: roll");
            Ctrl.Brakes(false);
            double t0 = Planetarium.GetUniversalTime();
            var samples = new List<object>();
            double nextSample = t0, cmd = double.NaN, maxPitch = double.MinValue, lastPitch = pitch0, lastT = t0;
            double lastHdg = 0, lastRoll = 0;
            float yawIn = 0;
            Dictionary<string, object> atVr = null, liftoff = null;
            bool broken = false;
            var wait = new WaitForFixedUpdate();
            try
            {
                while (true)
                {
                    yield return wait;
                    v = FlightGlobals.ActiveVessel;
                    if (v == null || v.parts.Count < parts)
                    {
                        broken = true;
                        var left = v == null ? new HashSet<string>() : new HashSet<string>(v.parts.Select(ServeSession.Id));
                        answer["lost"] = ids.Where(i => !left.Contains(i)).Cast<object>().ToList();
                        answer["brokeAtS"] = Math.Round(Planetarium.GetUniversalTime() - t0, 2);
                        answer["lastRollDeg"] = v == null ? (object)null : Math.Round(Roll(), 2);
                        break;
                    }
                    double t = Planetarium.GetUniversalTime(), dt = Math.Max(t - lastT, 1e-3);
                    double pitch = Pitch(), pitchRate = (pitch - lastPitch) / dt, agl = Agl() - agl0, spd = v.srfSpeed;
                    lastPitch = pitch; lastT = t;
                    maxPitch = Math.Max(maxPitch, pitch);
                    if (atVr == null && spd >= vr)
                    {
                        atVr = new Dictionary<string, object> { ["timeS"] = Math.Round(t - t0, 1), ["distanceM"] = Math.Round(Dist()) };
                        cmd = pitch;
                    }
                    float input = 0;
                    if (atVr != null)
                    {
                        // the commanded pitch ramps up at the rotation rate; a PD on the attitude follows it
                        cmd = Math.Min(pitchTarget, cmd + rate * dt);
                        input = Mathf.Clamp((float)(0.3 * (cmd - pitch) - 0.6 * pitchRate), -1f, 1f);
                    }
                    Ctrl.Pitch = input;
                    if (!steer && yawFixed != 0)
                        Ctrl.Yaw = yawIn = yawFixed;
                    if (steer)
                    {
                        // a heading back toward the centerline (2 deg per 100 m off it, at most 5), a PD on it through the
                        // rudder (and the nose wheel); a PD on the bank through the ailerons
                        double hdg = Heading(), roll = Roll(), hdgRate = (hdg - lastHdg) / dt, rollRate = (roll - lastRoll) / dt;
                        double want = Math.Max(-5, Math.Min(5, -0.02 * Drift()));
                        yawIn = Mathf.Clamp((float)(0.2 * (want - hdg) - 0.4 * hdgRate), -1f, 1f);
                        Ctrl.Yaw = yawIn;
                        Ctrl.Roll = Mathf.Clamp((float)(0.05 * roll + 0.1 * rollRate), -1f, 1f);
                        lastHdg = hdg; lastRoll = roll;
                    }
                    if (liftoff == null && atVr != null && agl > 0.5 && !v.Landed)
                        liftoff = new Dictionary<string, object>
                        {
                            ["timeS"] = Math.Round(t - t0, 1), ["speed"] = Math.Round(spd, 1), ["distanceM"] = Math.Round(Dist()),
                            ["pitchDeg"] = Math.Round(pitch, 2), ["aoaDeg"] = Math.Round(Flight.AoA, 2),
                        };
                    if (t >= nextSample)
                    {
                        nextSample += sampleS;
                        samples.Add(new List<object> { Math.Round(t - t0, 1), Math.Round(spd, 1), Math.Round(Dist()), Math.Round(pitch, 2),
                                                       Math.Round(agl, 2), Math.Round(input, 2), Math.Round(Roll(), 2),
                                                       Math.Round(Heading(), 2), Math.Round(Drift(), 2), Math.Round(Beta(), 2),
                                                       Math.Round(Elev(true), 2), Math.Round(Elev(false), 2), Math.Round(yawIn, 2), Math.Round(Rudder(), 2) });
                    }
                    if (agl > climbM || t - t0 > timeout) break;
                }
            }
            finally
            {
                Ctrl.Detach();
            }
            answer["vr"] = atVr;
            answer["liftoff"] = liftoff;
            answer["maxPitchDeg"] = Math.Round(maxPitch, 2);
            answer["samples [t, speed, distance, pitch, agl, input, roll, heading (+ right), drift m (+ right), beta, pitch controls ahead of / behind the CoM (deg), yaw input, rudder (deg)]"] = samples;
            v = FlightGlobals.ActiveVessel;
            answer["broken"] = broken ? (v == null ? "no vessel" : $"{parts - v.parts.Count} parts lost") : null;
            if (!string.Equals(q["back"], "off", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Step("Serve takeoff: back to the SPH");
                yield return Nav.EnsureState(ctx, StartState.Sph, craftPath);
                Ugui.DismissPopups();
                answer["scene"] = HighLogic.LoadedScene.ToString();
            }
        }
    }
}
