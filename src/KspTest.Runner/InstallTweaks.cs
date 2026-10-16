using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KspTest.Runner
{
    /// <summary>
    ///     What a run changes in the install, and puts back afterwards: QuickStart off (it would race the harness), the master
    ///     volume (with Mute), and Unity's window prefs.
    /// </summary>
    public sealed class InstallTweaks
    {
        private readonly KspInstall _install;
        private readonly Action<string> _log;
        private string? _quickStartWas;
        private Dictionary<string, int?>? _windowWas;

        private string QuickStartCfg => Path.Combine(_install.GameData, "QuickMods", "QuickStart", "Config.txt");

        public InstallTweaks(KspInstall install, Action<string> log)
        {
            _install = install;
            _log = log;
        }

        public void DisableQuickStart()
        {
            if (!File.Exists(QuickStartCfg))
                return;
            _quickStartWas = File.ReadAllText(QuickStartCfg);
            string disabled = Regex.Replace(_quickStartWas, @"(?m)^(\s*Enabled\s*=\s*)True\s*$", "${1}False");
            if (disabled == _quickStartWas && !Regex.IsMatch(_quickStartWas, @"(?m)^\s*Enabled\s*="))
                disabled = "Enabled = False\r\n" + _quickStartWas;
            File.WriteAllText(QuickStartCfg, disabled);
            _log("QuickStart disabled for this run");
        }

        public void RestoreQuickStart()
        {
            if (_quickStartWas == null)
                return;
            File.WriteAllText(QuickStartCfg, _quickStartWas);
            _quickStartWas = null;
            _log("QuickStart config restored");
        }

        // settings.cfg values changed for the run: KSP reads them at startup and writes the file back on exit, so the
        // player's are put back once it has exited
        private readonly Dictionary<string, string> _settingsWas = new Dictionary<string, string>();

        private void SetSetting(string key, string value)
        {
            if (!File.Exists(_install.SettingsCfg))
                return;
            string text = File.ReadAllText(_install.SettingsCfg);
            Match m = Regex.Match(text, $@"(?m)^{key}\s*=[ \t]*(.*?)\s*$");
            if (!m.Success)
                return;
            if (!_settingsWas.ContainsKey(key))
                _settingsWas[key] = m.Groups[1].Value;
            File.WriteAllText(_install.SettingsCfg, Regex.Replace(text, $@"(?m)^{key}\s*=[ \t]*.*?(?=\r?$)", $"{key} = {value}"));
        }

        /// <summary>The master volume 0 for the run.</summary>
        public void Mute()
        {
            SetSetting("MASTER_VOLUME", "0");
            if (_settingsWas.TryGetValue("MASTER_VOLUME", out string? was))
                _log($"muted (MASTER_VOLUME was {was})");
        }

        /// <summary>The player's settings.cfg values back, once KSP has exited.</summary>
        public void RestoreSettings()
        {
            if (_settingsWas.Count == 0 || !File.Exists(_install.SettingsCfg))
                return;
            string text = File.ReadAllText(_install.SettingsCfg);
            foreach (var kv in _settingsWas)
                text = Regex.Replace(text, $@"(?m)^{kv.Key}\s*=[ \t]*.*?(?=\r?$)", $"{kv.Key} = {kv.Value}");
            File.WriteAllText(_install.SettingsCfg, text);
            _log("settings.cfg restored: " + string.Join(", ", _settingsWas.Select(kv => $"{kv.Key} = {kv.Value}")));
            _settingsWas.Clear();
        }

        // Unity's own window prefs (one store shared by every KSP install): the run's size, windowed, set before the launch,
        // and the player's put back after it.  Unity writes the window it had on exit, so a KSP that crashed or closed
        // minimized leaves something like 160x28 there and the next game of any install opens that small; the command line
        // alone did not override it.  The Windows registry; elsewhere the -screen-* arguments have to do.
        private const string ScreenKey = @"Software\Squad\Kerbal Space Program";

        private static readonly Dictionary<string, string> ScreenNames = new Dictionary<string, string>
        {
            ["width"] = "Screenmanager Resolution Width_h182942802",
            ["height"] = "Screenmanager Resolution Height_h2627697771",
            ["mode"] = "Screenmanager Fullscreen mode_h3630240806",
            ["native"] = "Screenmanager Resolution Use Native_h1405027254",
        };

        public void SetWindow(int width, int height)
        {
            // KSP's own resolution too: started fullscreen at another size, it switched to the run's window as the first
            // test began, and crashed in the save's texture load right after, three times running
            SetSetting("SCREEN_RESOLUTION_WIDTH", width.ToString());
            SetSetting("SCREEN_RESOLUTION_HEIGHT", height.ToString());
            SetSetting("FULLSCREEN", "False");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(ScreenKey);
            _windowWas = new Dictionary<string, int?>();
            foreach (var kv in ScreenNames)
                _windowWas[kv.Key] = key.GetValue(kv.Value) is int v ? v : (int?)null;
            // fullscreen mode: 0 exclusive, 1 fullscreen window, 2 maximized window, 3 windowed
            var want = new Dictionary<string, int> { ["width"] = width, ["height"] = height, ["mode"] = 3, ["native"] = 0 };
            foreach (var kv in want)
                key.SetValue(ScreenNames[kv.Key], kv.Value, RegistryValueKind.DWord);
        }

        /// <summary>What was there, unless it was a tiny window left by a crash: then the install's settings.cfg size.</summary>
        public void RestoreWindow()
        {
            if (_windowWas == null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;
            var restore = new Dictionary<string, int?>(_windowWas);
            if (!(restore["width"] >= 640 && restore["height"] >= 480))
            {
                string cfg = File.Exists(_install.SettingsCfg) ? File.ReadAllText(_install.SettingsCfg) : "";
                Match w = Regex.Match(cfg, @"(?m)^\s*SCREEN_RESOLUTION_WIDTH\s*=\s*(\d+)");
                Match h = Regex.Match(cfg, @"(?m)^\s*SCREEN_RESOLUTION_HEIGHT\s*=\s*(\d+)");
                restore["width"] = w.Success ? int.Parse(w.Groups[1].Value) : 1920;
                restore["height"] = h.Success ? int.Parse(h.Groups[1].Value) : 1080;
                restore["mode"] = Regex.IsMatch(cfg, @"(?m)^\s*FULLSCREEN\s*=\s*True") ? 1 : 3;
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(ScreenKey);
            foreach (var kv in restore)
                if (kv.Value.HasValue)
                    key.SetValue(ScreenNames[kv.Key], kv.Value.Value, RegistryValueKind.DWord);
            _log($"window prefs restored: {restore["width"]}x{restore["height"]} mode {restore["mode"]}");
            _windowWas = null;
        }
    }
}
