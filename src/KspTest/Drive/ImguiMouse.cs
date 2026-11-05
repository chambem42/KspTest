using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// A synthetic mouse for IMGUI windows. The real cursor is the user's (and absent on CI), and Unity's input cannot be
    /// faked, so the mouse lives in the events a window's function sees: a prefix on GUI.CallWindowDelegate wraps the
    /// function, and while the pointer is set each Layout and Repaint pass runs with Event.current a copy of the event
    /// whose position is the pointer, in the window's coordinates (what its hover tests read). A press, drag or release
    /// replaces one Repaint pass of the window under the pointer (the window draws nothing that frame), as a real one
    /// follows the Layout pass. Positions are screen GUI points (y down), as Control.ScreenRect.
    ///
    /// IMGUI takes a press or release (not a drag) only inside clips that hold the real mouse: Unity keeps the real
    /// mouse position for its clips and reads a mouse event outside them as Ignore. In batchmode the real mouse is at the
    /// screen's top left corner, in a game window where the OS cursor was at its last mouse message, so a window that is to take synthetic
    /// presses is placed over it (OverRealMouse), and the control under the pointer must not sit in a scroll view or group of its own (they clip). A press
    /// the window would ignore is logged as such. Input.mousePosition stays the real cursor's: code that reads it needs
    /// PointerFeeds.
    /// </summary>
    public static partial class Imgui
    {
        private class PendingMouse
        {
            public EventType Type;
            public int Button;
            public Vector2 Screen;
            public int Frame = -1;   // the frame whose Repaint became this event
        }

        private static Vector2? _pointer;
        private static PendingMouse _mouse;
        private static Vector2? _realMouse;   // the real mouse as IMGUI has it (screen GUI point), from the last Repaint

        private static void PatchMouse()
        {
            _harmony.Patch(AccessTools.Method(typeof(GUI), "CallWindowDelegate"),
                           new HarmonyMethod(typeof(Imgui), nameof(PreCallWindowMouse)));
        }

        /// <summary>Where the synthetic mouse points (screen GUI point, y down); null when it is not in use.</summary>
        public static Vector2? Pointer => _pointer;

        /// <summary>The synthetic mouse moved to a screen point (it stays there until moved or let go).</summary>
        public static void PointAt(Vector2 screen)
        {
            EnsurePatched();
            _pointer = screen;
        }

        /// <summary>
        /// Where a window of this size is to go to take synthetic presses: over the real mouse (IMGUI takes a press only
        /// inside clips that hold it), as the windows' last Repaint saw it. That is the screen's top left in batchmode,
        /// and in a game window that never had a mouse message; one that had one keeps where the OS cursor then was
        /// (Input.mousePosition, the cursor now, is not it). Kept on screen; a window of no size yet (it lays itself
        /// out) goes with its corner just above and left of the mouse.
        /// </summary>
        public static Rect OverRealMouse(float width, float height)
        {
            EnsurePatched();
            Vector2 real = _realMouse ?? Vector2.zero;
            float x = Mathf.Clamp(real.x - Mathf.Max(width / 2, 10), 0, Mathf.Max(0, Screen.width - width));
            float y = Mathf.Clamp(real.y - Mathf.Max(height / 2, 10), 0, Mathf.Max(0, Screen.height - height));
            Log.Info($"Imgui mouse: the real mouse at {real.x:F0},{real.y:F0}{(_realMouse == null ? " (not seen yet)" : "")}: " +
                     $"a {width:F0}x{height:F0} window placed at {x:F0},{y:F0}");
            return new Rect(x, y, width, height);
        }

        /// <summary>The synthetic mouse let go of: IMGUI sees the real cursor again. Call it in a test's finally.</summary>
        public static void ReleasePointer()
        {
            _pointer = null;
            _mouse = null;
        }

        private static readonly HashSet<MethodBase> Readers = new HashSet<MethodBase>();

        /// <summary>
        /// A mod's own cursor reader (a static method that returns Input.mousePosition as a screen GUI point, y down, in a
        /// Vector2 or Vector3) made to return the pointer while it is set, the real cursor otherwise.
        /// </summary>
        public static void PointerFeeds(MethodInfo reader)
        {
            if (reader == null) throw new UITestFailure("Imgui: no cursor reader to feed the pointer to");
            EnsurePatched();
            if (!Readers.Add(reader)) return;
            string post = reader.ReturnType == typeof(Vector3) ? nameof(PostReader3)
                        : reader.ReturnType == typeof(Vector2) ? nameof(PostReader2)
                        : throw new UITestFailure($"Imgui: {reader.DeclaringType?.Name}.{reader.Name} returns neither a Vector2 nor a Vector3");
            _harmony.Patch(reader, postfix: new HarmonyMethod(typeof(Imgui), post));
        }

        private static void PostReader3(ref Vector3 __result)
        {
            if (_pointer != null) __result = new Vector3(_pointer.Value.x, _pointer.Value.y, 0);
        }

        private static void PostReader2(ref Vector2 __result)
        {
            if (_pointer != null) __result = _pointer.Value;
        }

        /// <summary>The pointer over a screen point for some frames (what a hover shows has drawn by then).</summary>
        public static IEnumerator Hover(Vector2 screen, int frames = 3)
        {
            PointAt(screen);
            yield return Wait.Frames(frames);
        }

        /// <summary>A button pressed at a screen point.</summary>
        public static IEnumerator MouseDown(Vector2 screen, int button = 0, double timeoutSec = 10) =>
            SendMouse(EventType.MouseDown, screen, button, timeoutSec);

        /// <summary>The pointer dragged (its button held) to a screen point.</summary>
        public static IEnumerator MouseDrag(Vector2 screen, int button = 0, double timeoutSec = 10) =>
            SendMouse(EventType.MouseDrag, screen, button, timeoutSec);

        /// <summary>A button let go of at a screen point.</summary>
        public static IEnumerator MouseUp(Vector2 screen, int button = 0, double timeoutSec = 10) =>
            SendMouse(EventType.MouseUp, screen, button, timeoutSec);

        /// <summary>A click: pressed and let go of at one point.</summary>
        public static IEnumerator MouseClick(Vector2 screen, int button = 0)
        {
            yield return MouseDown(screen, button);
            yield return MouseUp(screen, button);
        }

        /// <summary>A drag from one screen point to another, in steps, the button let go of at the end.</summary>
        public static IEnumerator MouseDragFromTo(Vector2 from, Vector2 to, int button = 0, int steps = 4)
        {
            yield return MouseDown(from, button);
            for (int k = 1; k <= steps; k++)
                yield return MouseDrag(Vector2.Lerp(from, to, (float)k / steps), button);
            yield return MouseUp(to, button);
        }

        /// <summary>The images (textures drawn with GUI.DrawTextureWithTexCoords) a visible window drew in its last Repaint.</summary>
        public static List<Control> Images(string window)
        {
            var list = new List<Control>();
            foreach (WindowInfo w in VisibleWindows())
            {
                if (window != null && w.Title.IndexOf(window, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Control c in w.Controls)
                    if (c.Kind == Kind.Image)
                        list.Add(c);
            }

            return list;
        }

        /// <summary>Where a texture was last drawn, on screen, in a visible window (null: not drawn).</summary>
        public static Rect? ImageRect(Texture texture, string window = null)
        {
            foreach (Control c in Images(window))
                if (ReferenceEquals(c.Texture, texture))
                    return c.ScreenRect;
            return null;
        }

        /// <summary>Where a control (button, toggle, grid cell) whose label contains (or equals) text was last drawn, on screen.</summary>
        public static Rect? ControlRect(string label, string window = null, bool exact = false)
        {
            foreach (WindowInfo w in VisibleWindows())
            {
                if (window != null && w.Title.IndexOf(window, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Control c in w.Controls)
                    if (c.Kind != Kind.Image && c.Label != null &&
                        (exact ? string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase)
                               : c.Label.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0))
                        return c.ScreenRect;
            }

            return null;
        }

        private static IEnumerator SendMouse(EventType type, Vector2 screen, int button, double timeoutSec)
        {
            PointAt(screen);
            var m = new PendingMouse { Type = type, Button = button, Screen = screen };
            _mouse = m;
            try
            {
                yield return Wait.Until(() => m.Frame >= 0 && Time.frameCount > m.Frame, timeoutSec,
                                        $"IMGUI mouse {type} at {screen.x:F0},{screen.y:F0}", shot: false, detail: Dump);
            }
            finally
            {
                if (_mouse == m) _mouse = null;
            }

            Log.Info($"IMGUI mouse {type} (button {button}) at {screen.x:F0},{screen.y:F0}");
            yield return null;   // let the event's consequences draw once
        }

        private static void PreCallWindowMouse(ref GUI.WindowFunction func)
        {
            if (func == null) return;
            GUI.WindowFunction inner = func;
            func = id => MousePass(inner, id);
        }

        private static void MousePass(GUI.WindowFunction inner, int id)
        {
            Event real = Event.current;
            if (real != null && real.type == EventType.Repaint)
                _realMouse = GUIUtility.GUIToScreenPoint(real.mousePosition);
            if (real == null || _pointer == null || (real.type != EventType.Layout && real.type != EventType.Repaint))
            {
                inner(id);
                return;
            }

            PendingMouse m = _mouse;
            bool inject = m != null && m.Frame < 0 && real.type == EventType.Repaint && WindowUnder(id, m.Screen);
            var ev = new Event(real) { mousePosition = GUIUtility.ScreenToGUIPoint(inject ? m.Screen : _pointer.Value) };
            if (inject)
            {
                m.Frame = Time.frameCount;
                ev.type = m.Type;
                ev.button = m.Button;
                ev.clickCount = 1;
                ev.modifiers = EventModifiers.None;
            }

            Event.current = ev;
            if (inject && ev.type != m.Type)
                Log.Warn($"Imgui mouse: window '{Info(id).Title}' ignores the {m.Type}: the real mouse is outside it (see OverRealMouse)");
            try
            {
                inner(id);
            }
            finally
            {
                Event.current = real;
            }
        }

        /// <summary>The window a screen point is over (its rect as last drawn).</summary>
        private static bool WindowUnder(int id, Vector2 screen)
        {
            WindowInfo w = Info(id);
            return w.Title.Length > 0 && w.Rect.Contains(screen);
        }
    }
}
