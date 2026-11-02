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
    /// FAR's shielding of the editor craft's control surfaces, by reflection: each one's exposed area factors, deflection and
    /// shielded flag, before and after one step of FAR's instant editor sim (as the stability tab and far-vlm run it), and
    /// the sim's Cl.
    /// </summary>
    public static class FarShieldCommands
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static object Get(object o, string name)
        {
            if (o == null)
                return null;
            for (Type t = o is Type st ? st : o.GetType(); t != null; t = t.BaseType)
            {
                object target = o is Type ? null : o;
                FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (f != null)
                    return f.GetValue(target);
                PropertyInfo p = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0)
                    return p.GetValue(target, null);
            }
            return null;
        }

        private static Type FindType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        private static string Id(Part p) => p == null ? null : p.partInfo.name + "_" + p.craftID;

        private static List<object> Rows(ShipConstruct ship) =>
            ship.parts.Select(p => p.Modules.Cast<PartModule>().FirstOrDefault(m => m.GetType().Name == "FARControllableSurface"))
                .Where(m => m != null)
                .Select(m => (object)new Dictionary<string, object>
                {
                    ["id"] = Id(m.part),
                    ["S"] = Get(m, "S"),
                    ["exposedMin"] = Get(m, "NUFAR_areaExposedFactor"),
                    ["exposedMax"] = Get(m, "NUFAR_totalExposedAreaFactor"),
                    ["AoAoffset"] = Get(m, "AoAoffset"),
                    ["shielded"] = Get(m, "isShielded"),
                    ["Cl"] = Get(m, "Cl"),
                })
                .ToList();

        [ServeCommand("farshield", "[aoa=4] [mach=0.5] [spoilers=0]: the control surfaces' shielding before and after one FAR " +
                                   "instant editor sim step, and its Cl")]
        public static IEnumerator FarShield(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            var ship = EditorLogic.fetch?.ship;
            if (ship == null)
            {
                answer["error"] = "no editor craft";
                yield break;
            }

            answer["before"] = Rows(ship);
            object gui = Get(FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.EditorGUI"), "Instance");
            object sim = Get(gui, "_instantSim");
            Type inType = FindType("FerramAerospaceResearch.FARGUI.FAREditorGUI.Simulation.InstantConditionSimInput");
            object input = Activator.CreateInstance(inType);
            void Set(string n, object v) => inType.GetField(n, Any)?.SetValue(input, v);
            Set("alpha", double.Parse(q["aoa"] ?? "4"));
            Set("machNumber", double.Parse(q["mach"] ?? "0.5"));
            Set("spoilers", q["spoilers"] == "1");
            object[] args = { input, null, true, false };
            sim.GetType().GetMethod("GetClCdCmSteady", Any).Invoke(sim, args);
            answer["Cl"] = Get(args[1], "Cl");
            answer["after"] = Rows(ship);
            answer["farAssembly"] = sim.GetType().Assembly.Location;
            yield break;
        }
    }
}
