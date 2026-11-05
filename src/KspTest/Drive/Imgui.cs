using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Immediate-mode GUI driver (RP-1/KCT, MechJeb, most mod windows). IMGUI has no widget tree, so
    /// Harmony postfixes on the choke points every GUI/GUILayout overload funnels into
    /// (GUI.DoButton / DoToggle / SelectionGrid / DoTextField / Label / Window / CallWindowDelegate)
    /// (a) record what is drawn during each Repaint pass - windows, labels, buttons, fields, screen
    /// rects - for Dump()/IsVisible() and (b) satisfy one queued action by forcing the control's return
    /// value when its label (and optionally window title) matches, exactly as if it had been clicked.
    /// Actions fire during the Layout pass so the following Repaint sees consistent state.
    /// Textures drawn with GUI.DrawTextureWithTexCoords are recorded too (Images), and a synthetic mouse
    /// (ImguiMouse.cs) points at and presses on what a window draws.
    /// </summary>
    public static partial class Imgui
    {
        public enum Kind { Button, Toggle, Select, TextField, Image, Label }

        public class Control
        {
            public Kind Kind;
            public string Label;      // button/toggle/grid-cell/label text; image: the texture's name
            public string Preceding;  // text field: the label drawn just before it
            public string Text;       // text field: current text
            public bool Value;        // toggle: current state
            public Rect ScreenRect;
            public bool Enabled;
            public Texture Texture;   // image: the texture drawn
        }

        public class WindowInfo
        {
            public int Id;
            public string Title = "";
            public Rect Rect;
            public float LastSeen;
            public List<Control> Controls = new List<Control>();
            public List<Control> Building = new List<Control>();
            public List<string> Labels = new List<string>();
            public List<string> BuildingLabels = new List<string>();
            public List<Control> LabelControls = new List<Control>();          // labels and boxes with text, where drawn
            public List<Control> BuildingLabelControls = new List<Control>();
        }

        private class Pending
        {
            public Kind Kind;
            public string Label;
            public string Window;
            public string Value;
            public bool Wanted;
            public int Nth = -1;
            public bool Exact;
            public bool Done;
            public string DoneDetail;
        }

        public const int NoWindow = -1;
        private static Harmony _harmony;
        private static readonly Dictionary<int, WindowInfo> Windows = new Dictionary<int, WindowInfo>();
        private static readonly Dictionary<int, string> Titles = new Dictionary<int, string>();
        private static readonly Dictionary<int, Rect> WindowRects = new Dictionary<int, Rect>();
        private static int _currentWindow = NoWindow;
        private static string _lastLabel;
        private static int _textFieldIndex;
        private static int _buttonMatchIndex;   // enabled buttons matching the pending click so far in this window pass
        private static Pending _pending;
        private static bool _registeredDiagnostics;

        // ---------------------------------------------------------------- patching

        public static void EnsurePatched()
        {
            if (_harmony != null) return;
            _harmony = new Harmony("KspTest.Imgui");
            var t = typeof(Imgui);
            Patch(AccessTools.Method(typeof(GUI), "DoButton", new[] { typeof(Rect), typeof(int), typeof(GUIContent), typeof(GUIStyle) }), null, "PostButton");
            Patch(AccessTools.Method(typeof(GUI), "DoToggle", new[] { typeof(Rect), typeof(int), typeof(bool), typeof(GUIContent), typeof(GUIStyle) }), null, "PostToggle");
            Patch(AccessTools.Method(typeof(GUI), "SelectionGrid", new[] { typeof(Rect), typeof(int), typeof(GUIContent[]), typeof(int), typeof(GUIStyle) }), null, "PostSelectionGrid");
            Patch(AccessTools.Method(typeof(GUI), "DoTextField", new[] { typeof(Rect), typeof(int), typeof(GUIContent), typeof(bool), typeof(int), typeof(GUIStyle), typeof(string), typeof(char) }), null, "PostTextField");
            Patch(AccessTools.Method(typeof(GUI), "Label", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) }), null, "PostLabel");
            Patch(AccessTools.Method(typeof(GUI), "Box", new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) }), null, "PostBox");
            Patch(AccessTools.Method(typeof(GUI), "Window", new[] { typeof(int), typeof(Rect), typeof(GUI.WindowFunction), typeof(GUIContent), typeof(GUIStyle) }), null, "PostWindow");
            Patch(AccessTools.Method(typeof(GUI), "CallWindowDelegate"), "PreCallWindow", "PostCallWindow");
            Patch(AccessTools.Method(typeof(GUI), "DrawTextureWithTexCoords", new[] { typeof(Rect), typeof(Texture), typeof(Rect), typeof(bool) }), null, "PostTexture");
            PatchMouse();
            if (!_registeredDiagnostics)
            {
                TestRunner.DiagnosticsProviders.Add(Dump);
                _registeredDiagnostics = true;
            }
            Log.Info("IMGUI driver patched");
        }

        private static void Patch(System.Reflection.MethodInfo target, string prefix, string postfix)
        {
            if (target == null) throw new UITestFailure("Imgui: IMGUI method to patch not found (Unity version mismatch?)");
            _harmony.Patch(target,
                prefix == null ? null : new HarmonyMethod(typeof(Imgui), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(Imgui), postfix));
        }

        // ---------------------------------------------------------------- window tracking

        private static WindowInfo Info(int id)
        {
            if (!Windows.TryGetValue(id, out var w))
            {
                w = new WindowInfo { Id = id };
                Windows[id] = w;
            }
            if (Titles.TryGetValue(id, out var title)) w.Title = title;
            if (WindowRects.TryGetValue(id, out var r)) w.Rect = r;
            return w;
        }

        private static void PreCallWindow(int id)
        {
            _currentWindow = id;
            _lastLabel = null;
            _textFieldIndex = 0;
            _buttonMatchIndex = 0;
            if (Event.current.type == EventType.Repaint)
            {
                var w = Info(id);
                w.Building.Clear();
                w.BuildingLabels.Clear();
                w.BuildingLabelControls.Clear();
            }
        }

        private static void PostCallWindow(int id)
        {
            if (Event.current.type == EventType.Repaint)
            {
                var w = Info(id);
                var tmp = w.Controls; w.Controls = w.Building; w.Building = tmp;
                var tl = w.Labels; w.Labels = w.BuildingLabels; w.BuildingLabels = tl;
                var tc = w.LabelControls; w.LabelControls = w.BuildingLabelControls; w.BuildingLabelControls = tc;
                w.LastSeen = Time.realtimeSinceStartup;
            }
            _currentWindow = NoWindow;
            _lastLabel = null;
        }

        private static void PostWindow(int id, GUIContent title, Rect __result)
        {
            try
            {
                Titles[id] = Ugui.Clean(title?.text);
                WindowRects[id] = __result;
            }
            catch { }
        }

        // ---------------------------------------------------------------- control postfixes

        private static bool IsRepaint => Event.current.type == EventType.Repaint;
        private static bool IsLayout => Event.current.type == EventType.Layout;

        private static void Record(Control c, Rect position)
        {
            if (!IsRepaint) return;
            c.ScreenRect = ToScreen(position);
            c.Enabled = GUI.enabled;
            Info(_currentWindow).Building.Add(c);
        }

        private static Rect ToScreen(Rect r)
        {
            try
            {
                var p = GUIUtility.GUIToScreenPoint(new Vector2(r.x, r.y));
                var s = GUI.matrix.lossyScale;
                return new Rect(p.x, p.y, r.width * s.x, r.height * s.y);
            }
            catch { return r; }
        }

        private static bool WindowMatches(Pending p)
        {
            if (string.IsNullOrEmpty(p.Window)) return true;
            var w = Info(_currentWindow);
            return w.Title.IndexOf(p.Window, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LabelMatches(Pending p, string label)
        {
            label = Ugui.Clean(label);
            if (p.Exact) return string.Equals(label, p.Label, StringComparison.OrdinalIgnoreCase);
            return string.Equals(label, p.Label, StringComparison.OrdinalIgnoreCase)
                || label.IndexOf(p.Label, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void PostButton(ref bool __result, Rect position, GUIContent content)
        {
            try
            {
                string label = content?.text ?? "";
                Record(new Control { Kind = Kind.Button, Label = Ugui.Clean(label) }, position);
                var p = _pending;
                if (p != null && !p.Done && p.Kind == Kind.Button && IsLayout && GUI.enabled && WindowMatches(p) && LabelMatches(p, label) &&
                    (p.Nth < 0 || _buttonMatchIndex++ == p.Nth))
                {
                    __result = true;
                    p.Done = true;
                    p.DoneDetail = $"'{Ugui.Clean(label)}' in window '{Info(_currentWindow).Title}'";
                }
            }
            catch (Exception e) { Log.Warn("Imgui.PostButton: " + e.Message); }
        }

        private static void PostToggle(ref bool __result, Rect position, bool value, GUIContent content)
        {
            try
            {
                string label = content?.text ?? "";
                Record(new Control { Kind = Kind.Toggle, Label = Ugui.Clean(label), Value = value }, position);
                var p = _pending;
                if (p != null && !p.Done && p.Kind == Kind.Toggle && IsLayout && GUI.enabled && WindowMatches(p) && LabelMatches(p, label))
                {
                    __result = p.Wanted;
                    p.Done = true;
                    p.DoneDetail = $"'{Ugui.Clean(label)}' {value} -> {p.Wanted} in window '{Info(_currentWindow).Title}'";
                }
            }
            catch (Exception e) { Log.Warn("Imgui.PostToggle: " + e.Message); }
        }

        private static void PostSelectionGrid(ref int __result, Rect position, int selected, GUIContent[] contents)
        {
            try
            {
                if (contents == null) return;
                for (int i = 0; i < contents.Length; i++)
                    Record(new Control { Kind = Kind.Select, Label = Ugui.Clean(contents[i]?.text), Value = i == selected }, position);
                var p = _pending;
                if (p != null && !p.Done && p.Kind == Kind.Select && IsLayout && GUI.enabled && WindowMatches(p))
                {
                    for (int i = 0; i < contents.Length; i++)
                    {
                        if (!LabelMatches(p, contents[i]?.text ?? "")) continue;
                        __result = i;
                        p.Done = true;
                        p.DoneDetail = $"grid cell {i} '{Ugui.Clean(contents[i]?.text)}' in window '{Info(_currentWindow).Title}'";
                        break;
                    }
                }
            }
            catch (Exception e) { Log.Warn("Imgui.PostSelectionGrid: " + e.Message); }
        }

        private static void PostTextField(Rect position, GUIContent content)
        {
            try
            {
                int index = _textFieldIndex++;
                Record(new Control { Kind = Kind.TextField, Label = index.ToString(), Preceding = _lastLabel, Text = content?.text }, position);
                var p = _pending;
                if (p != null && !p.Done && p.Kind == Kind.TextField && IsLayout && GUI.enabled && WindowMatches(p))
                {
                    bool match = p.Nth >= 0 ? index == p.Nth : (_lastLabel != null && LabelMatches(p, _lastLabel));
                    if (match && content != null)
                    {
                        string before = content.text;
                        content.text = p.Value;
                        GUI.changed = true;
                        p.Done = true;
                        p.DoneDetail = $"text field #{index} after '{_lastLabel}' '{before}' -> '{p.Value}' in window '{Info(_currentWindow).Title}'";
                    }
                }
            }
            catch (Exception e) { Log.Warn("Imgui.PostTextField: " + e.Message); }
        }

        private static void PostTexture(Rect position, Texture image)
        {
            try
            {
                if (image != null)
                    Record(new Control { Kind = Kind.Image, Label = image.name, Texture = image }, position);
            }
            catch { }
        }

        private static void PostLabel(Rect position, GUIContent content)
        {
            try
            {
                string text = Ugui.Clean(content?.text);
                if (text.Length == 0) return;
                _lastLabel = text;
                if (IsRepaint) Info(_currentWindow).BuildingLabels.Add(text);
                RecordLabel(text, position);
            }
            catch { }
        }

        // a box's text (a tooltip, say) is a label too, but not the one a text field after it is named by
        private static void PostBox(Rect position, GUIContent content)
        {
            try
            {
                string text = Ugui.Clean(content?.text);
                if (text.Length == 0) return;
                if (IsRepaint) Info(_currentWindow).BuildingLabels.Add(text);
                RecordLabel(text, position);
            }
            catch { }
        }

        private static void RecordLabel(string text, Rect position)
        {
            if (!IsRepaint) return;
            Info(_currentWindow).BuildingLabelControls.Add(new Control { Kind = Kind.Label, Label = text, ScreenRect = ToScreen(position), Enabled = GUI.enabled });
        }

        /// <summary>Where a label (or a box's text) that contains (or equals) text was last drawn, on screen (null: not drawn).</summary>
        public static Rect? LabelRect(string text, string window = null, bool exact = false)
        {
            foreach (WindowInfo w in VisibleWindows())
            {
                if (window != null && w.Title.IndexOf(window, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Control c in w.LabelControls)
                    if (exact ? string.Equals(c.Label, text, StringComparison.OrdinalIgnoreCase)
                              : c.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                        return c.ScreenRect;
            }

            return null;
        }

        // ---------------------------------------------------------------- public API

        private static IEnumerator Run(Pending p, double timeoutSec, string what)
        {
            EnsurePatched();
            // The form as it stands when its button is pressed is the one picture worth keeping of it; the
            // fields being filled in are not, and the action itself runs inside the wait, so shoot before it.
            if (p.Kind == Kind.Button && Snap.ClickShots) yield return Snap.AutoShot((p.Window ?? "form") + "-before-" + p.Label);
            _pending = p;
            try
            {
                yield return Wait.Until(() => p.Done, timeoutSec, what, shot: false, detail: Dump);
            }
            finally
            {
                if (_pending == p) _pending = null;
            }
            Log.Info("IMGUI " + p.Kind + " " + p.DoneDetail);
            yield return null;   // let the click's consequences render once
        }

        /// <summary>Clicks the first enabled button whose label contains (or, with exact, equals) text, optionally only inside a window whose title contains window.</summary>
        public static IEnumerator Click(string label, string window = null, double timeoutSec = 15, bool exact = false)
        {
            yield return Run(new Pending { Kind = Kind.Button, Label = label, Window = window, Exact = exact }, timeoutSec,
                $"IMGUI button '{label}'" + (window != null ? $" in window '{window}'" : ""));
        }

        /// <summary>Clicks the nth (from 0) enabled button with this label in a window, as Click does the first.</summary>
        public static IEnumerator ClickNth(string label, int n, string window, double timeoutSec = 15, bool exact = true)
        {
            yield return Run(new Pending { Kind = Kind.Button, Label = label, Window = window, Exact = exact, Nth = n }, timeoutSec,
                $"IMGUI button '{label}' #{n} in window '{window}'");
        }

        /// <summary>Sets a GUI.Toggle / GUILayout.Toggle to the wanted state.</summary>
        public static IEnumerator Toggle(string label, bool on, string window = null, double timeoutSec = 15, bool exact = false)
        {
            yield return Run(new Pending { Kind = Kind.Toggle, Label = label, Wanted = on, Window = window, Exact = exact }, timeoutSec,
                $"IMGUI toggle '{label}'" + (window != null ? $" in window '{window}'" : ""));
        }

        /// <summary>Selects a SelectionGrid / Toolbar cell by its label.</summary>
        public static IEnumerator Select(string cellLabel, string window = null, double timeoutSec = 15, bool exact = true)
        {
            yield return Run(new Pending { Kind = Kind.Select, Label = cellLabel, Window = window, Exact = exact }, timeoutSec,
                $"IMGUI selection grid cell '{cellLabel}'" + (window != null ? $" in window '{window}'" : ""));
        }

        /// <summary>Types a value into the text field drawn right after a label containing precedingLabel.</summary>
        public static IEnumerator SetTextAfterLabel(string precedingLabel, string value, string window = null, double timeoutSec = 15)
        {
            yield return Run(new Pending { Kind = Kind.TextField, Label = precedingLabel, Value = value, Window = window }, timeoutSec,
                $"IMGUI text field after label '{precedingLabel}'" + (window != null ? $" in window '{window}'" : ""));
        }

        /// <summary>Types a value into the nth (0-based, in draw order) text field of a window.</summary>
        public static IEnumerator SetTextNth(int n, string value, string window, double timeoutSec = 15)
        {
            yield return Run(new Pending { Kind = Kind.TextField, Nth = n, Value = value, Window = window }, timeoutSec,
                $"IMGUI text field #{n} in window '{window}'");
        }

        // ---------------------------------------------------------------- queries

        private const float Fresh = 0.5f;

        public static IEnumerable<WindowInfo> VisibleWindows()
        {
            foreach (var w in Windows.Values)
                if (Time.realtimeSinceStartup - w.LastSeen < Fresh) yield return w;
        }

        public static WindowInfo FindWindow(string titlePart)
        {
            foreach (var w in VisibleWindows())
                if (w.Title.IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0) return w;
            return null;
        }

        public static bool WindowOpen(string titlePart) => FindWindow(titlePart) != null;

        public static bool IsVisible(string label, string window = null)
        {
            foreach (var w in VisibleWindows())
            {
                if (window != null && w.Title.IndexOf(window, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var c in w.Controls)
                    if (c.Label != null && c.Label.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                foreach (var l in w.Labels)
                    if (l.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public static IEnumerator WaitWindow(string titlePart, double timeoutSec = 15)
        {
            EnsurePatched();
            yield return Wait.Until(() => WindowOpen(titlePart), timeoutSec, $"IMGUI window '{titlePart}'", shot: false, detail: Dump);
        }

        public static IEnumerator WaitWindowClosed(string titlePart, double timeoutSec = 15)
        {
            EnsurePatched();
            yield return Wait.Until(() => !WindowOpen(titlePart), timeoutSec, $"IMGUI window '{titlePart}' to close", shot: false);
        }

        public static IEnumerator WaitVisible(string label, string window = null, double timeoutSec = 15)
        {
            EnsurePatched();
            yield return Wait.Until(() => IsVisible(label, window), timeoutSec, $"IMGUI '{label}' visible", shot: false, detail: Dump);
        }

        /// <summary>Everything IMGUI drew in the last half second: windows, their labels, buttons, toggles, text fields.</summary>
        public static string Dump()
        {
            if (_harmony == null) return "IMGUI: driver not patched";
            var sb = new StringBuilder("IMGUI windows visible:\n");
            foreach (var w in VisibleWindows())
            {
                sb.Append($"  window {w.Id} '{w.Title}' at {w.Rect.x:F0},{w.Rect.y:F0} {w.Rect.width:F0}x{w.Rect.height:F0}\n");
                foreach (var l in w.Labels) sb.Append("    label    '").Append(Trunc(l)).Append("'\n");
                foreach (var c in w.Controls)
                {
                    switch (c.Kind)
                    {
                        case Kind.Button: sb.Append(c.Enabled ? "    button   '" : "    button-  '").Append(Trunc(c.Label)).Append("'\n"); break;
                        case Kind.Toggle: sb.Append("    toggle   '").Append(Trunc(c.Label)).Append("' = ").Append(c.Value).Append('\n'); break;
                        case Kind.Select: sb.Append("    gridcell '").Append(Trunc(c.Label)).Append("'").Append(c.Value ? " (selected)" : "").Append('\n'); break;
                        case Kind.TextField: sb.Append("    text #").Append(c.Label).Append(" after '").Append(Trunc(c.Preceding)).Append("' = '").Append(Trunc(c.Text)).Append("'\n"); break;
                        case Kind.Image: sb.Append("    image    '").Append(Trunc(c.Label)).Append($"' at {c.ScreenRect.x:F0},{c.ScreenRect.y:F0} {c.ScreenRect.width:F0}x{c.ScreenRect.height:F0}\n"); break;
                    }
                }
            }
            return sb.ToString();
        }

        private static string Trunc(string s) => s == null ? "" : (s.Length > 60 ? s.Substring(0, 57) + "..." : s).Replace('\n', ' ');
    }
}
