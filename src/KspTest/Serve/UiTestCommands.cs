using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;

namespace KspTest
{
    /// <summary>
    /// A [UITest] run from a Serve session: one being written in tools/ServePlugins runs as soon as serve-plugin loads it (the
    /// newest plugin's copy of a test wins), and a deployed one runs without a new session. The test is brought to its StartsAt
    /// the way the runner does, with the request's other values as its params.
    /// </summary>
    public static class UiTestCommands
    {
        [ServeCommand("uitest", "name=TEST [k=v ...]: a [UITest] (the newest plugin's copy first, then the deployed tests) brought to its StartsAt " +
                                "(craft=, craftDir=, launchSite= override its attribute) and run, the other values its params; the answer has " +
                                "status pass or skipped and the values it recorded (a failure answers error, with what it recorded so far)")]
        public static IEnumerator UiTest(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string name = q["name"] ?? throw new ArgumentException("name= needed");
            (MethodInfo method, UITestAttribute attr) = Find(name);
            var config = new RunConfig
            {
                Test = name, Save = ctx.Config.Save, NewGame = ctx.Config.NewGame, ResultsDir = ctx.Config.ResultsDir, TimeoutSec = ctx.Config.TimeoutSec,
            };
            // the request's values only: the session's own params (its craft=, port=) are not the test's
            foreach (string k in q.AllKeys)
                if (k != null && k != "name") config.Params[k] = q[k];
            var t = new TestContext(config, attr.Name ?? method.Name);
            answer["test"] = method.DeclaringType.FullName + "." + method.Name;
            answer["assembly"] = method.DeclaringType.Assembly.GetName().Name;
            answer["values"] = t.Values;   // filled as it runs, so a failure's answer still has them

            var missing = Env.Missing(attr.Requires);
            if (missing.Count > 0)
            {
                answer["status"] = "skipped";
                answer["message"] = $"requires {string.Join(", ", missing)} which is not loaded";
                yield break;
            }
            t.StartsAt = attr.StartsAt;
            t.LaunchSite = t.Param("launchSite", attr.LaunchSite);
            if (attr.StartsAt == StartState.Vab || attr.StartsAt == StartState.Sph || attr.StartsAt == StartState.Flight)
            {
                string craft = t.Param("craft", attr.Craft) ?? throw new ArgumentException($"{name} starts at {attr.StartsAt}: craft= needed");
                t.Craft = t.CraftPath(craft, t.Param("craftDir", attr.CraftDir ?? (attr.StartsAt == StartState.Sph ? "SPH" : null)));
            }
            var error = new Exception[1];
            yield return Pump(Nav.Restart(t), error);
            if (error[0] == null)
            {
                IEnumerator body = null;
                try { body = (IEnumerator)method.Invoke(null, method.GetParameters().Length == 0 ? new object[0] : new object[] { t }); }
                catch (Exception e) { error[0] = Unwrap(e); }
                if (body != null) yield return Pump(body, error);
            }
            // as the runner after every test
            Ctrl.Detach();
            Paw.CloseAll();
            if (error[0] == null) { answer["status"] = "pass"; yield break; }
            answer["status"] = error[0] is UITestFailure ? "fail" : "crash";
            answer["message"] = error[0].Message;
            if (!(error[0] is UITestFailure)) answer["stack"] = error[0].StackTrace;
            answer["step"] = t.Steps.LastOrDefault()?.Name;
        }

        /// <summary>
        /// Runs a coroutine and the ones it yields, passing its waits up, until it ends or throws (error[0]; the coroutines
        /// disposed, so their finally blocks run): a failure ends the request with an answer rather than an error.
        /// </summary>
        private static IEnumerator Pump(IEnumerator root, Exception[] error)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                bool moved;
                try { moved = stack.Peek().MoveNext(); }
                catch (Exception e)
                {
                    error[0] = Unwrap(e);
                    while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                    yield break;
                }
                if (!moved) { stack.Pop(); continue; }
                object current = stack.Peek().Current;
                if (current is IEnumerator nested) stack.Push(nested);
                else yield return current;
            }
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static (MethodInfo, UITestAttribute) Find(string name)
        {
            // AppDomain order is load order: the newest plugin last
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies().Reverse())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                foreach (Type type in types)
                    foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.GetCustomAttributes(typeof(UITestAttribute), false).FirstOrDefault() is UITestAttribute attr &&
                            (string.Equals(attr.Name ?? m.Name, name, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(type.FullName + "." + m.Name, name, StringComparison.OrdinalIgnoreCase)))
                            return (m, attr);
            }
            throw new ArgumentException($"no [UITest] named {name} in this session");
        }
    }
}
