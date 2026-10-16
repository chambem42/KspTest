using System;
using System.IO;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>Writes to KSP.log with a [KSPUITest] prefix and mirrors into harness.log in the results dir.</summary>
    public static class Log
    {
        public const string Prefix = "[KSPUITest] ";
        private static StreamWriter _file;

        public static void Open(string resultsDir)
        {
            try
            {
                Directory.CreateDirectory(resultsDir);
                _file = new StreamWriter(Path.Combine(resultsDir, "harness.log"), false) { AutoFlush = true };
            }
            catch (Exception e)
            {
                Debug.LogWarning(Prefix + "cannot open harness.log: " + e.Message);
            }
        }

        public static void Close()
        {
            _file?.Dispose();
            _file = null;
        }

        public static void Info(string msg)
        {
            Debug.Log(Prefix + msg);
            Mirror("INFO ", msg);
        }

        public static void Warn(string msg)
        {
            Debug.LogWarning(Prefix + msg);
            Mirror("WARN ", msg);
        }

        public static void Error(string msg)
        {
            Debug.LogError(Prefix + msg);
            Mirror("ERROR", msg);
        }

        private static void Mirror(string level, string msg)
        {
            _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {Time.realtimeSinceStartup,8:F1} {level} {msg}");
        }
    }
}
