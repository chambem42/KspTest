using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KspTest
{
    /// <summary>
    /// Drives Unity uGUI (stock KSP windows, PopupDialog, app launcher). Elements are found by their
    /// visible text (TMP or legacy Text child) or by GameObject hierarchy path; clicks go through
    /// ExecuteEvents so the Button's own handlers run exactly as for a real pointer click.
    /// </summary>
    public static class Ugui
    {
        private static readonly Regex RichText = new Regex("<[^>]+>", RegexOptions.Compiled);

        public static string Path(GameObject go)
        {
            var sb = new StringBuilder(go.name);
            var t = go.transform.parent;
            while (t != null)
            {
                sb.Insert(0, t.name + "/");
                t = t.parent;
            }
            return sb.ToString();
        }

        /// <summary>Visible label of a control: first TMP/Text child, rich-text tags stripped.</summary>
        public static string LabelOf(Component c)
        {
            var tmp = c.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return Clean(tmp.text);
            var txt = c.GetComponentInChildren<Text>(true);
            if (txt != null && !string.IsNullOrEmpty(txt.text)) return Clean(txt.text);
            return "";
        }

        public static string Clean(string s) => RichText.Replace(s ?? "", "").Trim();

        public static bool Matches(string label, string wanted)
        {
            return string.Equals(label, wanted, StringComparison.OrdinalIgnoreCase)
                || (wanted.Length > 2 && label.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static List<Button> ActiveButtons()
        {
            var list = new List<Button>();
            // the scene's active objects only: FindObjectsOfTypeAll would walk every prefab and asset too, every frame of a wait
            foreach (var b in UnityEngine.Object.FindObjectsOfType<Button>())
                if (b != null && b.gameObject.activeInHierarchy && b.gameObject.scene.IsValid())
                    list.Add(b);
            return list;
        }

        public static Button FindButton(string text, GameObject under = null)
        {
            foreach (var b in ActiveButtons())
            {
                if (under != null && !b.transform.IsChildOf(under.transform)) continue;
                if (Matches(LabelOf(b), text) || Matches(b.name, text)) return b;
            }
            return null;
        }

        public static Button FindButtonByPath(string path)
        {
            foreach (var b in ActiveButtons())
                if (Path(b.gameObject).EndsWith(path, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        /// <summary>Synthesises a full pointer click (down/up/click) on a GameObject via the EventSystem.</summary>
        public static void Click(GameObject go)
        {
            var es = EventSystem.current;
            var data = new PointerEventData(es) { button = PointerEventData.InputButton.Left, clickCount = 1 };
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                var canvas = go.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                data.position = RectTransformUtility.WorldToScreenPoint(cam, (corners[0] + corners[2]) * 0.5f);
            }
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerUpHandler);
            if (!ExecuteEvents.Execute(go, data, ExecuteEvents.pointerClickHandler))
            {
                var b = go.GetComponent<Button>();
                if (b != null) b.onClick.Invoke();
            }
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerExitHandler);
        }

        public static void Click(Button b)
        {
            if (!b.interactable) throw new UITestFailure($"button '{LabelOf(b)}' ({Path(b.gameObject)}) is not interactable");
            Log.Info($"uGUI click '{LabelOf(b)}' ({Path(b.gameObject)})");
            Click(b.gameObject);
        }

        /// <summary>Waits for a button with the given text (or name) to be active, then clicks it.</summary>
        public static IEnumerator ClickButton(string text, double timeoutSec = 30, GameObject under = null)
        {
            Button found = null;
            yield return Wait.Until(() => (found = FindButton(text, under)) != null && found.interactable,
                timeoutSec, $"uGUI button '{text}'" + (under != null ? " under " + under.name : ""), detail: Dump);
            Click(found);
        }

        public static IEnumerator ClickButtonByPath(string path, double timeoutSec = 30)
        {
            Button found = null;
            yield return Wait.Until(() => (found = FindButtonByPath(path)) != null && found.interactable,
                timeoutSec, $"uGUI button at path '{path}'", detail: Dump);
            Click(found);
        }

        public static List<PopupDialog> OpenPopups()
        {
            var list = new List<PopupDialog>();
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PopupDialog>())
                if (p != null && p.gameObject.activeInHierarchy && p.gameObject.scene.IsValid())
                    list.Add(p);
            return list;
        }

        public static PopupDialog FindPopup(string dialogName)
        {
            foreach (var p in OpenPopups())
                if (p.dialogToDisplay != null && string.Equals(p.dialogToDisplay.name, dialogName, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>Clicks a button by text inside an open PopupDialog (any dialog when dialogName is null).</summary>
        public static IEnumerator ClickPopup(string buttonText, string dialogName = null, double timeoutSec = 30)
        {
            Button found = null;
            yield return Wait.Until(() =>
            {
                foreach (var p in OpenPopups())
                {
                    if (dialogName != null && p.dialogToDisplay?.name != dialogName) continue;
                    found = FindButton(buttonText, p.popupWindow != null ? p.popupWindow : p.gameObject);
                    if (found != null) return true;
                }
                return false;
            }, timeoutSec, $"popup button '{buttonText}'" + (dialogName != null ? $" in dialog '{dialogName}'" : ""), detail: Dump);
            Click(found);
        }

        /// <summary>Buttons of one open PopupDialog in dialog order (first = the dialog's first option).</summary>
        public static List<Button> PopupButtons(PopupDialog p)
        {
            var list = new List<Button>();
            var root = p.popupWindow != null ? p.popupWindow : p.gameObject;
            foreach (var b in root.GetComponentsInChildren<Button>(false))
                if (b.gameObject.activeInHierarchy) list.Add(b);
            return list;
        }

        /// <summary>Clicks the nth button (default first, i.e. the "proceed" option) of a named open dialog.</summary>
        public static bool ClickPopupOption(string dialogName, int index = 0)
        {
            var p = FindPopup(dialogName);
            if (p == null) return false;
            var buttons = PopupButtons(p);
            if (index >= buttons.Count) return false;
            Log.Info($"popup '{dialogName}' ({p.dialogToDisplay.title}): clicking option {index} '{LabelOf(buttons[index])}'");
            Click(buttons[index]);
            return true;
        }

        public static void DismissPopups()
        {
            foreach (var p in OpenPopups())
            {
                Log.Info($"dismissing popup '{p.dialogToDisplay?.name}' ({p.dialogToDisplay?.title})");
                p.Dismiss();
            }
        }

        /// <summary>Human-readable list of open popups and active buttons: what a test could click right now.</summary>
        public static string Dump()
        {
            var sb = new StringBuilder();
            var popups = OpenPopups();
            sb.Append("uGUI popups open: ").Append(popups.Count).Append('\n');
            foreach (var p in popups)
                sb.Append("  popup '").Append(p.dialogToDisplay?.name).Append("' title='").Append(p.dialogToDisplay?.title).Append("'\n");
            sb.Append("uGUI active buttons:\n");
            foreach (var b in ActiveButtons())
            {
                string label = LabelOf(b);
                sb.Append("  [").Append(b.interactable ? "on " : "off").Append("] '").Append(label).Append("'  ").Append(Path(b.gameObject)).Append('\n');
            }
            return sb.ToString();
        }
    }
}
