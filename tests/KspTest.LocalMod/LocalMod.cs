using UnityEngine;

namespace KspTestLocalMod
{
    /// <summary>Says it loaded; the test only looks for its assembly.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class LocalMod : MonoBehaviour
    {
        private void Awake() => Debug.Log("[KspTestLocalMod] loaded");
    }
}
