using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KspTest.ServePlugins
{
    /// <summary>
    /// FAR's editor window on its Components tab (a build with one, feat/components-tab), driven by reflection: shown, its
    /// inputs and tint set, its numbers read back.
    /// </summary>
    public static class FarComponentsCommands
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type FarType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        private static FieldInfo F(Type t, string name) => t?.GetField(name, Any);

        [ServeCommand("farcomponents", "[open=on|off] [tab=components|static|stability|simulation|transonic] [tint=None|Parts|AR|Wings|" +
                                       "Shielded|Cl|Cd|Cm] [whole=on|off] [speed=m/s] [alt=km] [aoa=deg] [flaps=0..3] [spoilers=on|off] " +
                                       "[table=on|off] [kct=off|on] [rows=N]: FAR's editor window shown on a tab, the Components tab's inputs set; its summary and " +
                                       "first rows read back")]
        public static IEnumerator FarComponents(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            Type gui = FarType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI");
            object inst = gui?.GetProperty("Instance", Any)?.GetValue(null, null);
            if (inst == null)
            {
                answer["error"] = "no FAR editor GUI";
                yield break;
            }

            if (q["open"] != null)
                F(gui, "showGUI").SetValue(null, q["open"] != "off");
            string[] tabs = {"static", "stability", "simulation", "transonic", "components"};
            if (q["tab"] != null)
            {
                object dd = F(gui, "modeDropdown").GetValue(inst);
                F(dd.GetType(), "selectedOption").SetValue(dd, Array.IndexOf(tabs, q["tab"].ToLowerInvariant()));
            }

            object comp = F(gui, "_components")?.GetValue(inst);
            if (comp == null)
            {
                answer["error"] = "this FAR has no Components tab";
                yield break;
            }

            Type ct = comp.GetType();
            if (q["whole"] != null)
            {
                FarType("FerramAerospaceResearch.FARDebugValues").GetField("wholeWingAspectRatio").SetValue(null, q["whole"] != "off");
                FarType("ferram4.FARWingInteraction").GetMethod("InvalidateWingGroups", Any).Invoke(null, null);
                F(ct, "dirty").SetValue(comp, true);
            }

            if (q["tint"] != null)
            {
                FieldInfo tf = F(ct, "tint");
                object tv = Enum.Parse(tf.FieldType, q["tint"] == "AR" ? "AspectRatio" : q["tint"], true);
                tf.SetValue(comp, tv);
                // a build with Color by as a dropdown: its selection too, or it puts its own back
                object tdd = F(ct, "tintDropdown")?.GetValue(comp);
                if (tdd != null)
                    F(tdd.GetType(), "selectedOption").SetValue(tdd, Array.IndexOf(Enum.GetValues(tf.FieldType), tv));
                F(ct, "dirty").SetValue(comp, true);
                // the window fits itself to the new legend (a build that refits on a Color by change)
                F(ct, "resized")?.SetValue(comp, true);
            }

            foreach (var (key, field) in new[] {("speed", "speed"), ("alt", "altitude"), ("aoa", "aoa")})
                if (q[key] != null)
                    F(ct, field).SetValue(comp, q[key]);
            if (q["spoilers"] != null)
                F(ct, "spoilers").SetValue(comp, q["spoilers"] == "on");
            // the table of parts shown or hidden (a build with the toggle)
            if (q["table"] != null)
            {
                F(ct, "showTable")?.SetValue(comp, q["table"] == "on");
                // the window fits itself to the tab again (a build that refits on a table toggle)
                F(ct, "resized")?.SetValue(comp, true);
            }

            // RP-1's Integration Info window closed (it covers the craft in screenshots); kct=on leaves it
            if (q["kct"] != "on")
            {
                object states = FarType("RP0.KCT_GUI")?.GetField("GUIStates", Any)?.GetValue(null);
                states?.GetType().GetField("ShowEditorGUI", Any)?.SetValue(states, false);
            }
            if (q["flaps"] != null)
            {
                object fd = F(ct, "flapDropdown").GetValue(comp);
                F(fd.GetType(), "selectedOption").SetValue(fd, int.Parse(q["flaps"]));
            }

            // a few frames for the window to draw and the tab to compute
            for (int i = 0; i < 20; i++)
                yield return null;

            // hover=on: every part highlighted and let go again, as the mouse passing over them does
            if (q["hover"] == "on")
            {
                foreach (Part p in EditorLogic.fetch.ship.parts)
                    p.SetHighlight(true, false);
                yield return null;
                foreach (Part p in EditorLogic.fetch.ship.parts)
                    p.SetHighlight(false, false);
                yield return null;
            }

            answer["summary"] = F(ct, "summary").GetValue(comp);
            answer["legend"] = F(ct, "legend").GetValue(comp);
            if (F(ct, "showTable") != null)
                answer["table"] = F(ct, "showTable").GetValue(comp);
            if (F(gui, "guiRect")?.GetValue(inst) is Rect rect)
                answer["window"] = new Dictionary<string, object>
                {
                    ["x"] = rect.x, ["y"] = rect.y, ["width"] = rect.width, ["height"] = rect.height
                };
            int n = int.TryParse(q["rows"], out int r) ? r : 12;
            var rows = new List<object>();
            foreach (object row in ((IEnumerable)F(ct, "rows").GetValue(comp)).Cast<object>().Take(n))
            {
                Type rt = row.GetType();
                var d = new Dictionary<string, object>();
                foreach (string f in new[] {"Name", "S", "OwnAR", "EffectiveAR", "Group", "Shielded", "Lift", "Drag", "Moment", "Cl", "Cd", "Cm"})
                {
                    // a build without wing groups has no Group
                    if (F(rt, f) == null)
                        continue;
                    object v = F(rt, f).GetValue(row);
                    d[f] = v is double x ? Math.Round(x, 4) : v;
                }
                rows.Add(d);
            }

            answer["rows"] = rows;
            yield break;
        }
    }
}
