using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Profiling.Memory.Experimental;

namespace KspTest.ServePlugins
{
    /// <summary>
    /// Where the game's memory goes: Unity's totals, every loaded UnityEngine.Object by type (with
    /// Profiler.GetRuntimeMemorySizeLong, which reads 0 on the release player), textures/meshes/audio by the GameData
    /// folder they came from, GameDatabase config nodes by folder, and a MemoryProfiler snapshot (.snap) for the managed
    /// heap, read offline. Needs the development player for the sizes and the snapshot.
    /// </summary>
    public static class MemoryCommands
    {
        [ServeCommand("memsnap", "dir=PATH [scene=SPACECENTER|EDITOR|...] [settleSec=15] [gc=on] [snap=on]: memory census "
            + "CSVs + a MemoryProfiler snapshot in dir")]
        public static IEnumerator MemSnap(TestContext ctx, NameValueCollection q, Dictionary<string, object> answer)
        {
            string dir = q["dir"];
            if (string.IsNullOrEmpty(dir)) { answer["error"] = "dir= required"; yield break; }
            Directory.CreateDirectory(dir);

            if (!string.IsNullOrEmpty(q["scene"]))
            {
                var target = (GameScenes)Enum.Parse(typeof(GameScenes), q["scene"], true);
                if (HighLogic.LoadedScene != target)
                {
                    HighLogic.LoadScene(target);
                    float t0 = Time.realtimeSinceStartup;
                    while (HighLogic.LoadedScene != target && Time.realtimeSinceStartup - t0 < 120) yield return null;
                }
            }
            float settle = float.Parse(q["settleSec"] ?? "15", CultureInfo.InvariantCulture);
            float s0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - s0 < settle) yield return null;
            answer["scene"] = HighLogic.LoadedScene.ToString();
            answer["supported"] = Profiler.supported;

            if ((q["gc"] ?? "on") == "on")
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            var totals = new List<string[]>
            {
                Row("totalAllocated", Profiler.GetTotalAllocatedMemoryLong()),
                Row("totalReserved", Profiler.GetTotalReservedMemoryLong()),
                Row("totalUnusedReserved", Profiler.GetTotalUnusedReservedMemoryLong()),
                Row("monoHeapSize", Profiler.GetMonoHeapSizeLong()),
                Row("monoUsedSize", Profiler.GetMonoUsedSizeLong()),
                Row("gcTotalMemory", GC.GetTotalMemory(false)),
                Row("graphicsDriver", Profiler.GetAllocatedMemoryForGraphicsDriver()),
                Row("tempAllocatorSize", Profiler.GetTempAllocatorSize()),
                // texture streaming: what is resident vs what full mips would take
                Row("texTotal", (long)Texture.totalTextureMemory),
                Row("texCurrent", (long)Texture.currentTextureMemory),
                Row("texDesired", (long)Texture.desiredTextureMemory),
                Row("texTarget", (long)Texture.targetTextureMemory),
                Row("texNonStreaming", (long)Texture.nonStreamingTextureMemory),
                Row("texStreamingCount", (long)Texture.streamingTextureCount),
                Row("texNonStreamingCount", (long)Texture.nonStreamingTextureCount),
                Row("streamingActive", QualitySettings.streamingMipmapsActive ? 1 : 0),
                Row("streamingBudgetMb", (long)QualitySettings.streamingMipmapsMemoryBudget),
            };
            WriteCsv(Path.Combine(dir, "totals.csv"), new[] { "what", "bytes" }, totals);
            foreach (var r in totals) answer[r[0]] = long.Parse(r[1]);

