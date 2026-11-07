using System.Collections;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    ///     Low-res textures for a run (run.cfg textureLimit, ksptest --textures low): Unity's masterTextureLimit held at the
    ///     run's limit outside screenshots (3 = an eighth of the resolution: ~6 GiB less on an RP-1 install), 0 while a
    ///     screenshot or a recording holds them full. Raising the limit back reloads the full mips from disk within a few
    ///     frames. KSP sets the limit from settings.cfg's TEXTURE_QUALITY whenever it applies its settings; those calls are
    ///     patched to put the run's value back, so the settings.cfg every instance of the install shares never gets it.
    /// </summary>
    public static class Textures
    {
        private static Harmony _harmony;
        private static int _holds;
        private static bool _full;
        private static float _lowAt = float.MaxValue;

        /// <summary>The run's limit outside screenshots; 0: full textures, and nothing here does anything.</summary>
        public static int LowLimit { get; private set; }

        /// <summary>Frames to wait after going full before a capture: the render thread uploads the reloaded mips.</summary>
        public static int FullFrames = 3;

        /// <summary>How long textures stay full after the last hold ends, so a burst of shots switches once.</summary>
        public static float LowAfterSec = 5f;

        public static void Init(int limit)
        {
            if (limit <= 0 || _harmony != null)
                return;
            LowLimit = limit;
            _harmony = new Harmony("KspTest.Textures");
            var reapply = new HarmonyMethod(typeof(Textures), nameof(Reapply));
            _harmony.Patch(AccessTools.Method(typeof(GameSettings), nameof(GameSettings.ApplySettings)), postfix: reapply);
            _harmony.Patch(AccessTools.Method(typeof(GameSettings), "ApplyEngineSettings"), postfix: reapply);
            _harmony.Patch(AccessTools.Method(typeof(VideoSettings), nameof(VideoSettings.ApplySettings)), postfix: reapply);
            Reapply();
            Log.Info($"textures: masterTextureLimit {limit} outside screenshots");
        }

        private static void Reapply() => QualitySettings.masterTextureLimit = _full ? 0 : LowLimit;

        /// <summary>Full textures until <see cref="Release" />, waiting until they are in. Nothing in a full-texture run.</summary>
        public static IEnumerator Hold()
        {
            if (LowLimit <= 0)
                yield break;
            bool wasFull = _full;
            HoldNow();
            if (wasFull)
                yield break;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < FullFrames; i++)
                yield return null;
            Log.Info($"textures: full, {FullFrames} frames waited ({sw.ElapsedMilliseconds} ms)");
        }

        /// <summary>Full textures until <see cref="Release" />, without waiting (the first frames may still be low-res).</summary>
        public static void HoldNow()
        {
            if (LowLimit <= 0)
                return;
            _holds++;
            _lowAt = float.MaxValue;
            if (_full)
                return;
            var sw = Stopwatch.StartNew();
            _full = true;
            Reapply();
            Log.Info($"textures: limit 0 set in {sw.ElapsedMilliseconds} ms");
        }

        public static void Release()
        {
            if (LowLimit <= 0 || _holds == 0)
                return;
            if (--_holds == 0)
                _lowAt = Time.realtimeSinceStartup + LowAfterSec;
        }

        /// <summary>
        /// Every frame outside a test (TestRunner; a serve session): back to the run's limit once no hold has been left
        /// for <see cref="LowAfterSec" />.
        /// </summary>
        public static void Tick()
        {
            if (Time.realtimeSinceStartup >= _lowAt)
                LowNow();
        }

        /// <summary>
        /// Back to the run's limit now unless a hold is on: TestRunner's at the start of each test, before its scene and
        /// craft load. Not in the middle of one: the reload of every texture it starts crashed or froze KSP on Windows CI
        /// runners (no GPU) twice, as ProceduralParts built a craft's parts.
        /// </summary>
        public static void LowNow()
        {
            if (!_full || _holds > 0)
                return;
            var sw = Stopwatch.StartNew();
            _full = false;
            _lowAt = float.MaxValue;
            Reapply();
            Log.Info($"textures: limit {LowLimit} set in {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>The state, for the textures request.</summary>
        public static string Describe() =>
            LowLimit <= 0 ? "full (run without a texture limit)" : $"{(_full ? "full" : "low")}, run limit {LowLimit}, holds {_holds}, now {QualitySettings.masterTextureLimit}";
    }
}
