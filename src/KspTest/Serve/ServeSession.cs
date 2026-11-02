using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

namespace KspTest
{
    /// <summary>A Serve request: a public static method (TestContext, query, answer) -> IEnumerator.</summary>
    public delegate IEnumerator ServeHandler(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer);

    /// <summary>
    /// Marks a public static <see cref="ServeHandler"/>-shaped method as a Serve request named Name. Methods so marked in
    /// the harness, its modules and the test assemblies deployed with it are the session's requests; a DLL loaded with
    /// /plugin adds or replaces requests the same way, without a game restart (ksptest serve-plugin).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ServeCommandAttribute : Attribute
    {
        public string Name { get; }
        public string Help { get; }
        public ServeCommandAttribute(string name, string help = "") { Name = name; Help = help; }
    }

    /// <summary>
    /// A KSP session that stays up and answers HTTP requests on 127.0.0.1 (param port, default 8642), so a craft can be
    /// reloaded, shot, measured, flown and tweaked over and over without a game start each time. Each request runs on the
    /// main thread in turn and answers JSON when it is done (an error answers {"error": ...} and the session carries on).
    /// GET or POST, arguments in the query string; /help lists the requests. Built in: status, load, reload, shots, shot,
    /// parts, field, save, get, set, call, plugin, help, quit, and for craft work catalog, gear, pitchlimit, closeup and
    /// takeoff; KspTest.Far adds far, farsections, farlat and dash, KspTest.AtmosphereAutopilot fly and aafly. The harness
    /// log gets a line a minute so the runner's freeze watchdog leaves an idle session alone. ksptest serve is a client.
    /// </summary>
    public static class ServeSession
    {
        private class Request
        {
            public string Path;
            public NameValueCollection Query;
            public object Result;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        private class Command
        {
            public ServeHandler Handler;
            public string Help;
            public string From;
        }

        /// <summary>
        /// What the editor needs before a loaded craft can be measured, by name (KspTest.Far adds "FAR editor sim"): load and
        /// reload wait for each, status says whether each is ready.
        /// </summary>
        public static readonly List<KeyValuePair<string, Func<bool>>> EditorReady = new List<KeyValuePair<string, Func<bool>>>();

        private static readonly Queue<Request> Pending = new Queue<Request>();
        private static readonly Dictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _quit;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// Serves requests until /quit. Params: port (8642); craft (and craftDir, SPH by default): a craft to load into its
        /// editor first, else the session starts at the Space Center.
        /// </summary>
        [UITest(StartsAt = StartState.Any, TimeoutSec = 43200, Explicit = true)]
        public static IEnumerator Serve(TestContext ctx)
        {
            int port = (int)ctx.ParamDouble("port", 8642);
            Commands.Clear();
            foreach (var la in AssemblyLoader.loadedAssemblies)
                if (la.assembly == typeof(ServeSession).Assembly || la.assembly.GetReferencedAssemblies().Any(n => n.Name == "KspTest"))
                    Register(la.assembly);
            if (ctx.Param("craft") != null)
                yield return LoadPath(ctx, ctx.CraftPath(ctx.Param("craft"), ctx.Param("craftDir") ?? "SPH"), new Dictionary<string, object>());
            else
                yield return Nav.EnsureState(ctx, StartState.SpaceCenter);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _quit = false;
            var thread = new Thread(() => Listen(listener)) { IsBackground = true, Name = "KspTest.Serve" };
            thread.Start();
            ctx.Step($"Serving on http://127.0.0.1:{port}/");
            Log.Info($"SERVE listening on http://127.0.0.1:{port}/, requests: {string.Join(", ", Commands.Keys.OrderBy(k => k))}");
            Ugui.DismissPopups();
            float heartbeat = Time.realtimeSinceStartup;
            int served = 0;
            try
            {
                while (!_quit)
                {
                    if (Time.realtimeSinceStartup - heartbeat > 60)
                    {
                        heartbeat = Time.realtimeSinceStartup;
                        Log.Info($"SERVE idle, {served} requests so far");
                    }
                    Request r = null;
                    lock (Pending)
                        if (Pending.Count > 0) r = Pending.Dequeue();
                    if (r == null)
                    {
                        yield return null;
                        continue;
                    }
                    Log.Info("SERVE " + r.Path + (r.Query.Count > 0 ? "?" + string.Join("&", r.Query.AllKeys.Select(k => k + "=" + r.Query[k])) : ""));
                    var answer = new Dictionary<string, object>();
                    Exception error = null;
                    // the handler's coroutine pumped here, so that its exception answers the request instead of ending the session
                    var stack = new Stack<IEnumerator>();
                    try { stack.Push(Dispatch(ctx, r, answer)); }
                    catch (Exception e) { error = Unwrap(e); }
                    while (error == null && stack.Count > 0)
                    {
                        var top = stack.Peek();
                        bool moved = false;
                        try { moved = top.MoveNext(); }
                        catch (Exception e) { error = Unwrap(e); }
                        if (error != null) break;
                        if (!moved) { stack.Pop(); continue; }
                        if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yield return top.Current;
                    }
                    if (error != null)
                    {
                        answer.Clear();
                        answer["error"] = $"{error.GetType().Name}: {error.Message}";
                        answer["stack"] = error.StackTrace;
                        Log.Warn("SERVE error: " + error);
                    }
                    r.Result = answer;
                    r.Done.Set();
                    served++;
                    heartbeat = Time.realtimeSinceStartup;
                }
            }
            finally
            {
                _quit = true;
                try { listener.Stop(); } catch { }
                lock (Pending)
                    while (Pending.Count > 0)
                    {
                        var r = Pending.Dequeue();
                        r.Result = new Dictionary<string, object> { ["error"] = "session ended" };
                        r.Done.Set();
                    }
            }
            ctx.Record("served", served);
        }

