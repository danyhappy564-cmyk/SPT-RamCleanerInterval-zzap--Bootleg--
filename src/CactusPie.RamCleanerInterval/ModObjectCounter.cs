using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Counts the components each mod has in memory (MonoBehaviours whose class lives in the mod's DLL) and
    /// reports which mod's count keeps growing during the raid. A mod that attaches a component to every bot,
    /// bullet or effect and never removes it shows up here by name.
    /// Limit: memory a mod causes without its own component (e.g. a bot spawner choosing gear the game then
    /// builds) cannot be attributed this way - that shows up as "memory per death" instead.
    /// </summary>
    internal sealed class ModObjectCounter
    {
        private readonly ManualLogSource _log;
        private int[] _baseline;

        public ModObjectCounter(ManualLogSource log)
        {
            _log = log;
        }

        public string Suspect { get; private set; }

        public string LastSummary { get; private set; } = "아직 없음";

        public void Reset()
        {
            _baseline = null;
            Suspect = null;
        }

        public long Snapshot(int suspectGrowth)
        {
            var watch = Stopwatch.StartNew();
            ModRegistry.Build();
            var counts = new int[ModRegistry.Count];
            var typeMod = new Dictionary<Type, int>();
            MonoBehaviour[] all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour behaviour = all[i];
                if (behaviour == null)
                {
                    continue;
                }

                Type type = behaviour.GetType();
                if (!typeMod.TryGetValue(type, out int mod))
                {
                    mod = ModRegistry.IndexOf(type.Assembly);
                    typeMod[type] = mod;
                }

                if (mod >= 0)
                {
                    counts[mod]++;
                }
            }

            watch.Stop();
            if (_baseline == null)
            {
                _baseline = counts;
                LastSummary = $"{DateTime.Now:HH:mm:ss} 기준점 저장 ({watch.ElapsedMilliseconds}ms)";
                _log.LogInfo($"[mod objects] baseline in {watch.ElapsedMilliseconds}ms: " + Top(counts));
                return watch.ElapsedMilliseconds;
            }

            var growth = Enumerable.Range(0, counts.Length)
                .Select(i => new { Mod = i, Delta = counts[i] - (i < _baseline.Length ? _baseline[i] : 0), Count = counts[i] })
                .Where(x => x.Delta > 0)
                .OrderByDescending(x => x.Delta)
                .ToList();

            Suspect = growth.Count > 0 && growth[0].Delta >= suspectGrowth
                ? $"{ModRegistry.Name(growth[0].Mod)} — 레이드 중 컴포넌트 +{growth[0].Delta}개 (지금 {growth[0].Count}개)"
                : null;
            LastSummary = $"{DateTime.Now:HH:mm:ss} " + (Suspect != null ? "의심: " + Suspect : "계속 늘어나는 모드 없음") + $" ({watch.ElapsedMilliseconds}ms)";
            _log.LogInfo($"[mod objects] in {watch.ElapsedMilliseconds}ms, grown since raid start: " +
                         (growth.Count > 0 ? string.Join(", ", growth.Take(8).Select(x => $"{ModRegistry.Name(x.Mod)} {x.Count} (+{x.Delta})")) : "(none)") +
                         (Suspect != null ? $" | SUSPECT: {Suspect}" : string.Empty));
            return watch.ElapsedMilliseconds;
        }

        private static string Top(int[] counts)
        {
            return string.Join(", ", Enumerable.Range(0, counts.Length)
                .Where(i => counts[i] > 0)
                .OrderByDescending(i => counts[i])
                .Take(8)
                .Select(i => $"{ModRegistry.Name(i)} {counts[i]}"));
        }
    }
}
