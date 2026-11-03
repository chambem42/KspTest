using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// IMGUI for a -batchmode screenshot. Batchmode sends no OnGUI events at all, so KspTest runs the pass itself, as the
    /// player's GUI manager would: a Layout then a Repaint event through every enabled behaviour with an OnGUI, into the
    /// screenshot's texture. Each behaviour goes between GUIUtility's internal BeginGUI and EndGUI, all of them inside
    /// GUI.BeginWindows/EndWindows (so GUI.Window windows draw) and GUIUtility.BeginContainer, which counts as being in
    /// OnGUI (as UIElements' IMGUIContainer does; without it every GUI call throws "only from inside OnGUI"). KspTest's own
    /// behaviours go last, on top. The runner also runs the pass once a frame (Pump), so the OnGUIs run as they would in a
    /// rendered game.
    /// </summary>
    internal static class SnapGui
    {
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, MethodInfo> onGui = new Dictionary<Type, MethodInfo>();
        private static readonly HashSet<Type> failed = new HashSet<Type>();
        private static Action<int, int, int> beginGui;
        private static Action<int> endGui;
        private static Action<int, int> beginWindows;
        private static Action endWindows;
        private static MethodInfo beginContainer, endContainer;
        private static object guiState;
        private static bool resolved, usable;

        // the behaviours with an OnGUI, found again at most twice a second (FindObjectsOfType is slow in a modded game)
        private static readonly List<MonoBehaviour> found = new List<MonoBehaviour>();
        private static float foundAt = float.NegativeInfinity;
        private static int drawnFrame = -1;
        private static RenderTexture pumpRt;

        /// <summary>
        /// The pass for the frame in -batchmode, so IMGUI windows work as in a rendered game: their OnGUI code runs, and
        /// Imgui sees them and presses their buttons (RP-1's KCT windows, for one). Drawn into a texture nobody reads;
        /// a frame with a screenshot has had its pass already.
        /// </summary>
        public static void Pump()
        {
            if (drawnFrame == Time.frameCount || !Resolve()) return;
            if (pumpRt == null || pumpRt.width != Screen.width || pumpRt.height != Screen.height)
            {
                if (pumpRt != null) pumpRt.Release();
                pumpRt = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32) { name = "KspTest.SnapGui.Pump" };
            }
            Draw(pumpRt);
        }

        public static void Draw(RenderTexture rt)
        {
            if (!Resolve()) return;
            drawnFrame = Time.frameCount;
            if (Time.realtimeSinceStartup - foundAt > 0.5f)
            {
                foundAt = Time.realtimeSinceStartup;
                found.Clear();
                var ours = new List<MonoBehaviour>();
                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null || OnGui(mb.GetType()) == null) continue;
                    (mb.GetType().Assembly.GetName().Name.StartsWith("KspTest") ? ours : found).Add(mb);
                }
                found.AddRange(ours);
            }
            var behaviours = new List<MonoBehaviour>(found.Count);
            foreach (var mb in found)
                if (mb != null && mb.isActiveAndEnabled)
                    behaviours.Add(mb);

            RenderTexture prevRt = RenderTexture.active;
            Event prevEvent = Event.current;
            RenderTexture.active = rt;
            GL.PushMatrix();
            try
            {
                GL.LoadPixelMatrix(0, rt.width, rt.height, 0);
                // at the GUI's own size: a shot bigger than the screen (Snap.ShotSize, Linux batchmode's fixed 640x480)
                // gives windows that run off the screen the room to show whole, where scaling them up with it would cut
                // them off at the shot's edge
                Pass(EventType.Layout, behaviours);
                Pass(EventType.Repaint, behaviours);
            }
            finally
            {
                try { GUI.matrix = Matrix4x4.identity; }
                catch (Exception x) { Log.Warn("batchmode IMGUI: resetting GUI.matrix: " + x.Message); }
                GL.PopMatrix();
                // batchmode has no current event until now, and the setter refuses null
                Event.current = prevEvent ?? new Event { type = EventType.Ignore };
                RenderTexture.active = prevRt;
            }
        }

        private static void Pass(EventType type, List<MonoBehaviour> behaviours)
        {
            // off screen, so nothing is hovered
            var e = new Event { type = type, mousePosition = new Vector2(-10000, -10000) };
            Event.current = e;
            beginContainer.Invoke(null, new[] { guiState });
            try
            {
                beginWindows(0, 0);
                try
                {
                    foreach (var mb in behaviours)
                        if (mb != null)
                            Behaviour(mb, e, type);
                }
                finally
                {
                    Event.current = e;
                    e.type = type;
                    endWindows();
                }
            }
            finally
            {
                endContainer.Invoke(null, null);
            }
        }

        private static void Behaviour(MonoBehaviour mb, Event e, EventType type)
        {
            Event.current = e;
            e.type = type;   // a behaviour may have Used it
            beginGui(0, mb.GetInstanceID(), mb.useGUILayout ? 1 : 0);
            try
            {
                OnGui(mb.GetType()).Invoke(mb, null);
            }
            catch (TargetInvocationException x) when (x.InnerException is ExitGUIException) { }
            catch (Exception x)
            {
                if (failed.Add(mb.GetType()))
                    Log.Warn($"batchmode IMGUI: {mb.GetType().FullName}.OnGUI threw on {type}: {(x.InnerException ?? x).Message}");
            }
            finally
            {
                try { endGui(1); }
                catch (Exception x) { Log.Warn($"batchmode IMGUI: EndGUI after {mb.GetType().FullName}: {x.Message}"); }
            }
        }

        private static MethodInfo OnGui(Type t)
        {
            if (onGui.TryGetValue(t, out var m)) return m;
            for (Type b = t; m == null && b != null && b != typeof(MonoBehaviour); b = b.BaseType)
                m = b.GetMethod("OnGUI", Declared, null, Type.EmptyTypes, null);
            return onGui[t] = m;
        }

        // all internal: GUIUtility.BeginGUI(int skinMode, int instanceID, int useGUILayout), EndGUI(int layoutType),
        // BeginContainer(ObjectGUIState), EndContainer(); GUI.BeginWindows(int skinMode, int editorWindowInstanceID),
        // EndWindows()
        private static bool Resolve()
        {
            if (resolved) return usable;
            resolved = true;
            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                Type stateType = typeof(GUIUtility).Assembly.GetType("UnityEngine.ObjectGUIState");
                var begin = typeof(GUIUtility).GetMethod("BeginGUI", flags, null, new[] { typeof(int), typeof(int), typeof(int) }, null);
                var end = typeof(GUIUtility).GetMethod("EndGUI", flags, null, new[] { typeof(int) }, null);
                var bw = typeof(GUI).GetMethod("BeginWindows", flags, null, new[] { typeof(int), typeof(int) }, null);
                var ew = typeof(GUI).GetMethod("EndWindows", flags, null, Type.EmptyTypes, null);
                beginContainer = stateType == null ? null : typeof(GUIUtility).GetMethod("BeginContainer", flags, null, new[] { stateType }, null);
                endContainer = typeof(GUIUtility).GetMethod("EndContainer", flags, null, Type.EmptyTypes, null);
                if (begin == null || end == null || bw == null || ew == null || beginContainer == null || endContainer == null)
                {
                    Log.Warn("batchmode IMGUI: Unity's internal GUI entry points not found, screenshots without IMGUI");
                    return usable = false;
                }
                beginGui = (Action<int, int, int>)Delegate.CreateDelegate(typeof(Action<int, int, int>), begin);
                endGui = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), end);
                beginWindows = (Action<int, int>)Delegate.CreateDelegate(typeof(Action<int, int>), bw);
                endWindows = (Action)Delegate.CreateDelegate(typeof(Action), ew);
                guiState = Activator.CreateInstance(stateType, true);
                return usable = true;
            }
            catch (Exception x)
            {
                Log.Warn("batchmode IMGUI unavailable, screenshots without IMGUI: " + x.Message);
                return usable = false;
            }
        }
    }
}
