using System;

namespace KspTest
{
    /// <summary>Thrown by assertions and by drivers when an expected UI element never shows up.</summary>
    public class UITestFailure : Exception
    {
        public UITestFailure(string message) : base(message) { }
        public UITestFailure(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Thrown by a test that cannot run here (the machine lacks something it needs): reports "skipped".</summary>
    public class UITestSkipped : Exception
    {
        public UITestSkipped(string message) : base(message) { }
    }

    public static class Assert
    {
        /// <summary>Skips the test (not a failure) unless the machine has what it needs.</summary>
        public static void SkipUnless(bool cond, string reason)
        {
            if (!cond) throw new UITestSkipped(reason);
        }

        public static void True(bool cond, string what)
        {
            if (!cond) throw new UITestFailure("Assert failed: " + what);
        }

        public static void False(bool cond, string what) => True(!cond, what);

        public static void NotNull(object o, string what)
        {
            if (o == null) throw new UITestFailure("Assert failed: " + what + " is null");
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!Equals(expected, actual))
                throw new UITestFailure($"Assert failed: {what}: expected {expected}, got {actual}");
        }

        public static void Near(double expected, double actual, double tol, string what)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tol)
                throw new UITestFailure($"Assert failed: {what}: expected {expected} +/- {tol}, got {actual}");
        }

        public static void InRange(double lo, double hi, double actual, string what)
        {
            if (double.IsNaN(actual) || actual < lo || actual > hi)
                throw new UITestFailure($"Assert failed: {what}: expected [{lo}, {hi}], got {actual}");
        }

        public static void Fail(string why) => throw new UITestFailure(why);
    }
}