        private static Exception Unwrap(Exception e) =>
            e is TargetInvocationException t && t.InnerException != null ? Unwrap(t.InnerException) : e;

        // the listener's thread: each request queued for the main thread, its answer written when that is done
        private static void Listen(HttpListener listener)
        {
            while (!_quit && listener.IsListening)
            {
                HttpListenerContext hc;
                try { hc = listener.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var r = new Request { Path = hc.Request.Url.AbsolutePath.Trim('/'), Query = hc.Request.QueryString };
                    lock (Pending) Pending.Enqueue(r);
                    object result = r.Done.WaitOne(TimeSpan.FromMinutes(30)) ? r.Result
                        : new Dictionary<string, object> { ["error"] = "timed out waiting for the main thread" };
                    try
                    {
                        byte[] body = Encoding.UTF8.GetBytes(Json.Write(result));
                        hc.Response.ContentType = "application/json";
                        hc.Response.ContentLength64 = body.Length;
                        hc.Response.OutputStream.Write(body, 0, body.Length);
                        hc.Response.OutputStream.Close();
                    }
                    catch { }
                });
            }
        }

        private static IEnumerator Dispatch(TestContext ctx, Request r, Dictionary<string, object> answer)
        {
            string name = r.Path == "" ? "status" : r.Path;
            if (!Commands.TryGetValue(name, out Command c))
                throw new ArgumentException($"unknown request '{name}' (known: {string.Join(", ", Commands.Keys.OrderBy(k => k))})");
            return c.Handler(ctx, r.Query, answer);
        }

        /// <summary>Every [ServeCommand] method of an assembly registered (a later one of the same name replaces the earlier).</summary>
        private static List<string> Register(Assembly a)
        {
            var names = new List<string>();
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (Type t in types)
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var attr = m.GetCustomAttributes(false).FirstOrDefault(x => x.GetType().Name == nameof(ServeCommandAttribute));
                    if (attr == null) continue;
                    // by name, not type: a plugin built against an older copy of the harness still registers
                    string cmd = (string)attr.GetType().GetProperty("Name").GetValue(attr, null);
                    string help = (string)attr.GetType().GetProperty("Help").GetValue(attr, null);
                    var h = (ServeHandler)Delegate.CreateDelegate(typeof(ServeHandler), m);
                    Commands[cmd] = new Command { Handler = h, Help = help, From = a.GetName().Name };
                    names.Add(cmd);
                }
            return names;
        }

        // ---------------------------------------------------------------- the built-in requests

        [ServeCommand("help", "the requests and what they take")]
        public static IEnumerator Help(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            answer["requests"] = Commands.OrderBy(kv => kv.Key)
                                         .ToDictionary(kv => kv.Key, kv => (object)(kv.Value.Help + (kv.Value.From == typeof(ServeSession).Assembly.GetName().Name ? "" : $" [{kv.Value.From}]")));
            yield break;
        }

