using System;
using System.Collections;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>Coroutine wait helpers. All timeouts are wall-clock seconds; a timeout throws UITestFailure.</summary>
    public static class Wait
    {
        public const double DefaultTimeout = 120;

        /// <summary>What the harness is currently waiting for (shown on screen by the runner overlay).</summary>
        public static string Current { get; private set; }
        public static float CurrentSince { get; private set; }
        public static double CurrentTimeout { get; private set; }

        /// <summary>
        /// Waits for cond. With shot (the default) a screenshot is taken the moment it comes true, so pass
        /// shot: false for waits that only bridge to a state the next step photographs anyway (a scene load, a
        /// settling delay, a form field being set).
        /// </summary>
        public static IEnumerator Until(Func<bool> cond, double timeoutSec, string what, bool shot = true)
        {
            float start = Time.realtimeSinceStartup;
            float deadline = start + (float)timeoutSec;
            Current = FirstLine(what);
            CurrentSince = start;
            CurrentTimeout = timeoutSec;
            try
            {
                while (true)
                {
                    bool ok;
                    try { ok = cond(); }
                    catch (Exception e) { throw new UITestFailure($"condition '{what}' threw {e.GetType().Name}: {e.Message}", e); }
                    if (ok) break;
                    if (Time.realtimeSinceStartup > deadline)
                        throw new UITestFailure($"timeout after {timeoutSec:F0}s waiting for: {what}");
                    yield return null;
                }
            }
            finally
            {
                Current = null;
            }
            // what the screen looked like when the wait was satisfied (a timeout gets the end-of-test shot)
            if (shot) yield return Snap.WaitShot(FirstLine(what));
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl);
        }

        public static IEnumerator Until(Func<bool> cond, string what, bool shot = true) => Until(cond, DefaultTimeout, what, shot);

        public static IEnumerator Seconds(double s)
        {
            float end = Time.realtimeSinceStartup + (float)s;
            yield return Until(() => Time.realtimeSinceStartup >= end, s + 5, $"{s:F1} s (wall clock)", shot: false);
        }

        /// <summary>Game-time seconds (respects time warp), via the vessel clock.</summary>
        public static IEnumerator GameSeconds(double s)
        {
            double end = Planetarium.GetUniversalTime() + s;
            yield return Until(() => Planetarium.GetUniversalTime() >= end, DefaultTimeout + s, $"{s:F1} s (game time)", shot: false);
        }

        public static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        public static IEnumerator Scene(GameScenes scene, double timeoutSec = DefaultTimeout)
        {
            yield return Until(() => HighLogic.LoadedScene == scene, timeoutSec, $"scene {scene}", shot: false);   // nothing drawn yet
            yield return null;
        }

        public static bool IsEditorReady()
        {
            return HighLogic.LoadedSceneIsEditor
                && EditorLogic.fetch != null
                && EditorDriver.fetch != null
                && !EditorDriver.fetch.restartingEditor;
        }

        /// <summary>Editor scene loaded and (when requireShip) a craft with a root part is present.</summary>
        public static IEnumerator EditorReady(bool requireShip = true, double timeoutSec = DefaultTimeout)
        {
            yield return Until(() =>
                {
                    FailOnErrorPopup();   // e.g. "Craft Loading Error: ... parts missing"
                    return IsEditorReady()
                        && (!requireShip || (EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts.Count > 0 && EditorLogic.RootPart != null));
                },
                timeoutSec, requireShip ? "editor ready with a craft" : "editor ready", shot: false);   // the craft is not on screen yet
            // mods (KCT etc.) build their editor GUI in their own Start; give them a couple of frames
            yield return Frames(3);
        }

        public static bool IsFlightReady()
        {
            var v = FlightGlobals.ActiveVessel;
            return HighLogic.LoadedSceneIsFlight
                && FlightDriver.flightStarted
                && FlightGlobals.ready
                && v != null && v.loaded && !v.packed && !v.HoldPhysics
                && !FlightDriver.Pause;
        }

        public static IEnumerator FlightReady(double timeoutSec = DefaultTimeout, double graceSec = 1.0)
        {
            yield return Until(IsFlightReady, timeoutSec, "flight ready (vessel loaded, physics running)");
            yield return Seconds(graceSec);
        }

        /// <summary>Throws if a stock PopupDialog whose title contains "Error" is open (game-side failure).</summary>
        public static void FailOnErrorPopup()
        {
            foreach (var p in Ugui.OpenPopups())
            {
                var d = p.dialogToDisplay;
                if (d == null) continue;
                if ((d.title ?? "").IndexOf("Error", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new UITestFailure($"game error popup '{d.title}': {Ugui.Clean(d.message)}");
            }
        }

        public static IEnumerator PopupClosed(string dialogName, double timeoutSec = 30)
        {
            yield return Until(() => Ugui.FindPopup(dialogName) == null, timeoutSec, $"popup '{dialogName}' closed", shot: false);
        }

        public static IEnumerator NoPopup(double timeoutSec = 30)
        {
            yield return Until(() => Ugui.OpenPopups().Count == 0, timeoutSec, "no PopupDialog open", shot: false);   // CheckForOpenDialogs misses persistAcrossScenes popups
        }
    }
}
