using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Dumps every part's drag-cube geometry (what the stock occlusion cones are built from) and its pose relative
    /// to the root part, in the editor or in flight, so the two can be compared offline.
    /// </summary>
    public static class Cubes
    {
        public static string Dump(TestContext ctx, string fileName)
        {
            var inv = CultureInfo.InvariantCulture;
            Part root; System.Collections.Generic.List<Part> parts; Vector3 velLocal = Vector3.zero;
            if (HighLogic.LoadedSceneIsEditor)
            {
                root = EditorLogic.RootPart;
                parts = EditorLogic.fetch.ship.parts;
            }
            else
            {
                var v = FlightGlobals.ActiveVessel;
                root = v.rootPart;
                parts = v.parts;
                velLocal = root.transform.InverseTransformDirection((Vector3)v.srf_velocity.normalized);
            }
            Transform rt = root.transform;
            var sb = new StringBuilder();
            sb.Append("# scene=").Append(HighLogic.LoadedScene)
              .Append(" worldUpLocal=").Append(Fmt(rt.InverseTransformDirection(Vector3.up), inv))
              .Append(" velLocal=").Append(Fmt(velLocal, inv))
              .Append(" rootRot=").Append(Fmt(rt.rotation.eulerAngles, inv));
            // FAR voxelizes in the vessel transform's frame in flight and the root part's in the editor; if the two
            // are not a right-angle apart, every part sits oblique to the flight grid and its projected areas grow.
            if (HighLogic.LoadedSceneIsFlight)
            {
                Transform vt = FlightGlobals.ActiveVessel.vesselTransform;
                Quaternion rel = Quaternion.Inverse(vt.rotation) * rt.rotation;
                sb.Append(" vesselRot=").Append(Fmt(vt.rotation.eulerAngles, inv))
                  .Append(" rootInVessel=").Append(Fmt(rel.eulerAngles, inv));
            }
            sb.Append('\n');
            sb.Append("part,persistentId,posLocal,rotLocal,cubeCount,weightedCenter,weightedSize,areaOccluded,cubes,shielded\n");
            foreach (Part p in parts)
            {
                if (p == null || p.DragCubes == null) continue;
                DragCubeList dc = p.DragCubes;
                var cubes = new StringBuilder();
                foreach (DragCube c in dc.Cubes)
                    cubes.Append(c.Name).Append(':').Append(c.Weight.ToString("G4", inv)).Append(':').Append(Fmt(c.Size, inv)).Append(':').Append(Fmt(c.Center, inv)).Append(';');
                var occ = new StringBuilder();
                for (int i = 0; i < 6; ++i) occ.Append(dc.AreaOccluded[i].ToString("G5", inv)).Append(i < 5 ? "|" : "");
                Quaternion rotLocal = Quaternion.Inverse(rt.rotation) * p.transform.rotation;
                sb.Append(p.partInfo != null ? p.partInfo.name : p.name).Append(',')
                  .Append(p.persistentId).Append(',')
                  .Append(Fmt(rt.InverseTransformPoint(p.transform.position), inv)).Append(',')
                  .Append(Fmt(rotLocal.eulerAngles, inv)).Append(',')
                  .Append(dc.Cubes.Count).Append(',')
                  .Append(Fmt(dc.WeightedCenter, inv)).Append(',')
                  .Append(Fmt(dc.WeightedSize, inv)).Append(',')
                  .Append(occ).Append(',')
                  .Append(cubes).Append(',')
                  .Append(p.ShieldedFromAirstream).Append('\n');
            }
            string path = Path.Combine(ctx.ResultsDir, fileName);
            Directory.CreateDirectory(ctx.ResultsDir);
            File.WriteAllText(path, sb.ToString());
            Log.Info($"drag cubes: {parts.Count} parts -> {fileName}");
            return path;
        }

        private static string Fmt(Vector3 v, CultureInfo inv)
        {
            return v.x.ToString("G5", inv) + "|" + v.y.ToString("G5", inv) + "|" + v.z.ToString("G5", inv);
        }
    }
}
