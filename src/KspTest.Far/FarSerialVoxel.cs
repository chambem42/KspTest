using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace KspTest
{
    /// <summary>
    /// Makes FAR voxelize on one thread (VehicleVoxel.CreateNewVoxel's multiThreaded forced false). FAR's threaded
    /// voxelization splits the parts across threads that write the same voxel cells, so which part owns a shared cell,
    /// and the per-part areas behind the aero sections, changes from one voxelization to the next. One thread gives
    /// the same voxel every time, so two FAR builds can be compared on equal geometry. Affects voxelizations started
    /// after it is patched.
    /// </summary>
    public static class FarSerialVoxel
    {
        private static Harmony _harmony;

        public static bool EnsurePatched()
        {
            if (_harmony != null) return true;
            Type t = Reflect.FindType("FerramAerospaceResearch.FARPartGeometry.VehicleVoxel");
            MethodInfo target = t?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                 .FirstOrDefault(m => m.Name == "CreateNewVoxel" &&
                                                      m.GetParameters().Any(p => p.Name == "multiThreaded"));
            if (target == null) return false;
            _harmony = new Harmony("KspTest.FarSerialVoxel");
            _harmony.Patch(target, new HarmonyMethod(typeof(FarSerialVoxel), nameof(Prefix)));
            return true;
        }

        private static void Prefix(ref bool multiThreaded) => multiThreaded = false;
    }
}
