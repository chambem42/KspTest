using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using KSPUITest;
using KSPUITest.RP1;

namespace KSPUITest.Tests.RP1
{
    public static class KerbalismTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// Kerbalism's ResourceUpdate path against RP-1's avionics: which modules in the install have a
        /// ResourceUpdate and how Kerbalism calls them (interface, typed delegate or MethodInfo.Invoke), and in orbit
        /// the electric charge Kerbalism books to the "avionics" broker against what the avionics modules ask for
        /// (their ecConsumption), with the avionics on, shut down and on again; plus the stored charge's fall
        /// against the sum of every broker's rate. Asserts, in every state, the avionics broker within 1 % of the request
        /// (of 0 when RP-1 leaves the draw to ModuleCommand) and the stored charge's fall within 1 % of the brokers. Writes avionics_power.csv (one row per sample).
        /// </summary>
        [UITest("Kerb_AvionicsPower", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 1200)]
        public static IEnumerator Kerb_AvionicsPower(TestContext ctx)
        {
            Ugui.DismissPopups();
            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double holdSec = ctx.ParamDouble("holdSec", 20);
            var inv = CultureInfo.InvariantCulture;

            Assembly kerb = AssemblyLoader.loadedAssemblies.FirstOrDefault(a => a.assembly.GetName().Name == "Kerbalism")?.assembly;
            Assert.NotNull(kerb, "Kerbalism loaded");
            ctx.Record("kerbalismDll", kerb.Location);
            Type iModule = kerb.GetType("KERBALISM.IKerbalismModule");
            Type rud = kerb.GetType("KERBALISM.ResourceUpdateDelegate");
            ctx.Record("typedDelegateField", rud?.GetField("method", All) != null);

            // every PartModule type in the install with a public instance ResourceUpdate: how it would be called
            ctx.Step("Modules with a ResourceUpdate");
            var sig = new[] { typeof(Dictionary<string, double>), typeof(List<KeyValuePair<string, double>>) };
            int types = 0, viaInterface = 0, typed = 0, invoke = 0;
            foreach (var la in AssemblyLoader.loadedAssemblies)
            {
                Type[] ts;
                try { ts = la.assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); }
                foreach (Type t in ts)
                {
                    if (t == null || t.IsAbstract || !typeof(PartModule).IsAssignableFrom(t))
                        continue;
                    MethodInfo m;
                    try { m = t.GetMethod("ResourceUpdate", BindingFlags.Instance | BindingFlags.Public); }
                    catch (AmbiguousMatchException) { ctx.Record("resourceUpdate_" + t.FullName, "ambiguous overloads"); continue; }
                    if (m == null)
                        continue;
                    ++types;
                    string how;
                    if (iModule != null && iModule.IsAssignableFrom(t)) { how = "interface"; ++viaInterface; }
                    else if (m.ReturnType == typeof(string) && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(sig)) { how = "typed delegate"; ++typed; }
                    else { how = "Invoke fallback"; ++invoke; }
                    ctx.Record("resourceUpdate_" + t.FullName,
                        $"{how}; {m.ReturnType.Name} ({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))}), declared by {m.DeclaringType?.Name}");
                }
            }
            ctx.Record("resourceUpdateTypes", types);
            ctx.Record("resourceUpdateViaInterface", viaInterface);
            ctx.Record("resourceUpdateTyped", typed);
            ctx.Record("resourceUpdateInvoke", invoke);

            ctx.Step($"Open {craft} and simulate a 300 km orbit");
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            // RP-1's AvionicsUseKerbalism (default off) decides whether the avionics draw goes through Kerbalism
            // (ResourceUpdate) or through ModuleCommand; avionicsKerbalism=on|off sets it before the vessel starts
            bool viaKerbalism = !(ctx.Param("avionicsKerbalism", "on") ?? "on").Equals("off", StringComparison.OrdinalIgnoreCase);
            HighLogic.CurrentGame.Parameters.CustomParams<RP0.RP0Settings>().AvionicsUseKerbalism = viaKerbalism;
            ctx.Record("avionicsUseKerbalism", viaKerbalism);
            yield return Rp1.ConfigureSim(SimSpec.Circular(300, 28.6));
            yield return Rp1.StartSim();
            Vessel v = FlightGlobals.ActiveVessel;
            Assert.NotNull(v, "vessel");
            CheatOptions.InfiniteElectricity = false;
            ctx.Record("crew", v.GetCrewCount());
            yield return WaitGame(5);