            // where GameDatabase's assets came from
            var texUrl = new Dictionary<int, string>();
            foreach (var ti in GameDatabase.Instance.databaseTexture)
                if (ti?.texture != null) texUrl[ti.texture.GetInstanceID()] = ti.name;
            var meshUrl = new Dictionary<int, string>();
            foreach (var go in GameDatabase.Instance.databaseModel)
                if (go != null) ClaimMeshes(go, go.name, meshUrl);
            foreach (var ap in PartLoader.LoadedPartsList)
                if (ap?.partPrefab != null) ClaimMeshes(ap.partPrefab.gameObject, ap.partUrl ?? ap.name, meshUrl);
            var audioUrl = new Dictionary<int, string>();
            foreach (var clip in GameDatabase.Instance.databaseAudio)
                if (clip != null) audioUrl[clip.GetInstanceID()] = clip.name;

            // every loaded object, by type; assets in detail
            var all = Resources.FindObjectsOfTypeAll(typeof(UnityEngine.Object));
            var byType = new Dictionary<string, long[]>();
            var tex = new List<string[]>();
            var meshes = new List<string[]>();
            var audio = new List<string[]>();
            var byFolder = new Dictionary<string, long[]>();   // folder -> texture, mesh, audio bytes
            foreach (var o in all)
            {
                if (o == null) continue;
                long size = Profiler.GetRuntimeMemorySizeLong(o);
                var t = o.GetType();
                string key = t.Assembly.GetName().Name + "|" + t.FullName;
                if (!byType.TryGetValue(key, out var acc)) byType[key] = acc = new long[2];
                acc[0]++;
                acc[1] += size;

                int id = o.GetInstanceID();
                if (o is Texture tx)
                {
                    texUrl.TryGetValue(id, out string url);
                    string fmt = "", readable = "", mips = "";
                    if (tx is Texture2D t2) { fmt = t2.format.ToString(); readable = t2.isReadable.ToString(); mips = t2.mipmapCount.ToString(); }
                    else if (tx is RenderTexture rt) { fmt = rt.format.ToString(); }
                    else if (tx is Cubemap cm) { fmt = cm.format.ToString(); mips = cm.mipmapCount.ToString(); }
                    string folder = Folder(url, tx.name);
                    Add(byFolder, folder, 0, size);
                    tex.Add(new[] { t.Name, tx.name, url ?? "", folder, tx.width.ToString(), tx.height.ToString(), fmt, mips, readable,
                        size.ToString() });
                }
                else if (o is Mesh m)
                {
                    meshUrl.TryGetValue(id, out string url);
                    string folder = Folder(url, null);
                    Add(byFolder, folder, 1, size);
                    meshes.Add(new[] { m.name, url ?? "", folder, m.vertexCount.ToString(), m.isReadable.ToString(), size.ToString() });
                }
                else if (o is AudioClip a)
                {
                    audioUrl.TryGetValue(id, out string url);
                    string folder = Folder(url, null);
                    Add(byFolder, folder, 2, size);
                    audio.Add(new[] { a.name, url ?? "", folder, a.length.ToString("F1", CultureInfo.InvariantCulture),
                        a.loadType.ToString(), size.ToString() });
                }
            }
            WriteCsv(Path.Combine(dir, "unity_types.csv"), new[] { "assembly", "type", "count", "bytes" },
                byType.OrderByDescending(kv => kv.Value[1]).Select(kv =>
                {
                    var parts = kv.Key.Split('|');
                    return new[] { parts[0], parts[1], kv.Value[0].ToString(), kv.Value[1].ToString() };
                }));
            WriteCsv(Path.Combine(dir, "textures.csv"),
                new[] { "class", "name", "url", "folder", "width", "height", "format", "mips", "readable", "bytes" },
                tex.OrderByDescending(r => long.Parse(r[9])));
            WriteCsv(Path.Combine(dir, "meshes.csv"), new[] { "name", "url", "folder", "vertices", "readable", "bytes" },
                meshes.OrderByDescending(r => long.Parse(r[5])));
            WriteCsv(Path.Combine(dir, "audio.csv"), new[] { "name", "url", "folder", "seconds", "loadType", "bytes" },
                audio.OrderByDescending(r => long.Parse(r[5])));

