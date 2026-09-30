using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Diagnostic for "native memory grows ~1 GB/min and UnloadUnusedAssets frees nothing" (2026-09-29 logs):
    /// something keeps creating Unity objects and keeps them referenced. Counts every loaded Unity object by
    /// type and every scene GameObject by name, and logs what grew since the first snapshot of the raid.
    /// A mod leaking materials (Renderer.material copies), render textures, meshes or spawned effect objects
    /// shows up by type and usually by a recognisable GameObject name.
    ///
    /// FindObjectsOfTypeAll walks every object in memory, so a snapshot hitches (hundreds of ms on a big raid).
    /// It is opt-in and runs at quiet moments only.
    /// </summary>
    internal sealed class LeakTracker
    {
        private const int TopCount = 12;

        private readonly ManualLogSource _log;
        private Dictionary<Type, int> _firstTypes;
        private Dictionary<string, int> _firstNames;
        private Dictionary<string, int> _firstRoots;
        private int _firstInactive;
        private int _firstCorpses;
        private int _firstPooledIdle;
        private Dictionary<Type, int> _lastTypes;
        private int _firstObjects;
        private float _firstTime;
        private int _snapshots;

        public LeakTracker(ManualLogSource log)
        {
            _log = log;
        }

        public string LastSummary { get; private set; } = "아직 없음";

        public int Snapshots => _snapshots;

        public void Reset()
        {
            _firstTypes = null;
            _firstNames = null;
            _firstRoots = null;
            _lastTypes = null;
            _snapshots = 0;
            LastSummary = "아직 없음";
        }

        public void Snapshot(string reason)
        {
            var watch = Stopwatch.StartNew();
            Object[] all = Resources.FindObjectsOfTypeAll<Object>();

            var types = new Dictionary<Type, int>(512);
            var names = new Dictionary<string, int>(4096);
            var roots = new Dictionary<string, int>(1024);
            int sceneObjects = 0;
            int inactive = 0;
            int corpses = 0;
            int pooledActive = 0;
            int pooledIdle = 0;
            long renderTexturePixels = 0;
            int renderTextures = 0;

            for (int i = 0; i < all.Length; i++)
            {
                Object obj = all[i];
                if (obj == null)
                {
                    continue;
                }

                Type type = obj.GetType();
                types.TryGetValue(type, out int count);
                types[type] = count + 1;

                if (obj is GameObject go)
                {
                    // Only instances living in a scene (DontDestroyOnLoad included); prefabs/assets have no scene.
                    if (!go.scene.IsValid())
                    {
                        continue;
                    }

                    sceneObjects++;
                    if (!go.activeInHierarchy)
                    {
                        inactive++;
                    }

                    string name = NormalizeName(go.name);
                    names.TryGetValue(name, out int n);
                    names[name] = n + 1;

                    // Which top-level object the growth hangs under: a corpse / bot, the asset pool, the level...
                    string root = NormalizeName(go.transform.root.name);
                    roots.TryGetValue(root, out int r);
                    roots[root] = r + 1;
                }
                else if (obj is EFT.Interactive.Corpse)
                {
                    corpses++;
                }
                else if (obj is EFT.AssetsManager.AssetPoolObject pooled)
                {
                    // Pool objects sitting disabled = returned to the pool and waiting for reuse.
                    if (pooled.gameObject.activeInHierarchy)
                    {
                        pooledActive++;
                    }
                    else
                    {
                        pooledIdle++;
                    }
                }
                else if (obj is RenderTexture rt)
                {
                    renderTextures++;
                    renderTexturePixels += (long)rt.width * rt.height * Math.Max(1, rt.volumeDepth);
                }
            }

            watch.Stop();
            _snapshots++;
            float now = Time.realtimeSinceStartup;

            if (_firstTypes == null)
            {
                _firstTypes = types;
                _firstNames = names;
                _firstRoots = roots;
                _firstInactive = inactive;
                _firstCorpses = corpses;
                _firstPooledIdle = pooledIdle;
                _firstObjects = all.Length;
                _firstTime = now;
                _lastTypes = types;
                LastSummary = $"{DateTime.Now:HH:mm:ss} 기준점 저장: 오브젝트 {all.Length:N0}개, 씬 GameObject {sceneObjects:N0}개 ({watch.ElapsedMilliseconds}ms)";
                _log.LogInfo($"[leak] #{_snapshots} baseline ({reason}) in {watch.ElapsedMilliseconds}ms: objects {all.Length}, scene GameObjects {sceneObjects}, " +
                             $"RenderTextures {renderTextures} ({renderTexturePixels / 1000000.0:0.0} MPix), inactive scene GameObjects {inactive}, " +
                             $"corpses {corpses}, pool objects active {pooledActive} / idle {pooledIdle}");
                return;
            }

            float minutes = Mathf.Max(0.1f, (now - _firstTime) / 60f);
            var sb = new StringBuilder(2048);
            sb.Append($"[leak] #{_snapshots} ({reason}) in {watch.ElapsedMilliseconds}ms, {minutes:0.0} min after baseline: ");
            sb.Append($"objects {all.Length} ({Signed(all.Length - _firstObjects)}), scene GameObjects {sceneObjects}, ");
            sb.Append($"RenderTextures {renderTextures} ({renderTexturePixels / 1000000.0:0.0} MPix), ");
            sb.Append($"inactive scene GameObjects {inactive} ({Signed(inactive - _firstInactive)}), corpses {corpses} ({Signed(corpses - _firstCorpses)}), ");
            sb.Append($"pool objects active {pooledActive} / idle {pooledIdle} ({Signed(pooledIdle - _firstPooledIdle)} idle)\n");

            sb.Append("  types grown since baseline (since last snapshot): ");
            AppendTop(sb, types, _firstTypes, t => t.FullName, _lastTypes);
            sb.Append("\n  scene GameObject names grown since baseline: ");
            AppendTop(sb, names, _firstNames, s => s, null);
            sb.Append("\n  top-level (root) objects that gained children since baseline: ");
            AppendTop(sb, roots, _firstRoots, s => s, null);
            _log.LogInfo(sb.ToString());

            KeyValuePair<Type, int> topType = types
                .Select(kv => new KeyValuePair<Type, int>(kv.Key, kv.Value - Get(_firstTypes, kv.Key)))
                .OrderByDescending(kv => kv.Value)
                .FirstOrDefault();
            KeyValuePair<string, int> topName = names
                .Select(kv => new KeyValuePair<string, int>(kv.Key, kv.Value - Get(_firstNames, kv.Key)))
                .OrderByDescending(kv => kv.Value)
                .FirstOrDefault();
            LastSummary = $"{DateTime.Now:HH:mm:ss} {minutes:0}분 동안 오브젝트 {Signed(all.Length - _firstObjects)}개 — " +
                          $"가장 많이 는 종류 {topType.Key?.Name ?? "-"} {Signed(topType.Value)}, " +
                          $"이름 '{topName.Key ?? "-"}' {Signed(topName.Value)} ({watch.ElapsedMilliseconds}ms, 자세한 건 로그의 [leak])";
            _lastTypes = types;
        }

        private static void AppendTop<TKey>(StringBuilder sb, Dictionary<TKey, int> now, Dictionary<TKey, int> baseline,
            Func<TKey, string> label, Dictionary<TKey, int> previous)
        {
            var grown = now
                .Select(kv => new { kv.Key, Delta = kv.Value - Get(baseline, kv.Key), Count = kv.Value })
                .Where(x => x.Delta > 0)
                .OrderByDescending(x => x.Delta)
                .Take(TopCount)
                .ToList();

            if (grown.Count == 0)
            {
                sb.Append("(none)");
                return;
            }

            for (int i = 0; i < grown.Count; i++)
            {
                var x = grown[i];
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(label(x.Key)).Append(' ').Append(x.Count).Append(" (").Append(Signed(x.Delta));
                if (previous != null)
                {
                    sb.Append(" / ").Append(Signed(x.Count - Get(previous, x.Key)));
                }

                sb.Append(')');
            }
        }

        private static int Get<TKey>(Dictionary<TKey, int> map, TKey key)
        {
            return map != null && map.TryGetValue(key, out int value) ? value : 0;
        }

        private static string Signed(int value)
        {
            return value >= 0 ? "+" + value : value.ToString();
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "(no name)";
            }

            // "Blood_Decal(Clone)(Clone)" / "Bot 12" -> group instances of the same thing together.
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone > 0)
            {
                name = name.Substring(0, clone);
            }

            string trimmed = name.TrimEnd(' ', '_', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            return trimmed.Length > 0 ? trimmed : name;
        }
    }
}