            var avionics = new List<RP0.ModuleAvionics>();
            foreach (Part p in v.parts)
                avionics.AddRange(p.FindModulesImplementing<RP0.ModuleAvionics>());
            ctx.Record("avionicsModules", string.Join(", ", avionics.Select(a => a.GetType().Name + (a.dead ? " (dead)" : ""))));
            Assert.True(avionics.Count > 0, "the craft has avionics");
            FieldInfo fEc = typeof(RP0.ModuleAvionics).GetField("ecConsumption", All);
            ctx.Record("useKerbalismInFlight", string.Join(", ", avionics.Select(a => a.useKerbalismInFlight)));

            // Kerbalism's list of the vessel's ResourceUpdate modules and how each is called
            object vd = kerb.GetType("KERBALISM.DB").GetMethod("KerbalismData", All, null, new[] { typeof(Vessel) }, null).Invoke(null, new object[] { v });
            var delegates = vd.GetType().GetField("resourceUpdateDelegates", All)?.GetValue(vd) as IList;
            Assert.NotNull(delegates, "VesselData.resourceUpdateDelegates built");
            var calls = new List<string>();
            foreach (object d in delegates)
            {
                var module = (PartModule)rud.GetField("module", All).GetValue(d);
                FieldInfo fm = rud.GetField("method", All);
                string how = iModule.IsAssignableFrom(module.GetType()) ? "interface"
                    : fm == null ? "Invoke (3.43)" : fm.GetValue(d) != null ? "typed delegate" : "Invoke fallback";
                calls.Add($"{module.part.partInfo.name}/{module.GetType().Name}: {how}");
            }
            ctx.Record("vesselResourceUpdates", string.Join("; ", calls));

            object supply = vd.GetType().GetMethod("Supply", All, null, new[] { typeof(string) }, null).Invoke(vd, new object[] { "ElectricCharge" });
            PropertyInfo pBrokers = supply.GetType().GetProperty("ResourceBrokers", All);
            Type rate = supply.GetType().GetNestedType("ResourceBrokerRate", All);
            FieldInfo fBroker = rate.GetField("broker", All), fRate = rate.GetField("rate", All);
            Type brokerType = kerb.GetType("KERBALISM.ResourceBroker");
            PropertyInfo pId = brokerType.GetProperty("Id", All);
            MethodInfo getResource = kerb.GetType("KERBALISM.ResourceCache").GetMethod("GetResource", All, null, new[] { typeof(Vessel), typeof(string) }, null);

            double Requested() => avionics.Sum(a => ((KeyValuePair<string, double>)fEc.GetValue(a)).Value);   // kW, negative
            double Broker(string id)
            {
                double r = 0;
                foreach (object br in (IList)pBrokers.GetValue(supply, null))
                    if ((string)pId.GetValue(fBroker.GetValue(br), null) == id)
                        r += (double)fRate.GetValue(br);
                return r;
            }
            double AllBrokers()
            {
                double r = 0;
                foreach (object br in (IList)pBrokers.GetValue(supply, null))
                    r += (double)fRate.GetValue(br);
                return r;
            }
            double EcAmount()
            {
                object ri = getResource.Invoke(null, new object[] { v, "ElectricCharge" });
                return (double)ri.GetType().GetProperty("Amount", All).GetValue(ri, null);
            }
            string BrokerList() => string.Join(", ", ((IList)pBrokers.GetValue(supply, null)).Cast<object>()
                .Select(br => $"{pId.GetValue(fBroker.GetValue(br), null)} {((double)fRate.GetValue(br)).ToString("G4", inv)}"));

