using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// Opens Trajectories' flight profile plot (FlightProfilePlot.Visible, builds that have it) and takes a screenshot
    /// as the vessel passes each altitude: plot_<km>.png under the results folder.
    /// </summary>
    public class PlotShotProbe : MonoBehaviour
    {
        public string Dir;
        public Queue<double> Alts;
        public readonly List<string> Taken = new List<string>();
        private bool opened;

        private void Update()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
                return;
            if (!opened)
                opened = TrajApi.SetStaticBool("Trajectories.FlightProfilePlot", "Visible", true);
            if (Alts == null || Alts.Count == 0 || v.altitude >= Alts.Peek() * 1000)
                return;
            double km = Alts.Dequeue();
            string file = Path.Combine(Dir, $"plot_{km:F0}.png");
            ScreenCapture.CaptureScreenshot(file);
            Taken.Add($"{km:F0}:{(opened ? "open" : "no plot")}");
            Log.Info($"PlotShotProbe: screenshot at {v.altitude / 1000:F1} km -> {file}");
        }

        private void OnDestroy() => TrajApi.SetStaticBool("Trajectories.FlightProfilePlot", "Visible", false);
    }
}
