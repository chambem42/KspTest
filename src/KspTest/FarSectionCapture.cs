using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// The force each part's voxel sections contribute to a FAR prediction, which FAR only sums: while armed, every
    /// SimulatedForceContext.ApplyForce call on the main thread is bracketed and the change in its FARCenterQuery's
    /// total force is added to that section's part, with the local velocity the prediction gave it.
    /// </summary>
    public static class FarSectionCapture
    {
        private static Harmony _harmony;
        private static bool _armed;
        private static FieldInfo _center, _force, _torque, _aeroModule;

        public sealed class PartForce
        {
            public Vector3d Force;
            /// <summary>The change in the query's torque about the world origin (FARCenterQuery.torque).</summary>
            public Vector3d Torque;
            public Vector3 LocalVel;
            public int Calls;
        }

        public static readonly Dictionary<Part, PartForce> Parts = new Dictionary<Part, PartForce>();

        /// <summary>Each wing's force from its last PrecomputeCenterOfLift call while armed (the pass a prediction
        /// accumulates), with how many calls it had.</summary>
        public static readonly Dictionary<PartModule, (Vector3d force, int calls)> Wings = new Dictionary<PartModule, (Vector3d, int)>();

        public static bool EnsurePatched()
        {
            if (_harmony != null) return true;
            Type ctx = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.SimulatedForceContext");
            Type query = Reflect.FindType("ferram4.FARCenterQuery");
            Type pd = Reflect.FindType("FerramAerospaceResearch.FARAeroComponents.FARAeroSection+PartData");
            MethodInfo apply = ctx?.GetMethod("ApplyForce", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _center = ctx?.GetField("center", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _force = query?.GetField("force");
            _torque = query?.GetField("torque");
            _aeroModule = pd?.GetField("aeroModule");
            if (apply == null || _center == null || _force == null || _aeroModule == null) return false;
            _harmony = new Harmony("KspTest.FarSectionCapture");
            _harmony.Patch(apply, new HarmonyMethod(typeof(FarSectionCapture), nameof(Prefix)),
                           new HarmonyMethod(typeof(FarSectionCapture), nameof(Postfix)));
            Type wing = Reflect.FindType("ferram4.FARWingAerodynamicModel");
            foreach (MethodInfo m in wing.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == "PrecomputeCenterOfLift" && m.ReturnType == typeof(Vector3d) && m.DeclaringType == wing)
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(FarSectionCapture), nameof(WingPostfix)));
            return true;
        }

        // Only the thread that armed it is captured: background cache fills evaluate snapshots on worker threads at the
        // same time, and their sections' forces landed in the capture (and in its dictionaries, unsynchronized).
        private static int _armedThread;

        private static bool Capturing => _armed && System.Threading.Thread.CurrentThread.ManagedThreadId == _armedThread;

        public static void Arm() { Parts.Clear(); Wings.Clear(); _armedThread = System.Threading.Thread.CurrentThread.ManagedThreadId; _armed = true; }
        public static void Disarm() => _armed = false;

        private static void WingPostfix(PartModule __instance, Vector3d __result)
        {
            if (!Capturing) return;
            Wings.TryGetValue(__instance, out var w);
            Wings[__instance] = (__result, w.calls + 1);
        }

        private static void Prefix(object __instance, out Vector3d[] __state)
        {
            __state = null;
            if (!Capturing) return;
            object center = _center.GetValue(__instance);
            if (center != null)
                __state = new[] { (Vector3d)_force.GetValue(center), _torque != null ? (Vector3d)_torque.GetValue(center) : Vector3d.zero };
        }

        private static void Postfix(object __instance, object[] __args, Vector3d[] __state)
        {
            if (!Capturing || __state == null) return;
            object center = _center.GetValue(__instance);
            if (center == null) return;
            Vector3d delta = (Vector3d)_force.GetValue(center) - __state[0];
            Vector3d deltaTorque = _torque != null ? (Vector3d)_torque.GetValue(center) - __state[1] : Vector3d.zero;
            var module = _aeroModule.GetValue(__args[0]) as PartModule;
            Part part = module != null ? module.part : null;
            if (part == null) return;
            if (!Parts.TryGetValue(part, out PartForce pf)) Parts[part] = pf = new PartForce { LocalVel = (Vector3)__args[1] };
            pf.Force += delta;
            pf.Torque += deltaTorque;
            pf.Calls++;
        }
    }
}