        [ServeCommand("status", "scene, loaded craft, parts, mass, center of mass")]
        public static IEnumerator StatusCmd(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            Status(answer);
            yield break;
        }

        [ServeCommand("load", "craft=NAME [dir=SPH] or path=ABSOLUTE: the craft into its editor")]
        public static IEnumerator Load(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string path = q["path"] ?? ctx.CraftPath(q["craft"] ?? throw new ArgumentException("craft= or path= needed"), q["dir"] ?? "SPH");
            return LoadPath(ctx, path, answer);
        }

        [ServeCommand("reload", "the loaded craft again from its file (after a generator rewrote it)")]
        public static IEnumerator Reload(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer) =>
            LoadPath(ctx, Nav.LoadedCraft ?? throw new InvalidOperationException("no craft loaded yet"), answer);

        private static IEnumerator LoadPath(TestContext ctx, string path, Dictionary<string, object> answer)
        {
            path = Path.GetFullPath(path);
            // in an editor already: straight in (EnsureState would take the same file as nothing to do)
            if (HighLogic.LoadedSceneIsEditor) yield return Nav.LoadCraft(ctx, path);
            else yield return Nav.EnsureState(ctx, Nav.CraftFacility(path) == EditorFacility.SPH ? StartState.Sph : StartState.Vab, path);
            Ugui.DismissPopups();
            foreach (var ready in EditorReady)
                yield return Wait.Until(ready.Value, 60, ready.Key + " ready", shot: false);
            Status(answer);
        }

        [ServeCommand("shots", "views=plan+side+front [scenery=off] [label=X]: the editor craft's three views (EditorShots), the files and their scale records; the camera put back after")]
        public static IEnumerator Shots(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string label = q["label"] ?? EditorLogic.fetch.ship.shipName;
            int before = ctx.Screenshots.Count;
            var keys = new HashSet<string>(ctx.Values.Keys);
            // the views pose the editor's cameras and turn its camera controllers off: the player's view comes back after
            var cam = CameraState.Take();
            try
            {
                yield return EditorShots.Views(ctx, label, (q["views"] ?? "plan+side+front").Split(' ', '+'), q["scenery"] == "off");
            }
            finally
            {
                cam.Restore();
            }
            answer["files"] = NewShots(ctx, before);
            answer["records"] = ctx.Values.Where(kv => !keys.Contains(kv.Key) && !kv.Key.Contains(".camera."))
                                          .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [ServeCommand("shot", "name=X: a screenshot of whatever is on screen")]
        public static IEnumerator ShotCmd(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            int before = ctx.Screenshots.Count;
            yield return Snap.Shot(ctx, q["name"] ?? "shot");
            answer["files"] = NewShots(ctx, before);
        }

        private static bool _texturesHeld;

        [ServeCommand("textures", "[mode=full|low]: in a low-texture run (--textures low), full textures until mode=low (screenshots go full on their own)")]
        public static IEnumerator TexturesCmd(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string mode = q["mode"];
            if (mode == "full" && !_texturesHeld)
            {
                _texturesHeld = true;
                yield return Textures.Hold();
            }
            else if (mode == "low" && _texturesHeld)
            {
                _texturesHeld = false;
                Textures.Release();
            }
            answer["textures"] = Textures.Describe();
        }

        /// <summary>The screenshots taken since there were before of them, as absolute paths.</summary>
        public static List<object> NewShots(TestContext ctx, int before) =>
            ctx.Screenshots.Skip(before).Select(f => (object)Path.GetFullPath(Path.Combine(ctx.ResultsDir, f))).ToList();

        [ServeCommand("parts", "[match=TEXT]: each part's craft-file id, title, position, mass, resource mass, parent, modules")]
        public static IEnumerator PartsCmd(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string match = q["match"];
            var list = new List<object>();
            foreach (Part p in ShipParts())
            {
                string id = Id(p);
                if (match != null && id.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0 &&
                    p.partInfo.title.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["title"] = p.partInfo.title,
                    ["pos"] = Vec(p.transform.position),
                    ["massT"] = (double)p.mass,
                    ["resourceT"] = p.GetResourceMass(),
                    ["parent"] = p.parent != null ? Id(p.parent) : null,
                    ["modules"] = p.Modules.Cast<PartModule>().Select(m => (object)m.moduleName).ToList(),
                });
            }
            answer["parts"] = list;
            yield break;
        }

