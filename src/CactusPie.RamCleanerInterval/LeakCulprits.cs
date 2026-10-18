using System;
using System.Collections.Generic;
using System.Linq;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// "Which mod is leaking?" in one ranked list, for the leak hunt mode: the overlay, the session report and the F12
    /// status. Joins what the separate trackers already measure, per mod, for the whole session (a leak shows raid after raid):
    /// <list type="bullet">
    /// <item>held - after a raid, in the menu, the mod's static fields still point at that raid's destroyed objects
    /// (<see cref="LeakHolderScan"/>): the strongest sign, the memory can't be freed until the game closes;</item>
    /// <item>objects - the mod's own components keep growing in a raid (<see cref="ModObjectCounter"/>);</item>
    /// <item>creation - the mod's code creates a lot of managed memory per minute (<see cref="ModCostProfiler"/>), and
    /// whether that keeps growing during the raid. Creating is not keeping, so on its own this is only a hint;</item>
    /// <item>bot gear - first-load memory of the mod's item bundles (<see cref="HeavyItemTracker"/>): not a leak, but
    /// the usual reason memory climbs with every bot that dies.</item>
    /// </list>
    /// Main thread only. Each input is idempotent (max / latest), so callers may feed it as often as they like.
    /// </summary>
    internal sealed class LeakCulprits
    {
        internal enum Level
        {
            Info = 0, // bot gear only: not a leak
            Hint = 1, // creates a lot of memory
            Suspect = 2, // keeps growing (objects, or creation growing)
            Holding = 3, // held the last raid's objects after it ended
        }

        internal sealed class Row
        {
            public string Mod;
            public Level Level;
            public double Score;
            public string Verdict;
            public string Evidence;
            public string Short; // the same evidence in a few words, for the overlay
        }

        private sealed class Entry
        {
            public int HeldObjects; // last scan that found something for this mod
            public int HeldRaids; // scans (= raids) that found something
            public string HeldField;
            public int ObjectGrowth; // largest component growth seen in one raid
            public int ObjectCount;
            public float AllocPeak; // highest MB/min in any profiler window
            public bool AllocGrowing; // creation grew a lot from the start of a raid
            public double GearMb;
        }

        private readonly Dictionary<string, Entry> _mods = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Holder scans run so far (after-raid, in the menu) and the last one's outcome, for "nothing found yet".</summary>
        public int Scans { get; private set; }

        public bool Any => _mods.Count > 0;

        private Entry Get(string mod)
        {
            if (!_mods.TryGetValue(mod, out Entry entry))
            {
                entry = new Entry();
                _mods[mod] = entry;
            }

            return entry;
        }

        /// <summary>After each after-raid holder scan (also when it found nothing: that counts as a scan).</summary>
        public void AddHolderScan(LeakHolderScan.Result result)
        {
            Scans++;
            foreach (IGrouping<string, LeakHolderScan.Holder> group in result.Holders.GroupBy(h => h.Mod))
            {
                Entry entry = Get(group.Key);
                LeakHolderScan.Holder biggest = group.OrderByDescending(h => h.Count).First();
                entry.HeldObjects = group.Sum(h => h.Count);
                entry.HeldRaids++;
                entry.HeldField = biggest.Field;
            }
        }

        /// <summary>After each mod object count: (mod, growth since raid start, count now).</summary>
        public void AddObjectGrowth(IEnumerable<ModObjectCounter.Growth> growth, int suspectGrowth)
        {
            foreach (ModObjectCounter.Growth g in growth)
            {
                // Small growth is normal (a component per bot); only what reaches half the suspect line is worth a row.
                if (g.Delta < suspectGrowth / 2 && !_mods.ContainsKey(g.Mod))
                {
                    continue;
                }

                Entry entry = Get(g.Mod);
                if (g.Delta > entry.ObjectGrowth)
                {
                    entry.ObjectGrowth = g.Delta;
                    entry.ObjectCount = g.Count;
                }
            }
        }

        /// <summary>Latest profiler window: MB/min per mod, plus its suspect line (names a mod whose creation keeps growing).</summary>
        public void AddAlloc(List<KeyValuePair<string, float>> alloc, string allocSuspect, float suspectMbPerMin)
        {
            foreach (KeyValuePair<string, float> mod in alloc)
            {
                // The suspect line can hold two parts ("A — 45% ... / B — 3 → 40 MB/min"); only a "→" part names a growing mod.
                bool growing = allocSuspect != null && allocSuspect.Split(new[] { " / " }, StringSplitOptions.None)
                    .Any(part => part.StartsWith(mod.Key + " — ", StringComparison.Ordinal) && part.Contains("→"));
                if (mod.Value < suspectMbPerMin && !growing && !_mods.ContainsKey(mod.Key))
                {
                    continue;
                }

                Entry entry = Get(mod.Key);
                entry.AllocPeak = Math.Max(entry.AllocPeak, mod.Value);
                entry.AllocGrowing |= growing;
            }
        }

        /// <summary>Bot gear first-load memory per mod (heavy item finder), when it is on.</summary>
        public void SetGear(IEnumerable<HeavyItemTracker.Stat> mods, double minMb)
        {
            foreach (HeavyItemTracker.Stat stat in mods)
            {
                if (stat.Mb >= minMb || _mods.ContainsKey(stat.Mod))
                {
                    Get(stat.Mod).GearMb = stat.Mb;
                }
            }
        }

        /// <summary>Ranked, strongest evidence first. Current language.</summary>
        public List<Row> Rows(float allocSuspectMbPerMin, int objectSuspectGrowth)
        {
            var rows = new List<Row>();
            foreach (KeyValuePair<string, Entry> pair in _mods)
            {
                Entry e = pair.Value;
                var evidence = new List<string>();
                var brief = new List<string>();
                Level level = Level.Info;
                double score = 0;

                if (e.HeldObjects > 0)
                {
                    level = Level.Holding;
                    score += 1000 + e.HeldRaids * 100 + Math.Min(e.HeldObjects, 10000) / 10.0;
                    evidence.Add(Loc.L($"레이드가 끝난 뒤에도 지난 레이드 물체 {e.HeldObjects:N0}개를 붙잡음({e.HeldRaids}판, {e.HeldField})",
                                       $"still held {e.HeldObjects:N0} objects of the finished raid ({e.HeldRaids} raid(s), {e.HeldField})"));
                    brief.Add(Loc.L($"지난 레이드 붙잡음 {e.HeldObjects:N0}개·{e.HeldRaids}판", $"held last raid {e.HeldObjects:N0} · {e.HeldRaids} raid(s)"));
                }

                if (e.ObjectGrowth > 0)
                {
                    bool over = e.ObjectGrowth >= objectSuspectGrowth;
                    if (over && level < Level.Suspect)
                    {
                        level = Level.Suspect;
                    }

                    score += (over ? 300 : 0) + Math.Min(e.ObjectGrowth, 20000) / 100.0;
                    evidence.Add(Loc.L($"레이드 중 자기 오브젝트 +{e.ObjectGrowth:N0}개(그때 {e.ObjectCount:N0}개)", $"+{e.ObjectGrowth:N0} of its own objects in a raid ({e.ObjectCount:N0} then)"));
                    brief.Add(Loc.L($"오브젝트 +{e.ObjectGrowth:N0}", $"objects +{e.ObjectGrowth:N0}"));
                }

                if (e.AllocPeak > 0)
                {
                    if (e.AllocGrowing && level < Level.Suspect)
                    {
                        level = Level.Suspect;
                    }
                    else if (e.AllocPeak >= allocSuspectMbPerMin && level < Level.Hint)
                    {
                        level = Level.Hint;
                    }

                    score += (e.AllocGrowing ? 250 : 0) + (e.AllocPeak >= allocSuspectMbPerMin ? 100 : 0) + Math.Min(e.AllocPeak, 2000) / 10.0;
                    evidence.Add(Loc.L($"메모리 생성 최고 {e.AllocPeak:0}MB/분" + (e.AllocGrowing ? "(레이드 중 점점 늘어남)" : string.Empty),
                                       $"creates up to {e.AllocPeak:0} MB/min" + (e.AllocGrowing ? " (growing during the raid)" : string.Empty)));
                    brief.Add(Loc.L($"생성 {e.AllocPeak:0}MB/분", $"creates {e.AllocPeak:0} MB/min") + (e.AllocGrowing ? "↑" : string.Empty));
                }

                if (e.GearMb > 0)
                {
                    score += Math.Min(e.GearMb, 5000) / 50.0;
                    evidence.Add(Loc.L($"봇 장비 처음 로드 {e.GearMb:0}MB(누수 아님 — 아이템이 많아서)", $"bot gear first load {e.GearMb:0} MB (not a leak — many items)"));
                    brief.Add(Loc.L($"봇 장비 {e.GearMb:0}MB", $"bot gear {e.GearMb:0} MB"));
                }

                if (evidence.Count == 0)
                {
                    continue;
                }

                rows.Add(new Row { Mod = pair.Key, Level = level, Score = score, Verdict = Verdict(level), Evidence = string.Join(" · ", evidence), Short = string.Join(" · ", brief) });
            }

            return rows.OrderByDescending(r => r.Level).ThenByDescending(r => r.Score).ToList();
        }

        internal static string Verdict(Level level)
        {
            switch (level)
            {
                case Level.Holding: return Loc.L("누수 확실", "leaking");
                case Level.Suspect: return Loc.L("누수 의심", "suspect");
                case Level.Hint: return Loc.L("참고: 많이 만듦", "hint: creates a lot");
                default: return Loc.L("참고: 봇 장비", "info: bot gear");
            }
        }

        /// <summary>What to tell the player when the list is empty.</summary>
        public string EmptyText =>
            Scans == 0
                ? Loc.L("아직 근거 없음 — 레이드를 끝내면 메뉴에서 '지난 레이드를 붙잡은 모드'를 검사합니다. 같은 맵으로 2판 이상 권장.",
                        "No evidence yet — after each raid the menu checks which mod still holds the last raid. Two raids or more on the same map recommended.")
                : Loc.L($"레이드 {Scans}판 검사 — 지난 레이드를 붙잡은 모드 없음, 계속 늘어나는 모드 없음",
                        $"{Scans} raid(s) checked — no mod held the last raid, none keeps growing");
    }
}
