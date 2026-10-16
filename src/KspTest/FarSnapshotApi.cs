using System;
using System.Reflection;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// FAR's off-thread prediction API by reflection: FARAPI.CaptureVesselSnapshot(Vessel) on the main thread, then
    /// FARAPI.CalculateSnapshotAeroForcesAt(FARVesselSnapshot, out force, out torque, velocity, lat/lon/alt, ut) from
    /// any thread. MethodInfo.Invoke is itself safe to call concurrently.
    /// </summary>
    public static class FarSnapshotApi
    {
        private static MethodInfo _capture, _predict, _live;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            Type api = Reflect.FindType("FerramAerospaceResearch.FARAPI");
            Type snap = Reflect.FindType("FerramAerospaceResearch.FARVesselSnapshot");
            if (api == null || snap == null) return;
            const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
            _capture = api.GetMethod("CaptureVesselSnapshot", S, null, new[] { typeof(Vessel) }, null);
            // CalculateSnapshotAeroForcesAt; earlier builds had it as an overload of CalculateVesselAeroForcesAt
            var snapArgs = new[] { snap, typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(Vector3), typeof(Vector3d), typeof(double) };
            _predict = api.GetMethod("CalculateSnapshotAeroForcesAt", S, null, snapArgs, null)
                       ?? api.GetMethod("CalculateVesselAeroForcesAt", S, null, snapArgs, null);
            _live = api.GetMethod("CalculateVesselAeroForcesAt", S, null,
                new[] { typeof(Vessel), typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(Vector3), typeof(Vector3d), typeof(double) }, null);
        }

        /// <summary>True when FAR has the snapshot API.</summary>
        public static bool Present
        {
            get { Resolve(); return _capture != null && _predict != null; }
        }

        /// <summary>A snapshot of the vessel (main thread), or null.</summary>
        public static object Capture(Vessel v)
        {
            Resolve();
            return _capture?.Invoke(null, new object[] { v });
        }

        /// <summary>The prediction against a snapshot; false when FAR refuses it (stale or invalid). Any thread.</summary>
        public static bool Predict(object snapshot, Vector3 vel, Vector3d latLonAlt, double ut, out Vector3 force, out Vector3 torque)
        {
            Resolve();
            object[] args = { snapshot, null, null, vel, latLonAlt, ut };
            object ok = _predict.Invoke(null, args);
            force = args[1] is Vector3 f ? f : Vector3.zero;
            torque = args[2] is Vector3 t ? t : Vector3.zero;
            return ok is bool b && b;
        }

        /// <summary>The live-vessel prediction (FARAPI.CalculateVesselAeroForcesAt(Vessel, ...)), from whatever thread calls it.</summary>
        public static void PredictLive(Vessel v, Vector3 vel, Vector3d latLonAlt, double ut, out Vector3 force, out Vector3 torque)
        {
            Resolve();
            object[] args = { v, null, null, vel, latLonAlt, ut };
            _live.Invoke(null, args);
            force = args[1] is Vector3 f ? f : Vector3.zero;
            torque = args[2] is Vector3 t ? t : Vector3.zero;
        }

        /// <summary>Exact equality of two force/torque pairs (Unity's == is approximate).</summary>
        public static bool Same(Vector3 f1, Vector3 t1, Vector3 f2, Vector3 t2) =>
            f1.x.Equals(f2.x) && f1.y.Equals(f2.y) && f1.z.Equals(f2.z) && t1.x.Equals(t2.x) && t1.y.Equals(t2.y) && t1.z.Equals(t2.z);
    }
}