        [ServeCommand("field", "part=ID module=M [field=F [value=V]]: a module's KSPFields, one read, or one set (and the editor told)")]
        public static IEnumerator FieldCmd(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string id = q["part"] ?? throw new ArgumentException("part= needed");
            Part part = ShipParts().FirstOrDefault(p => Id(p) == id) ?? throw new ArgumentException("no part " + id);
            string moduleName = q["module"] ?? throw new ArgumentException("module= needed");
            PartModule module = part.Modules.Cast<PartModule>().FirstOrDefault(m => m.moduleName == moduleName)
                                ?? throw new ArgumentException($"{id} has no {moduleName}");
            string fieldName = q["field"];
            if (fieldName == null)
            {
                answer["fields"] = module.Fields.Cast<BaseField>().ToDictionary(f => f.name, f => (object)Convert.ToString(f.GetValue(module), Inv));
                yield break;
            }
            BaseField field = module.Fields[fieldName] ?? throw new ArgumentException($"{moduleName} has no field {fieldName}");
            object old = field.GetValue(module);
            if (q["value"] != null)
            {
                field.SetValue(Parse(q["value"], field.FieldInfo.FieldType), module);
                // what a slider in the part's menu would do: the module's change handler, then the editor's
                field.uiControlEditor?.onFieldChanged?.Invoke(field, old);
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
                answer["old"] = Convert.ToString(old, Inv);
            }
            answer["value"] = Convert.ToString(field.GetValue(module), Inv);
        }

        [ServeCommand("save", "path=ABSOLUTE or name=NAME [dir=SPH]: the editor craft written to a craft file")]
        public static IEnumerator Save(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string path = q["path"] ?? ctx.CraftPath(q["name"] ?? throw new ArgumentException("path= or name= needed"), q["dir"] ?? "SPH");
            var ship = EditorLogic.fetch.ship;
            if (q["name"] != null) ship.shipName = Path.GetFileNameWithoutExtension(path);
            ship.SaveShip().Save(path);
            answer["saved"] = Path.GetFullPath(path);
            yield break;
        }

        [ServeCommand("quit", "ends the session (KSP quits)")]
        public static IEnumerator Quit(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            _quit = true;
            answer["quit"] = true;
            yield break;
        }

        // ------------------------------------------------- reflection: any loaded type's static members and onward

        [ServeCommand("get", "expr=Type.member.member[n]... [depth=1]: a value (a type's static member, then fields, properties, list items)")]
        public static IEnumerator Get(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            answer["value"] = ToJson(Resolve(q["expr"] ?? throw new ArgumentException("expr= needed"), out _), int.Parse(q["depth"] ?? "1"));
            yield break;
        }

        [ServeCommand("set", "expr=Type.member...member value=V: a field or property set")]
        public static IEnumerator Set(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string expr = q["expr"] ?? throw new ArgumentException("expr= needed");
            int dot = expr.LastIndexOf('.');
            if (dot < 0) throw new ArgumentException("expr= needs an owner and a member");
            object owner = Resolve(expr.Substring(0, dot), out Type ownerType);
            string name = expr.Substring(dot + 1);
            Type t = owner?.GetType() ?? ownerType;
            var f = t.GetField(name, Any);
            var p = f == null ? t.GetProperty(name, Any) : null;
            if (f == null && p == null) throw new ArgumentException($"{t.Name} has no field or property {name}");
            object value = Parse(q["value"], f?.FieldType ?? p.PropertyType);
            if (f != null) f.SetValue(owner, value); else p.SetValue(owner, value, null);
            answer["value"] = ToJson(f != null ? f.GetValue(owner) : p.GetValue(owner, null), 1);
            yield break;
        }

