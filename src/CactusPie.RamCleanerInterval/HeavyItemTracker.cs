using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Diz.Jobs;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// EXPERIMENTAL: which item bundles cost the most memory when they are loaded for the first time in a raid.
    ///
    /// <para>When a bot spawns, the game loads the bundles of everything it carries through
    /// <c>ObjectsFactory.LoadBundlesAndCreatePools</c>. This measures the game's committed memory before the call and
    /// after its Task finishes, and splits the difference over the bundles that were new this raid. SPT's
    /// <c>BundleManager</c> (bundle key → mod folder) says which mod a bundle belongs to; anything not in it is the
    /// game's own. The result is a ranking of mods and bundles by first-load memory - the candidates to remove from a
    /// bot gear mod (APBS) when memory per death is high.</para>
    ///
    /// <para>Why it is experimental: loading is asynchronous and other things allocate at the same time, so each number
    /// is noisy; loads that overlapped another load are marked. Trust repeated, large numbers, not single ones.
    /// Loads during raid loading are ignored (the whole map loads then).</para>
    /// </summary>
    internal sealed class HeavyItemTracker
    {
        private const long Mb = 1024L * 1024L;

        private static HeavyItemTracker s_instance;

        private readonly ManualLogSource _log;
        private readonly object _gate = new object();
        private readonly HashSet<string> _seenThisRaid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Pending> _running = new List<Pending>();
        private readonly ConcurrentQueue<Pending> _done = new ConcurrentQueue<Pending>();
        private readonly Dictionary<string, Stat> _bundles = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Stat> _mods = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _bundleMods;
        private bool _installed;

        private sealed class Pending
        {
            public string[] Paths;
            public long Start;
            public long Delta;
            public bool Overlapped;
        }

        public sealed class Stat
        {
            public string Name;
            public string Mod;
            public double Mb;
            public int Count;
            public int Overlapped;
        }

        public HeavyItemTracker(ManualLogSource log)
        {
            _log = log;
            s_instance = this;
        }

        /// <summary>Set every second: enabled and in a raid past its loading phase.</summary>
        public volatile bool Active;

        private string _status;

        public string Status { get => _status ?? Loc.L("꺼짐", "off"); private set => _status = value; }

        public int Loads { get; private set; }

        public void Install()
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.LoadBundlesAndCreatePools),
                    new[]
                    {
                        typeof(ObjectsFactory.Pools), typeof(List<ObjectsFactory.PoolResourceInfo>), typeof(ObjectsFactory.AssemblyType),
                        typeof(YieldDelegate), typeof(IProgress<InitLevelProgress>), typeof(CancellationToken),
                    });
                if (target == null)
                {
                    Status = Loc.L("설치 실패: 게임의 번들 로드 함수를 못 찾음", "install failed: the game's bundle loading function was not found");
                    return;
                }

                var harmony = new Harmony("com.cactuspie.ramcleanerinterval.heavyitems");
                harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(HeavyItemTracker), nameof(Prefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(HeavyItemTracker), nameof(Postfix))));
                Status = Loc.L("설치됨 — 레이드 중 봇 장비 로드를 측정", "installed — measuring bot gear loads in raid");
                _log.LogInfo("[heavy items] experimental bundle-load measurement installed");
            }
            catch (Exception ex)
            {
                Status = Loc.L("설치 실패: ", "install failed: ") + ex.Message;
                _log.LogWarning($"[heavy items] install failed: {ex}");
            }
        }

        private static void Prefix(List<ObjectsFactory.PoolResourceInfo> resources, out object __state)
        {
            __state = null;
            HeavyItemTracker self = s_instance;
            if (self == null || !self.Active || resources == null)
            {
                return;
            }

            try
            {
                __state = self.Begin(resources);
            }
            catch (Exception)
            {
                // measurement only
            }
        }

        private static void Postfix(object __state, Task __result)
        {
            if (!(__state is Pending pending) || __result == null)
            {
                return;
            }

            HeavyItemTracker self = s_instance;
            __result.ContinueWith(_ => self?.End(pending), TaskContinuationOptions.ExecuteSynchronously);
        }

        private Pending Begin(List<ObjectsFactory.PoolResourceInfo> resources)
        {
            var fresh = new List<string>();
            lock (_gate)
            {
                foreach (ObjectsFactory.PoolResourceInfo info in resources)
                {
                    string path = info.ResourceKey?.path;
                    if (!string.IsNullOrEmpty(path) && _seenThisRaid.Add(path))
                    {
                        fresh.Add(path);
                    }
                }

                if (fresh.Count == 0)
                {
                    return null;
                }

                var pending = new Pending { Paths = fresh.ToArray(), Start = MemoryStats.ReadPrivateBytes() };
                if (pending.Start <= 0)
                {
                    return null;
                }

                if (_running.Count > 0)
                {
                    pending.Overlapped = true;
                    foreach (Pending other in _running)
                    {
                        other.Overlapped = true;
                    }
                }

                _running.Add(pending);
                return pending;
            }
        }

        private void End(Pending pending)
        {
            long end = MemoryStats.ReadPrivateBytes();
            lock (_gate)
            {
                _running.Remove(pending);
            }

            pending.Delta = end > 0 ? end - pending.Start : 0;
            _done.Enqueue(pending);
        }

        /// <summary>Fold finished measurements into the ranking (main thread, once per second).</summary>
        public void Drain()
        {
            while (_done.TryDequeue(out Pending pending))
            {
                Loads++;
                double each = Math.Max(0, pending.Delta) / (double)Mb / pending.Paths.Length;
                foreach (string path in pending.Paths)
                {
                    string mod = ModOf(path);
                    Add(_bundles, path, Path.GetFileNameWithoutExtension(path), mod, each, pending.Overlapped);
                    Add(_mods, mod, mod, mod, each, pending.Overlapped);
                }
            }
        }

        private static void Add(Dictionary<string, Stat> table, string key, string name, string mod, double mb, bool overlapped)
        {
            if (!table.TryGetValue(key, out Stat stat))
            {
                stat = new Stat { Name = name, Mod = mod };
                table[key] = stat;
            }

            stat.Mb += mb;
            stat.Count++;
            if (overlapped)
            {
                stat.Overlapped++;
            }
        }

        public void ResetRaid()
        {
            lock (_gate)
            {
                _seenThisRaid.Clear();
                _running.Clear();
            }

            while (_done.TryDequeue(out _))
            {
            }
        }

        /// <summary>Mods by first-load memory over the whole session, biggest first.</summary>
        public List<Stat> TopMods(int count) => _mods.Values.OrderByDescending(x => x.Mb).Take(count).ToList();

        /// <summary>Bundles by first-load memory per load, biggest first (only bundles seen at least once).</summary>
        public List<Stat> TopBundles(int count) => _bundles.Values.OrderByDescending(x => x.Mb / Math.Max(1, x.Count)).Take(count).ToList();

        /// <summary>Bundle key → mod folder name from SPT's BundleManager; "게임 기본" for anything not listed.</summary>
        private string ModOf(string path)
        {
            if (_bundleMods == null)
            {
                _bundleMods = LoadBundleMods();
            }

            return _bundleMods.TryGetValue(path, out string mod) ? mod : Loc.L("게임 기본", "base game");
        }

        private Dictionary<string, string> LoadBundleMods()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Type manager = AccessTools.TypeByName("SPT.Custom.Utils.BundleManager");
                object bundles = manager == null ? null : AccessTools.Field(manager, "Bundles")?.GetValue(null);
                if (!(bundles is IEnumerable entries))
                {
                    _log.LogInfo("[heavy items] SPT BundleManager not found - every bundle counts as the game's own");
                    return map;
                }

                FieldInfo modPathField = null;
                foreach (object entry in entries)
                {
                    Type entryType = entry.GetType();
                    string key = entryType.GetProperty("Key")?.GetValue(entry) as string;
                    object item = entryType.GetProperty("Value")?.GetValue(entry);
                    if (key == null || item == null)
                    {
                        continue;
                    }

                    modPathField = modPathField ?? AccessTools.Field(item.GetType(), "ModPath");
                    string modPath = modPathField?.GetValue(item) as string;
                    if (!string.IsNullOrEmpty(modPath))
                    {
                        map[key] = Path.GetFileName(modPath.TrimEnd('/', '\\'));
                    }
                }

                _log.LogInfo($"[heavy items] {map.Count} mod bundles known from SPT's BundleManager");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[heavy items] could not read SPT's bundle list: {ex.Message}");
            }

            return map;
        }

        public string DescribeForLog(int count)
        {
            if (_mods.Count == 0)
            {
                return "no first-time bundle loads measured yet";
            }

            return "mods: " + string.Join(", ", TopMods(count).Select(x => $"{x.Mod} {x.Mb:0} MB/{x.Count} bundles" + (x.Overlapped > 0 ? $" ({x.Overlapped} overlapped)" : string.Empty))) +
                   " | bundles: " + string.Join(", ", TopBundles(count).Select(x => $"{x.Name} [{x.Mod}] {x.Mb / Math.Max(1, x.Count):0} MB"));
        }
    }
}
