using System;
using System.Reflection;

namespace KSPUITest
{
    /// <summary>Reflection helpers for poking private state in the mod under test (last resort, not a UI path).</summary>
    public static class Reflect
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        public static Type FindType(string fullName)
        {
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                var t = a.assembly.GetType(fullName, false);
                if (t != null) return t;
            }
            throw new UITestFailure("type not found: " + fullName);
        }

        public static object Get(object target, string member) => Get(target.GetType(), target, member);
        public static object GetStatic(Type type, string member) => Get(type, null, member);

        public static void Set(object target, string member, object value) => Set(target.GetType(), target, member, value);
        public static void SetStatic(Type type, string member, object value) => Set(type, null, member, value);

        public static object Call(object target, string method, params object[] args) => Invoke(target.GetType(), target, method, args);
        public static object CallStatic(Type type, string method, params object[] args) => Invoke(type, null, method, args);

        private static object Get(Type type, object target, string member)
        {
            var f = type.GetField(member, All);
            if (f != null) return f.GetValue(target);
            var p = type.GetProperty(member, All);
            if (p != null) return p.GetValue(target, null);
            throw new UITestFailure($"member {type.Name}.{member} not found");
        }

        private static void Set(Type type, object target, string member, object value)
        {
            var f = type.GetField(member, All);
            if (f != null) { f.SetValue(target, value); return; }
            var p = type.GetProperty(member, All);
            if (p != null) { p.SetValue(target, value, null); return; }
            throw new UITestFailure($"member {type.Name}.{member} not found");
        }

        private static object Invoke(Type type, object target, string method, object[] args)
        {
            var m = type.GetMethod(method, All);
            if (m == null) throw new UITestFailure($"method {type.Name}.{method} not found");
            try
            {
                return m.Invoke(target, args);
            }
            catch (TargetInvocationException e)
            {
                var inner = e.InnerException ?? e;
                throw new UITestFailure($"{type.Name}.{method} threw {inner.GetType().Name}: {inner.Message}", inner);
            }
        }
    }
}