        [ServeCommand("call", "expr=Type.member...Method [args=a,b,c] [depth=1]: a method called (by name and argument count, arguments parsed to its types; ctx for a TestContext); a coroutine it returns is run to its end")]
        public static IEnumerator Call(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string expr = q["expr"] ?? throw new ArgumentException("expr= needed");
            int dot = expr.LastIndexOf('.');
            if (dot < 0) throw new ArgumentException("expr= needs an owner and a method");
            object owner = Resolve(expr.Substring(0, dot), out Type ownerType);
            string name = expr.Substring(dot + 1);
            string[] args = string.IsNullOrEmpty(q["args"]) ? new string[0] : q["args"].Split(',');
            Type t = owner?.GetType() ?? ownerType;
            MethodInfo m = t.GetMethods(Any).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length
                                                                && (owner != null || x.IsStatic))
                           ?? throw new ArgumentException($"{t.Name} has no method {name} taking {args.Length} arguments");
            var pars = m.GetParameters();
            object[] values = args.Select((a, i) => pars[i].ParameterType == typeof(TestContext) && a == "ctx" ? ctx : Parse(a, pars[i].ParameterType)).ToArray();
            object result = m.Invoke(m.IsStatic ? null : owner, values);
            if (result is IEnumerator co)
            {
                yield return co;
                answer["ran"] = "coroutine";
            }
            else answer["value"] = ToJson(result, int.Parse(q["depth"] ?? "1"));
        }

        /// <summary>
        /// A dotted path: its first segments a type (full or short name, any loaded assembly; the longest that names one),
        /// then static then instance fields or properties, each optionally indexed [n] into a list.
        /// </summary>
        private static object Resolve(string expr, out Type type)
        {
            string[] seg = expr.Split('.');
            type = null;
            int used = 0;
            for (int n = seg.Length; n >= 1 && type == null; --n)
            {
                string name = string.Join(".", seg.Take(n).Select(s => Bare(s)));
                type = FindType(name);
                if (type != null) used = n;
            }
            if (type == null) throw new ArgumentException("no type at the start of " + expr);
            object cur = null;
            Type curType = type;
            string last = seg[used - 1];
            if (last.Contains("[")) throw new ArgumentException("a type can't be indexed: " + last);
            for (int i = used; i < seg.Length; ++i)
            {
                string s = Bare(seg[i]);
                Type t = cur?.GetType() ?? curType;
                var f = t.GetField(s, Any);
                var p = f == null ? t.GetProperty(s, Any) : null;
                if (f == null && p == null) throw new ArgumentException($"{t.Name} has no field or property {s}");
                cur = f != null ? f.GetValue(f.IsStatic ? null : cur) : p.GetValue(p.GetGetMethod(true).IsStatic ? null : cur, null);
                curType = f?.FieldType ?? p.PropertyType;
                foreach (int k in Indexes(seg[i]))
                {
                    if (!(cur is IList list)) throw new ArgumentException(s + " is not a list");
                    cur = list[k];
                    curType = cur?.GetType() ?? typeof(object);
                }
                if (cur == null && i < seg.Length - 1) throw new NullReferenceException(string.Join(".", seg.Take(i + 1)) + " is null");
            }
            type = curType;
            return cur;
        }

        private static string Bare(string s) => s.Contains("[") ? s.Substring(0, s.IndexOf('[')) : s;

        private static IEnumerable<int> Indexes(string s)
        {
            int i = s.IndexOf('[');
            while (i >= 0)
            {
                int j = s.IndexOf(']', i);
                yield return int.Parse(s.Substring(i + 1, j - i - 1), Inv);
                i = s.IndexOf('[', j);
            }
        }

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        private static Type FindType(string name)
        {
            if (TypeCache.TryGetValue(name, out Type cached)) return cached;
            Type found = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                found = types.FirstOrDefault(t => t.FullName == name) ?? types.FirstOrDefault(t => t.Name == name && !t.IsNested);
                if (found != null && found.FullName == name) break;
            }
            TypeCache[name] = found;
            return found;
        }

        private static object Parse(string s, Type t)
        {
            if (t == typeof(string)) return s;
            if (t.IsEnum) return Enum.Parse(t, s, true);
            if (t == typeof(Vector3))
            {
                var v = s.Split(' ', ';').Select(x => float.Parse(x, Inv)).ToArray();
                return new Vector3(v[0], v[1], v[2]);
            }
            return Convert.ChangeType(s, Nullable.GetUnderlyingType(t) ?? t, Inv);
        }

        /// <summary>A value as JSON-able data: numbers, strings, vectors as lists, dictionaries and lists (to depth, at most 500
        /// items), anything else its public fields and properties at depth > 0, else its ToString.</summary>
        private static object ToJson(object v, int depth)
        {
            switch (v)
            {
                case null: return null;
                case string s: return s;
                case bool b: return b;
                case Enum e: return e.ToString();
                case Vector3 v3: return Vec(v3);
                case Vector3d v3d: return new List<object> { v3d.x, v3d.y, v3d.z };
                case Quaternion qt: return new List<object> { (double)qt.x, (double)qt.y, (double)qt.z, (double)qt.w };
            }
            if (v is IConvertible c && v.GetType().IsPrimitive) return Convert.ToDouble(c, Inv);
            if (v is decimal dec) return (double)dec;
            if (v is IDictionary dict)
            {
                var o = new Dictionary<string, object>();
                foreach (DictionaryEntry kv in dict)
                    o[Convert.ToString(kv.Key, Inv)] = depth > 0 ? ToJson(kv.Value, depth - 1) : Convert.ToString(kv.Value, Inv);
                return o;
            }
            if (v is IEnumerable list)
                return list.Cast<object>().Take(500).Select(x => depth > 0 ? ToJson(x, depth - 1) : (object)Convert.ToString(x, Inv)).ToList();
            if (depth <= 0 || v is UnityEngine.Object) return v.ToString();
            var fields = new Dictionary<string, object> { ["$type"] = v.GetType().FullName };
            foreach (var f in v.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                fields[f.Name] = ToJson(f.GetValue(v), depth - 1);
            foreach (var p in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            {
                try { fields[p.Name] = ToJson(p.GetValue(v, null), depth - 1); }
                catch (Exception e) { fields[p.Name] = "(" + Unwrap(e).GetType().Name + ")"; }
            }
            return fields;
        }

        // ------------------------------------------------------------------------- plugins

        [ServeCommand("plugin", "path=X.dll: a DLL of [ServeCommand] methods loaded (from its bytes: it can be rebuilt) and its requests registered")]
        public static IEnumerator Plugin(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string path = Path.GetFullPath(q["path"] ?? throw new ArgumentException("path= needed"));
            byte[] dll = File.ReadAllBytes(path);
            string pdbPath = Path.ChangeExtension(path, ".pdb");
            Assembly a;
            // with its symbols for line numbers in stack traces, where this Mono reads them
            try { a = File.Exists(pdbPath) ? Assembly.Load(dll, File.ReadAllBytes(pdbPath)) : Assembly.Load(dll); }
            catch (Exception e) when (!(e is BadImageFormatException)) { a = Assembly.Load(dll); }
            List<string> names = Register(a);
            Log.Info($"SERVE plugin {a.GetName().Name}: {string.Join(", ", names)}");
            answer["assembly"] = a.GetName().Name;
            answer["registered"] = names.Cast<object>().ToList();
            yield break;
        }

        // ------------------------------------------------------------------------- helpers for requests

        /// <summary>The editor craft's parts; throws without one.</summary>
        public static List<Part> ShipParts()
        {
            var ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null || ship.parts.Count == 0) throw new InvalidOperationException("no craft in the editor");
            return ship.parts;
        }

        /// <summary>The parts' mass, resources included (t), and center of mass (world).</summary>
        public static void MassAndCom(List<Part> parts, out double mass, out Vector3 com)
        {
            com = Vector3.zero;
            mass = 0;
            foreach (Part p in parts)
            {
                float m = p.mass + p.GetResourceMass();
                com += m * p.transform.TransformPoint(p.CoMOffset);
                mass += m;
            }
            com /= Mathf.Max((float)mass, 1e-6f);
        }

        public static List<object> Vec(Vector3 v) => new List<object> { (double)v.x, (double)v.y, (double)v.z };

        /// <summary>A part as its craft file names it: partName_craftID.</summary>
        public static string Id(Part p) => p.partInfo.name + "_" + p.craftID;

        /// <summary>"0,2,4" (or fallback) as numbers.</summary>
        public static double[] Doubles(string s, string fallback) =>
            (s ?? fallback).Split(',').Select(x => double.Parse(x.Trim(), Inv)).ToArray();

        /// <summary>The least-squares slope of y on x.</summary>
        public static double Slope(List<double> x, List<double> y)
        {
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0;
            for (int i = 0; i < x.Count; ++i) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
            return sxy / sxx;
        }

        private static void Status(Dictionary<string, object> answer)
        {
            answer["scene"] = HighLogic.LoadedScene.ToString();
            answer["craftFile"] = Nav.LoadedCraft;
            var ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null || ship.parts.Count == 0) return;
            MassAndCom(ship.parts, out double mass, out Vector3 com);
            answer["ship"] = ship.shipName;
            answer["parts"] = ship.parts.Count;
            answer["massT"] = mass;
            answer["com"] = Vec(com);
            answer["rootPos"] = Vec(ship.parts[0].transform.position);
            foreach (var ready in EditorReady)
                answer["ready: " + ready.Key] = ready.Value();
        }
    }
}