            // GameDatabase config nodes by folder: counts and string characters (the managed heap holds them)
            foreach (var uc in GameDatabase.Instance.root.AllConfigs)
            {
                string folder = Folder(uc.parent?.url, null);
                long[] c = new long[3];
                CountNode(uc.config, c);
                Add(byFolder, folder, 3, c[0]);
                Add(byFolder, folder, 4, c[1]);
                Add(byFolder, folder, 5, c[2]);
            }
            WriteCsv(Path.Combine(dir, "folders.csv"),
                new[] { "folder", "textureBytes", "meshBytes", "audioBytes", "cfgNodes", "cfgValues", "cfgChars" },
                byFolder.OrderByDescending(kv => kv.Value[0] + kv.Value[1] + kv.Value[2]).Select(kv =>
                    new[] { kv.Key }.Concat(kv.Value.Select(v => v.ToString())).ToArray()));
            answer["objects"] = all.Length;
            answer["textures"] = tex.Count;
            answer["meshes"] = meshes.Count;

            if ((q["snap"] ?? "on") == "on")
            {
                string snap = Path.Combine(dir, "ksp.snap");
                bool done = false, ok = false;
                float t0 = Time.realtimeSinceStartup;
                MemoryProfiler.TakeSnapshot(snap, (p, success) => { done = true; ok = success; },
                    CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects | CaptureFlags.NativeAllocations
                    | CaptureFlags.NativeAllocationSites | CaptureFlags.NativeStackTraces);
                while (!done && Time.realtimeSinceStartup - t0 < 600) yield return null;
                answer["snapshot"] = ok ? snap : "failed";
                answer["snapshotSec"] = Time.realtimeSinceStartup - t0;
            }
        }

        static void ClaimMeshes(GameObject go, string url, Dictionary<int, string> owner)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && !owner.ContainsKey(mf.sharedMesh.GetInstanceID())) owner[mf.sharedMesh.GetInstanceID()] = url;
            foreach (var sm in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (sm.sharedMesh != null && !owner.ContainsKey(sm.sharedMesh.GetInstanceID())) owner[sm.sharedMesh.GetInstanceID()] = url;
            foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true))
                if (mc.sharedMesh != null && !owner.ContainsKey(mc.sharedMesh.GetInstanceID())) owner[mc.sharedMesh.GetInstanceID()] = url;
        }

        static void CountNode(ConfigNode n, long[] c)
        {
            c[0]++;
            foreach (ConfigNode.Value v in n.values)
            {
                c[1]++;
                c[2] += (v.name?.Length ?? 0) + (v.value?.Length ?? 0) + (v.comment?.Length ?? 0);
            }
            foreach (ConfigNode child in n.nodes) CountNode(child, c);
        }

        // first path segment of a GameData url, or of a file path under GameData
        static string Folder(string url, string name)
        {
            string s = url;
            if (string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(name))
            {
                string p = name.Replace('\\', '/');
                int g = p.IndexOf("GameData/", StringComparison.OrdinalIgnoreCase);
                if (g >= 0) s = p.Substring(g + 9);
            }
            if (string.IsNullOrEmpty(s)) return "?";
            int slash = s.IndexOf('/');
            return slash > 0 ? s.Substring(0, slash) : s;
        }

        static void Add(Dictionary<string, long[]> d, string key, int i, long v)
        {
            if (!d.TryGetValue(key, out var a)) d[key] = a = new long[6];
            a[i] += v;
        }

        static string[] Row(string what, long bytes) => new[] { what, bytes.ToString() };

        static void WriteCsv(string path, string[] header, IEnumerable<string[]> rows)
        {
            using (var w = new StreamWriter(path))
            {
                w.WriteLine(string.Join(",", header));
                foreach (var r in rows) w.WriteLine(string.Join(",", r.Select(Esc)));
            }
        }

        static string Esc(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
