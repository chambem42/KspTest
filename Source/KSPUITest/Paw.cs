using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// Part Action Window driver. Opens the PAW the same way a click does
    /// (UIPartActionController.SpawnPartActionWindow) and operates its list items
    /// (UIPartActionButton.OnClick, UIPartActionToggle.OnTap, UIPartActionFloatRange.slider) so the
    /// stock item code paths run. API-level fallbacks (BaseEvent.Invoke / BaseField.SetValue) are
    /// separate and explicit.
    /// </summary>
    public static class Paw
    {
        // ---- part lookup ----

        public static List<Part> CurrentParts()
        {
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch?.ship != null) return EditorLogic.fetch.ship.parts;
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null) return FlightGlobals.ActiveVessel.parts;
            return new List<Part>();
        }

        public static bool PartMatches(Part p, string nameOrTitle)
        {
            return string.Equals(p.partInfo?.name, nameOrTitle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.partInfo?.title, nameOrTitle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.name, nameOrTitle, StringComparison.OrdinalIgnoreCase)
                || (p.partInfo != null && p.partInfo.title.IndexOf(nameOrTitle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Finds a part of the current craft by partInfo.name / title (substring ok). index picks among duplicates.</summary>
        public static Part Find(string nameOrTitle, int index = 0)
        {
            int seen = 0;
            foreach (var p in CurrentParts())
                if (PartMatches(p, nameOrTitle) && seen++ == index) return p;
            var sb = new StringBuilder($"part '{nameOrTitle}' #{index} not found; parts: ");
            foreach (var p in CurrentParts()) sb.Append(p.partInfo?.name).Append(" (").Append(p.partInfo?.title).Append("), ");
            throw new UITestFailure(sb.ToString());
        }

        /// <summary>First part having the given PartModule type name (e.g. "ModuleEngines", "MechJebCore").</summary>
        public static Part FindWithModule(string moduleName, int index = 0)
        {
            int seen = 0;
            foreach (var p in CurrentParts())
                if (p.Modules.Contains(moduleName) && seen++ == index) return p;
            throw new UITestFailure($"no part with module '{moduleName}' #{index}");
        }

        // ---- window ----

        public static UIPartActionWindow Window(Part part)
        {
            var c = UIPartActionController.Instance;
            return c == null ? null : c.GetItem(part, false);
        }

        public static bool IsOpen(Part part)
        {
            var w = Window(part);
            return w != null && w.isValid && w.gameObject.activeInHierarchy && w.ListItems != null && w.ListItems.Count > 0;
        }

        public static IEnumerator Open(Part part, double timeoutSec = 10)
        {
            if (UIPartActionController.Instance == null) throw new UITestFailure("UIPartActionController.Instance is null");
            if (!IsOpen(part))
            {
                Log.Info($"opening PAW for {part.partInfo?.title}");
                UIPartActionController.Instance.SpawnPartActionWindow(part);
            }
            yield return Wait.Until(() => IsOpen(part), timeoutSec, $"PAW for {part.partInfo?.title}", shot: false);
            yield return null;   // let UpdateWindow populate item values
        }

        public static void Close(Part part)
        {
            var w = Window(part);
            if (w != null)
            {
                Log.Info($"closing PAW for {part.partInfo?.title}");
                w.isValid = false;   // the controller destroys invalid windows on its next update
            }
        }

        public static void CloseAll()
        {
            var c = UIPartActionController.Instance;
            if (c?.windows == null) return;
            foreach (var w in c.windows) if (w != null) w.isValid = false;
        }

        // ---- items ----

        private static bool ItemMatches(string wanted, params string[] candidates)
        {
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && (string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase)
                    || Ugui.Clean(c).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return false;
        }

        public static UIPartActionButton FindButtonItem(Part part, string eventNameOrLabel)
        {
            var w = Window(part);
            if (w == null) return null;
            foreach (var item in w.ListItems)
                if (item is UIPartActionButton b && b.Evt != null && ItemMatches(eventNameOrLabel, b.Evt.name, b.Evt.guiName, b.label?.text))
                    return b;
            return null;
        }

        public static T FindFieldItem<T>(Part part, string fieldName) where T : UIPartActionFieldItem
        {
            var w = Window(part);
            if (w == null) return null;
            foreach (var item in w.ListItems)
                if (item is T f && f.Field != null && ItemMatches(fieldName, f.Field.name, f.Field.guiName))
                    return f;
            return null;
        }

        /// <summary>Opens the PAW and clicks a KSPEvent button by event name or displayed label.</summary>
        public static IEnumerator ClickEvent(Part part, string eventNameOrLabel, double timeoutSec = 10)
        {
            yield return Open(part, timeoutSec);
            UIPartActionButton btn = null;
            yield return Wait.Until(() => (btn = FindButtonItem(part, eventNameOrLabel)) != null, timeoutSec,
                $"PAW button '{eventNameOrLabel}' on {part.partInfo?.title}\n" + Dump(part));
            Log.Info($"PAW click '{btn.Evt.guiName}' ({btn.Evt.name}) on {part.partInfo?.title}");
            btn.OnClick();
            yield return null;
        }

        /// <summary>Opens the PAW and taps a toggle field until it shows the wanted state.</summary>
        public static IEnumerator SetToggle(Part part, string fieldName, bool on, double timeoutSec = 10)
        {
            yield return Open(part, timeoutSec);
            UIPartActionToggle t = null;
            yield return Wait.Until(() => (t = FindFieldItem<UIPartActionToggle>(part, fieldName)) != null, timeoutSec,
                $"PAW toggle '{fieldName}' on {part.partInfo?.title}\n" + Dump(part), shot: false);
            bool current = t.Field.GetValue<bool>(t.Field.host);
            if (current != on)
            {
                Log.Info($"PAW toggle '{t.Field.guiName}' ({t.Field.name}) {current} -> {on}");
                // A real click flips the UIButtonToggle first, whose onToggle then runs OnTap (which copies
                // the button state into the field). Go through the button so the whole path executes.
                if (t.toggle != null && t.toggle.ButtonCtrl != null) Ugui.Click(t.toggle.ButtonCtrl.gameObject);
                else t.OnTap();
                yield return null;
                bool after = t.Field.GetValue<bool>(t.Field.host);
                if (after != on) throw new UITestFailure($"toggle '{fieldName}' still {after} after click\n" + Dump(part));
            }
        }

        /// <summary>Opens the PAW and drags a float-range slider to the value (clamped to its range).</summary>
        public static IEnumerator SetSlider(Part part, string fieldName, float value, double timeoutSec = 10)
        {
            yield return Open(part, timeoutSec);
            UIPartActionFloatRange s = null;
            yield return Wait.Until(() => (s = FindFieldItem<UIPartActionFloatRange>(part, fieldName)) != null, timeoutSec,
                $"PAW slider '{fieldName}' on {part.partInfo?.title}\n" + Dump(part), shot: false);
            float clamped = Mathf.Clamp(value, s.slider.minValue, s.slider.maxValue);
            Log.Info($"PAW slider '{s.Field.guiName}' ({s.Field.name}) -> {clamped}");
            s.slider.value = clamped;   // fires onValueChanged -> the item writes the field
            yield return null;
        }

        // ---- API-level fallbacks (no UI involved) ----

        public static BaseField GetField(Part part, string fieldName)
        {
            var f = part.Fields[fieldName];
            if (f != null) return f;
            foreach (PartModule m in part.Modules)
            {
                f = m.Fields[fieldName];
                if (f != null) return f;
            }
            throw new UITestFailure($"field '{fieldName}' not found on {part.partInfo?.title}");
        }

        public static T Get<T>(Part part, string fieldName)
        {
            var f = GetField(part, fieldName);
            return f.GetValue<T>(f.host);
        }

        public static void SetDirect(Part part, string fieldName, object value)
        {
            var f = GetField(part, fieldName);
            Log.Info($"direct set {part.partInfo?.title}.{fieldName} = {value}");
            f.SetValue(value, f.host);
        }

        public static void InvokeDirect(Part part, string eventName)
        {
            BaseEvent e = part.Events[eventName];
            if (e == null)
                foreach (PartModule m in part.Modules) { e = m.Events[eventName]; if (e != null) break; }
            if (e == null) throw new UITestFailure($"event '{eventName}' not found on {part.partInfo?.title}");
            Log.Info($"direct invoke {part.partInfo?.title}.{eventName}");
            e.Invoke();
        }

        // ---- diagnostics ----

        public static string Dump(Part part)
        {
            var w = Window(part);
            if (w == null) return $"PAW for {part.partInfo?.title}: not open";
            var sb = new StringBuilder($"PAW for {part.partInfo?.title} ({w.ListItems.Count} items):\n");
            foreach (var item in w.ListItems)
            {
                switch (item)
                {
                    case UIPartActionButton b:
                        sb.Append("  button  '").Append(b.Evt?.guiName).Append("' (").Append(b.Evt?.name).Append(")\n"); break;
                    case UIPartActionFieldItem f:
                        object v = null;
                        try { v = f.Field?.GetValue(f.Field.host); } catch { }
                        sb.Append("  ").Append(item.GetType().Name.Replace("UIPartAction", "").ToLower().PadRight(8))
                          .Append("'").Append(f.Field?.guiName).Append("' (").Append(f.Field?.name).Append(") = ").Append(v).Append('\n');
                        break;
                    default:
                        sb.Append("  ").Append(item.GetType().Name).Append('\n'); break;
                }
            }
            return sb.ToString();
        }

        public static string DumpAll()
        {
            var c = UIPartActionController.Instance;
            if (c?.windows == null || c.windows.Count == 0) return "PAW: none open";
            var sb = new StringBuilder();
            foreach (var w in c.windows) if (w != null && w.part != null) sb.Append(Dump(w.part));
            return sb.ToString();
        }
    }
}