            var csv = new StringBuilder("state,ut,requestedKW,avionicsBrokerRate,allBrokersRate,ecAmount\n");
            IEnumerator Hold(string state)
            {
                yield return WaitGame(3);   // let the brokers settle on the new request
                double ut0 = Planetarium.GetUniversalTime(), ec0 = EcAmount();
                double sumReq = 0, sumBroker = 0, sumAll = 0, worst = 0;
                int n = 0;
                double end = ut0 + holdSec;
                while (Planetarium.GetUniversalTime() < end)
                {
                    yield return new UnityEngine.WaitForFixedUpdate();
                    double req = Requested(), br = Broker("avionics"), all = AllBrokers();
                    sumReq += req; sumBroker += br; sumAll += all; ++n;
                    if (Math.Abs(req) > 1e-9)
                        worst = Math.Max(worst, Math.Abs(br - req) / Math.Abs(req));
                    else
                        worst = Math.Max(worst, Math.Abs(br));
                    if (n % 10 == 0)
                        csv.Append(string.Format(inv, "{0},{1:F2},{2:G6},{3:G6},{4:G6},{5:G8}\n", state, Planetarium.GetUniversalTime(), req, br, all, EcAmount()));
                }
                double dt = Planetarium.GetUniversalTime() - ut0, dEc = EcAmount() - ec0;
                double meanReq = sumReq / n, meanBroker = sumBroker / n, meanAll = sumAll / n;
                ctx.Record(state + ".requestedKW", Math.Round(meanReq, 6));
                ctx.Record(state + ".avionicsBroker", Math.Round(meanBroker, 6));
                ctx.Record(state + ".worstStepErrPct", Math.Round(100 * worst, 3));
                ctx.Record(state + ".allBrokers", Math.Round(meanAll, 6));
                ctx.Record(state + ".ecDropPerSec", Math.Round(dEc / dt, 6));
                ctx.Record(state + ".brokers", BrokerList());
                // through Kerbalism the avionics broker carries the request; through ModuleCommand it is in Others
                double expected = viaKerbalism ? meanReq : 0.0;
                ctx.Record(state + ".othersBroker", Math.Round(Broker("Others"), 6));
                Assert.True(Math.Abs(meanBroker - expected) <= Math.Max(1e-6, 0.01 * Math.Abs(meanReq)),
                    $"{state}: avionics broker {meanBroker:G6}, expected {expected:G6} (request {meanReq:G6})");
                Assert.True(Math.Abs(dEc / dt - meanAll) <= Math.Max(1e-4, 0.01 * Math.Abs(meanAll)),
                    $"{state}: stored charge fell {dEc / dt:G6}/s against the brokers' {meanAll:G6}/s");
            }

            ctx.Step("Avionics on");
            yield return Hold("on");

            var toggleable = avionics.Where(a => a.Events[nameof(RP0.ModuleAvionics.ToggleEvent)].active && a.Events[nameof(RP0.ModuleAvionics.ToggleEvent)].guiActive).ToList();
            ctx.Record("toggleable", toggleable.Count);
            if (toggleable.Count > 0)
            {
                ctx.Step("Avionics shut down");
                foreach (var a in toggleable)
                    a.ToggleEvent();
                ctx.Record("systemEnabledAfterShutdown", string.Join(", ", toggleable.Select(a => a.systemEnabled)));
                yield return Hold("off");

                ctx.Step("Avionics on again");
                foreach (var a in toggleable)
                    a.ToggleEvent();
                yield return Hold("onAgain");
                Assert.True(Math.Abs(Requested()) > 0, "drawing power again");
            }

            System.IO.Directory.CreateDirectory(ctx.ResultsDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(ctx.ResultsDir, "avionics_power.csv"), csv.ToString());
        }

        private static IEnumerator WaitGame(double sec)
        {
            double end = Planetarium.GetUniversalTime() + sec;
            while (Planetarium.GetUniversalTime() < end)
                yield return null;
        }
    }
}
