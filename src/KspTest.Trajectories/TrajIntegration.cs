using UnityEngine;

namespace KspTest
{
    /// <summary>Gives VideoCapture Trajectories' predicted impact point.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class TrajIntegration : MonoBehaviour
    {
        private void Awake()
        {
            VideoCapture.Impact = Impact;
            Destroy(this);
        }

        private static bool Impact(out double lat, out double lon) => TrajApi.ImpactLatLon(out lat, out lon, out _);
    }
}
