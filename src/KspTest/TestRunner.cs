using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// The harness entry point. Instantiated once at game start; inert unless
    /// GameData/KspTest/PluginData/run.cfg exists. Discovers [UITest] methods in every loaded
    /// assembly, runs the requested ones one after another in this game session (each pumped by
    /// hand so exceptions are caught; Nav.EnsureState brings the game to each test's declared
    /// StartsAt from wherever the previous test left it), enforces timeouts, writes result.json
    /// and quits the game.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class TestRunner : MonoBehaviour
    {
        public static TestRunner Instance { get; private set; }
        public static RunConfig Config { get; private set; }
        public static TestContext Context { get; private set; }

        /// <summary>Extra diagnostics dumped on failure; drivers (Imgui etc.) register themselves here.</summary>
        public static readonly List<Func<string>> DiagnosticsProviders = new List<Func<string>>();
        /// <summary>Run after every test (pass or fail) to undo anything that would leak into the next one.</summary>
        public static readonly List<Action> CleanupHooks = new List<Action>();

        public class TestInfo
        {
            public string Name;
            public UITestAttribute Attr;
            public MethodInfo Method;
        }

        private class TestResult
        {
            public string Name;
            public string Status = "running";
            public string Message = "";
            public float StartedAt;
            public float EndedAt;
            public TestContext Ctx;
            public bool TimedOut;
        }

        private static bool _ran;
        private static bool _started;
        private readonly List<TestResult> _results = new List<TestResult>();
        private TestResult _current;
        private float _runDeadline;
        private float _testDeadline;
        private bool _finished;
        private float _startedAt;

        private void Awake()
        {
            // KSP instantiates Instantly addons more than once; Destroy() is deferred, so a plain Destroy
            // would still let the duplicate's Start() run. Kill it now and never let it start.
            if (_ran) { DestroyImmediate(this); return; }
            Config = RunConfig.TryLoad();
            if (Config == null) { DestroyImmediate(this); return; }   // no manifest: normal play, harness inert
            _ran = true;
            Instance = this;
            DontDestroyOnLoad(gameObject);

            Log.Open(Config.ResultsDir);
            WaypointProbe.HookAdds();   // before any scene adds waypoints
            Log.Info($"BEGIN tests='{Config.Test}' save='{Config.Save}' timeout={Config.TimeoutSec}s results='{Config.ResultsDir}'");
            Log.Info($"KSP {Versioning.GetVersionStringFull()} root='{KSPUtil.ApplicationRootPath}'");
            _startedAt = Time.realtimeSinceStartup;
            _runDeadline = _startedAt + (float)Config.TimeoutSec;
            _testDeadline = float.MaxValue;
            ApplyResolution();
        }

        private void Start()
        {
            if (Instance != this || _started) return;
            _started = true;
            StartCoroutine(RunAll());
        }

        private void Update()
        {
            if (_finished) return;
            float now = Time.realtimeSinceStartup;
            if (_current != null && !_current.TimedOut && now > _testDeadline)
            {
                _current.TimedOut = true;   // the pump notices and aborts the test
                Log.Error($"test '{_current.Name}' timed out");
            }
            if (now > _runDeadline)
            {
                StartCoroutine(FinishRun($"overall timeout of {Config.TimeoutSec}s exceeded in scene {HighLogic.LoadedScene}"));
            }
        }

        // ---- on-screen status: which test/step is running and what it is waiting for ----

        private GUIStyle _overlayStyle;
        private int _planCount;
        private int _planIndex;

        /// <summary>Hides the on-screen status (for screen recordings).</summary>
        public static bool HideOverlay;

        private void OnGUI()
        {
            if (HideOverlay)
                return;
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 16,
                    richText = true,
                    wordWrap = true,
                    normal = { textColor = Color.white, background = Texture2D.whiteTexture },
                };
            }
            var sb = new StringBuilder();
            if (_finished)
            {
                sb.Append("<b>KspTest</b>  finished: ").Append(OverallStatus().ToUpper());
            }
            else if (_current == null)
            {
                sb.Append("<b>KspTest</b>  loading / between tests");
            }
            else
            {
                sb.Append($"<b>KspTest</b>  test {_planIndex}/{_planCount}: <b>{_current.Name}</b>  ({Time.realtimeSinceStartup - _current.StartedAt:F0} s)");
                if (TestContext.ScenarioStatus != null) sb.Append("   ").Append(TestContext.ScenarioStatus);
                sb.Append("\nstep: ").Append(LastStep(_current));
                if (Wait.Current != null)
                    sb.Append($"\nwaiting for: {Wait.Current}  ({Time.realtimeSinceStartup - Wait.CurrentSince:F0}/{Wait.CurrentTimeout:F0} s)");
            }
            // Background 30% opaque, text solid: it sits over the stock UI's bottom-left corner (the map view's orbit
            // info), which has to stay readable through it.
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0f, 0f, 0f, 0.3f);
            GUI.Box(new Rect(8, Screen.height - 96, Mathf.Min(900, Screen.width - 16), 88), sb.ToString(), _overlayStyle);
            GUI.backgroundColor = prev;
        }

        private void ApplyResolution()
        {
            if (Config.Width <= 0 || Config.Height <= 0) return;
            if (Screen.width == Config.Width && Screen.height == Config.Height && !Screen.fullScreen) return;
            Log.Info($"setting resolution {Config.Width}x{Config.Height} windowed (was {Screen.width}x{Screen.height} fullscreen={Screen.fullScreen})");
            Screen.SetResolution(Config.Width, Config.Height, false);
        }

        // Not named "Main": Unity invokes a MonoBehaviour method of that name itself (legacy callback).
        private IEnumerator RunAll()
        {
            if (QuickStartIsActive())
            {
                AddResult("(run)", "crash", "QuickStart is enabled in GameData/QuickMods/QuickStart/Config.txt; it would race the harness. Disable it for the run.");
                yield return FinishRun(null);
                yield break;
            }

            var tests = DiscoverTests();
            if (Config.Tests.Count == 1 && string.Equals(Config.Tests[0], "list", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                foreach (var t in tests.Values)
                    sb.Append(t.Name).Append("  starts=").Append(t.Attr.StartsAt)
                      .Append(t.Attr.Craft != null ? " craft=" + t.Attr.Craft : "")
                      .Append(t.Attr.Requires != null ? "  [requires " + t.Attr.Requires + "]" : "").Append('\n');
                var r = AddResult("list", "pass", $"{tests.Count} tests available");
                r.Ctx = new TestContext(Config, "list");
                r.Ctx.Record("tests", sb.ToString());
                yield return FinishRun(null);
                yield break;
            }

            var plan = new List<TestInfo>();
            foreach (var name in Config.Tests)
            {
                if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase)) { plan.AddRange(tests.Values); continue; }
                if (name.Contains("*"))
                {
                    var re = new Regex("^" + Regex.Escape(name).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
                    foreach (var t in tests.Values) if (re.IsMatch(t.Name)) plan.Add(t);
                    continue;
                }
                if (tests.TryGetValue(name, out var info)) plan.Add(info);
                else AddResult(name, "crash", $"test '{name}' not found; available: {string.Join(", ", tests.Keys)}");
            }
            Log.Info($"plan: {string.Join(", ", plan.ConvertAll(t => t.Name))}");

            // Wait for the game to reach the main menu before handing control to tests; a second
            // resolution pass because KSP re-applies settings.cfg during its own startup.
            yield return Wait.Scene(GameScenes.MAINMENU, 900);
            ApplyResolution();

            _planCount = plan.Count;
            foreach (var info in plan)
            {
                _planIndex++;
                yield return RunOne(info);
                if (_finished) yield break;
            }
            yield return FinishRun(null);
        }

        private IEnumerator RunOne(TestInfo info)
        {
            var result = new TestResult { Name = info.Name, StartedAt = Time.realtimeSinceStartup };
            _results.Add(result);
            _current = result;
            TestContext.ScenarioStatus = null;
            Context = result.Ctx = new TestContext(Config, info.Name);
            Snap.WaitShots = (Context.Param("waitShots", "on") ?? "on").ToLowerInvariant() != "off";
            double timeout = info.Attr.TimeoutSec > 0 ? info.Attr.TimeoutSec : Config.TimeoutSec;
            _testDeadline = Time.realtimeSinceStartup + (float)timeout;
            Log.Info($"==== TEST {info.Name} (starts at {info.Attr.StartsAt}, timeout {timeout:F0}s) ====");

            var missing = Env.Missing(info.Attr.Requires);
            if (missing.Count > 0)
            {
                yield return EndTest(result, "skipped", $"requires {string.Join(", ", missing)} which is not loaded");
                yield break;
            }

            // precondition: get the game to the declared start state
            string craft = null;
            if (info.Attr.StartsAt == StartState.Vab || info.Attr.StartsAt == StartState.Sph || info.Attr.StartsAt == StartState.Flight)
            {
                string craftName = Context.Param("craft", info.Attr.Craft);
                string craftDir = Context.Param("craftDir", info.Attr.CraftDir ?? (info.Attr.StartsAt == StartState.Sph ? "SPH" : "VAB"));
                if (craftName == null)
                {
                    yield return EndTest(result, "crash", $"StartsAt {info.Attr.StartsAt} but no Craft on the attribute and no 'craft' param");
                    yield break;
                }
                craft = Context.CraftPath(craftName, craftDir);
            }
            string outcome = null;
            yield return Pump(Nav.EnsureState(Context, info.Attr.StartsAt, craft, Context.Param("launchSite", info.Attr.LaunchSite)), result, s => outcome = s);
            if (outcome != null)
            {
                yield return EndTest(result, outcome == "timeout" ? "timeout" : "fail", "precondition: " + result.Message);
                yield break;
            }

            IEnumerator body = null;
            Exception startError = null;
            try
            {
                var args = info.Method.GetParameters().Length == 0 ? new object[0] : new object[] { Context };
                body = (IEnumerator)info.Method.Invoke(null, args);
            }
            catch (Exception e) { startError = Unwrap(e); }
            if (startError != null)
            {
                yield return EndTest(result, "crash", "could not start test: " + startError);
                yield break;
            }

            yield return Pump(body, result, s => outcome = s);
            yield return EndTest(result, outcome ?? "pass", result.Message);
        }

        /// <summary>
        /// Runs an enumerator (and any nested IEnumerators it yields) with exception capture and the
        /// per-test timeout. Calls done(null) on success, done(status) with result.Message set on failure.
        /// </summary>
        private IEnumerator Pump(IEnumerator root, TestResult result, Action<string> done)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (result.TimedOut)
                {
                    DisposeAll(stack);
                    result.Message = $"timed out in scene {HighLogic.LoadedScene} at step '{LastStep(result)}'";
                    done("timeout");
                    yield break;
                }
                var top = stack.Peek();
                bool moved = false;
                Exception error = null;
                try { moved = top.MoveNext(); }
                catch (Exception e) { error = Unwrap(e); }
                if (error != null)
                {
                    DisposeAll(stack);   // runs finally blocks (drivers clear pending state there)
                    result.Message = $"{error.GetType().Name}: {error.Message}\n{error.StackTrace}";
                    done(error is UITestFailure ? "fail" : "crash");
                    yield break;
                }
                if (!moved) { stack.Pop(); continue; }

                var cur = top.Current;
                if (cur is IEnumerator nested) { stack.Push(nested); continue; }
                yield return cur;   // null / YieldInstruction / CustomYieldInstruction pass through to Unity
            }
            done(null);
        }

        private static void DisposeAll(Stack<IEnumerator> stack)
        {
            while (stack.Count > 0)
                try { (stack.Pop() as IDisposable)?.Dispose(); } catch { }
        }

        private static string LastStep(TestResult r)
        {
            var steps = r.Ctx?.Steps;
            return steps != null && steps.Count > 0 ? steps[steps.Count - 1].Name : "(none)";
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
            return e;
        }

        private IEnumerator EndTest(TestResult result, string status, string message)
        {
            result.Status = status;
            result.Message = message ?? "";
            result.EndedAt = Time.realtimeSinceStartup;
            result.Ctx?.CloseStep(status == "pass" ? "pass" : status);
            if (status == "pass") Log.Info($"RESULT {result.Name}: pass");
            else Log.Error($"RESULT {result.Name}: {status}: {message}");

            if (status != "skipped" && result.Ctx != null)
            {
                IEnumerator shot = null;
                try { shot = Snap.Shot(result.Ctx, "end-" + status); } catch (Exception e) { Log.Warn("screenshot failed: " + e.Message); }
                if (shot != null) yield return shot;
                if (status != "pass") WriteDiagnostics(result);
            }

            // undo per-test state so the next test starts from a known place
            try { Ctrl.Detach(); } catch { }
            try { Paw.CloseAll(); } catch { }
            try { if (HighLogic.LoadedSceneIsFlight) { Flight.WarpOff(); Flight.Unpause(); } } catch { }
            try { Flight.IgnoreMaxTemperature(false); Flight.InfinitePropellant(false); } catch { }
            foreach (var h in CleanupHooks)
                try { h(); } catch (Exception e) { Log.Warn("cleanup hook failed: " + e.Message); }

            _current = null;
            Context = null;
            TestContext.ScenarioStatus = null;
            _testDeadline = float.MaxValue;
            WriteResult();   // keep result.json current in case the game dies later
        }

        private TestResult AddResult(string name, string status, string message)
        {
            var r = new TestResult { Name = name, Status = status, Message = message, StartedAt = Time.realtimeSinceStartup, EndedAt = Time.realtimeSinceStartup };
            _results.Add(r);
            if (status == "pass") Log.Info($"RESULT {name}: pass"); else Log.Error($"RESULT {name}: {status}: {message}");
            return r;
        }

        private IEnumerator FinishRun(string abortMessage)
        {
            if (_finished) yield break;
            _finished = true;
            if (abortMessage != null)
            {
                if (_current != null && _current.Status == "running")
                {
                    _current.Status = "timeout";
                    _current.Message = abortMessage;
                    _current.EndedAt = Time.realtimeSinceStartup;
                    WriteDiagnostics(_current);
                }
                else AddResult("(run)", "timeout", abortMessage);
            }
            WriteResult();
            Log.Info("END " + OverallStatus());
            Log.Close();

            if (Config.QuitWhenDone)
            {
                yield return new WaitForSecondsRealtime(1.5f);
                Application.Quit();
            }
        }

        private string OverallStatus()
        {
            bool any = false;
            foreach (var r in _results)
            {
                if (r.Status == "pass" || r.Status == "skipped") continue;
                any = true;
                if (r.Status == "timeout") return "timeout";
            }
            return any ? "fail" : "pass";
        }

        private void WriteDiagnostics(TestResult result)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("test: ").Append(result.Name).Append('\n');
                sb.Append("scene: ").Append(Nav.Describe()).Append('\n');
                sb.Append("input locks: ");
                foreach (var kv in InputLockManager.lockStack) sb.Append(kv.Key).Append(' ');
                sb.Append('\n');
                sb.Append(Ugui.Dump());
                foreach (var p in DiagnosticsProviders)
                {
                    try { sb.Append(p()).Append('\n'); }
                    catch (Exception e) { sb.Append("diagnostics provider failed: ").Append(e.Message).Append('\n'); }
                }
                sb.Append("\n--- KSP.log tail ---\n").Append(KspLogTail(200));
                File.WriteAllText(Path.Combine(Config.ResultsDir, $"diagnostics-{result.Name}.txt"), sb.ToString());
            }
            catch (Exception e)
            {
                Log.Warn("could not write diagnostics: " + e.Message);
            }
        }

        private static string KspLogTail(int lines)
        {
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "KSP.log");
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    var q = new Queue<string>(lines + 1);
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        q.Enqueue(line);
                        if (q.Count > lines) q.Dequeue();
                    }
                    return string.Join("\n", q.ToArray());
                }
            }
            catch (Exception e)
            {
                return "(KSP.log unreadable: " + e.Message + ")";
            }
        }

        private void WriteResult()
        {
            try
            {
                var tests = new List<object>();
                foreach (var r in _results)
                {
                    var steps = new List<object>();
                    if (r.Ctx != null)
                        foreach (var s in r.Ctx.Steps)
                            steps.Add(new Dictionary<string, object>
                            {
                                { "name", s.Name },
                                { "status", s.Status },
                                { "sec", Math.Round((s.EndedAt > 0 ? s.EndedAt : Time.realtimeSinceStartup) - s.StartedAt, 1) },
                            });
                    tests.Add(new Dictionary<string, object>
                    {
                        { "test", r.Name },
                        { "status", r.Status },
                        { "message", r.Message },
                        { "durationSec", Math.Round((r.EndedAt > 0 ? r.EndedAt : Time.realtimeSinceStartup) - r.StartedAt, 1) },
                        { "steps", steps },
                        { "values", r.Ctx?.Values ?? new Dictionary<string, object>() },
                        { "screenshots", r.Ctx?.Screenshots ?? new List<string>() },
                    });
                }
                var result = new Dictionary<string, object>
                {
                    { "status", _finished ? OverallStatus() : "running" },
                    { "durationSec", Math.Round(Time.realtimeSinceStartup - _startedAt, 1) },
                    { "scene", Nav.Describe() },
                    { "tests", tests },
                    { "env", new Dictionary<string, object>
                        {
                            { "ksp", Versioning.GetVersionStringFull() },
                            { "rp1", Env.HasRP1 },
                            { "mechjeb", Env.HasMechJeb },
                            { "far", Env.HasFAR },
                        }
                    },
                };
                Directory.CreateDirectory(Config.ResultsDir);
                File.WriteAllText(Path.Combine(Config.ResultsDir, "result.json"), Json.Write(result));
            }
            catch (Exception e)
            {
                Log.Error("could not write result.json: " + e);
            }
        }

        private static bool QuickStartIsActive()
        {
            if (!Env.HasAssembly("QuickStart")) return false;
            string cfg = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "QuickMods", "QuickStart", "Config.txt");
            if (!File.Exists(cfg)) return true;   // present with unknown config: assume its default (enabled)
            foreach (var line in File.ReadAllLines(cfg))
            {
                var t = line.Trim();
                if (t.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase))
                    return t.EndsWith("True", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        public static Dictionary<string, TestInfo> DiscoverTests()
        {
            var tests = new Dictionary<string, TestInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var la in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try { types = la.assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (var t in types)
                {
                    if (t == null) continue;
                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static); }
                    catch (Exception) { continue; }
                    foreach (var m in methods)
                    {
                        var attr = m.GetCustomAttribute<UITestAttribute>();
                        if (attr == null) continue;
                        if (!typeof(IEnumerator).IsAssignableFrom(m.ReturnType))
                        {
                            Log.Warn($"[UITest] {t.FullName}.{m.Name} ignored: must return IEnumerator");
                            continue;
                        }
                        var ps = m.GetParameters();
                        if (ps.Length > 1 || (ps.Length == 1 && ps[0].ParameterType != typeof(TestContext)))
                        {
                            Log.Warn($"[UITest] {t.FullName}.{m.Name} ignored: parameters must be () or (TestContext)");
                            continue;
                        }
                        string name = attr.Name ?? m.Name;
                        if (tests.ContainsKey(name))
                            Log.Warn($"[UITest] duplicate test name '{name}' in {t.FullName}; keeping the first");
                        else
                            tests[name] = new TestInfo { Name = name, Attr = attr, Method = m };
                    }
                }
            }
            Log.Info($"discovered {tests.Count} tests: {string.Join(", ", tests.Keys)}");
            return tests;
        }
    }
}
