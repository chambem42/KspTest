using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace KspTest.Runner
{
    /// <summary>
    ///     Copies a save template fresh into the install's test save.  A craft or subassembly the template doesn't have, or has
    ///     different, is someone's work (one saved while playing in the test save went 13 s before a reset deleted it): then
    ///     every file that differs from the template is copied to the backup folder first, same layout.  persistent.sfs and the
    ///     like change every run and alone are not.
    /// </summary>
    public static class SaveReset
    {
        public static void Reset(string template, string target, string backupRoot, string saveFolder, Action<string> log)
        {
            if (!File.Exists(Path.Combine(template, "persistent.sfs")))
                throw new FileNotFoundException($"save template not found: {Path.Combine(template, "persistent.sfs")}");

            if (Directory.Exists(target))
            {
                var differ = new List<string>();
                foreach (string f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                {
                    string rel = Rel(target, f);
                    string tpl = Path.Combine(template, rel);
                    if (File.Exists(tpl) && SameContent(tpl, f))
                        continue;
                    differ.Add(rel);
                }

                // not work: KSP's Auto-Saved Ship, its .loadmeta beside every craft, and the .original a mod leaves when it
                // rewrites a craft on load; a craft that left an .original matching the template was rewritten by the game
                // on load (made on another install, its _modVersions restamped), not by anyone
                bool Rewritten(string rel)
                {
                    string orig = Path.Combine(target, rel + ".original"), tpl = Path.Combine(template, rel);
                    return File.Exists(orig) && File.Exists(tpl) && SameContent(orig, tpl);
                }

                var work = differ.Where(rel => Regex.IsMatch(rel, @"^(Ships|Subassemblies)[\\/]")
                    && !Regex.IsMatch(rel, @"[\\/]Auto-Saved Ship\.craft$")
                    && !Regex.IsMatch(rel, @"^Ships[\\/]temp\.craft$")
                    && !Regex.IsMatch(rel, @"\.(loadmeta|original)$")
                    && !Rewritten(rel)).ToList();
                if (work.Count > 0)
                {
                    string backup = Path.Combine(backupRoot, $"{saveFolder}-{DateTime.Now:yyyyMMdd-HHmmss}");
                    foreach (string rel in differ)
                    {
                        string to = Path.Combine(backup, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Copy(Path.Combine(target, rel), to);
                    }

                    log($"save reset: kept {differ.Count} file(s) that differ from the template ({string.Join(", ", work)}) in {backup}");
                }

                Directory.Delete(target, true);
            }

            CopyDirectory(template, target);
            log($"save reset: {template} -> {target}");
        }

        public static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Rel(from, d)));
            foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(f, Path.Combine(to, Rel(from, f)), true);
        }

        private static string Rel(string root, string path) => path.Substring(root.Length).TrimStart('\\', '/');

        private static bool SameContent(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length)
                return false;
            using var md5 = MD5.Create();
            using FileStream fa = File.OpenRead(a), fb = File.OpenRead(b);
            return md5.ComputeHash(fa).SequenceEqual(md5.ComputeHash(fb));
        }
    }
}
