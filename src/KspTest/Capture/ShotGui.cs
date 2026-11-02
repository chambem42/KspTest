using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Other mods' windows hidden for a screenshot or a video, where onHideUI leaves them up: KSP-UI popup dialogs
    /// (Trajectories' window) hidden and, with imgui, IMGUI behaviours (RP-1's integration window, Procedural Parts' hint)
    /// disabled, until Dispose puts them back. The harness's own behaviours are left alone. Disabling a behaviour stops its
    /// Update too (AtmosphereAutopilot, FAR's flight GUI), so in flight leave imgui off.
    /// </summary>
    public sealed class ShotGui : IDisposable
    {
        private readonly List<MonoBehaviour> disabled = new List<MonoBehaviour>();
        private readonly List<GameObject> hidden = new List<GameObject>();

        public static ShotGui HideOthers(bool imgui = true)
        {
            var g = new ShotGui();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var mb in imgui ? UnityEngine.Object.FindObjectsOfType<MonoBehaviour>() : new MonoBehaviour[0])
            {
                if (mb == null || !mb.enabled || mb.GetType().Assembly.GetName().Name.StartsWith("KspTest")) continue;
                if (mb.GetType().GetMethod("OnGUI", flags) == null) continue;
                mb.enabled = false;
                g.disabled.Add(mb);
            }
            foreach (var pd in UnityEngine.Object.FindObjectsOfType<PopupDialog>())
            {
                if (pd == null || !pd.gameObject.activeSelf) continue;
                pd.gameObject.SetActive(false);
                g.hidden.Add(pd.gameObject);
            }
            Log.Info($"ShotGui: {g.disabled.Count} IMGUI behaviours off, {g.hidden.Count} popup dialogs hidden");
            return g;
        }

        public void Dispose()
        {
            foreach (var mb in disabled)
                if (mb != null) mb.enabled = true;
            foreach (var go in hidden)
                if (go != null) go.SetActive(true);
            disabled.Clear();
            hidden.Clear();
        }
    }
}
