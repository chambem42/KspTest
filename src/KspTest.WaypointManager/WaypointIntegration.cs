using UnityEngine;

namespace KspTest
{
    /// <summary>During a test run, records who adds each waypoint from the start, before any scene adds one.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class WaypointIntegration : MonoBehaviour
    {
        // Start, not Awake: the harness's runner has read the run by then (it is inert without one)
        private void Start()
        {
            if (TestRunner.Instance != null)
                WaypointProbe.HookAdds();
            Destroy(this);
        }
    }
}
