using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Keeps one line per finished raid in BepInEx\config\RamCleaner.performance.txt (date, map, minutes, average
    /// FPS, 1% low, deaths, mod fingerprint) and compares a new raid against the previous one on the same map:
    /// "average FPS dropped 19% since the last raid on this map - mods added/updated in between: ...".
    /// </summary>
    internal sealed class PerformanceHistory
    {
        private const int MaxRecords = 60;
        private const char Sep = '|';

        private readonly ManualLogSource _log;
        private readonly string _path;

        public PerformanceHistory(ManualLogSource log)
        {
            _log = log;
            _path = Path.Combine(BepInEx.Paths.ConfigPath, "RamCleaner.performance.txt");
        }

        private string _lastComparison;

        public string LastComparison { get => _lastComparison ?? Loc.L("아직 없음", "none yet"); private set => _lastComparison = value; }

        private sealed class Record
        {
            public DateTime Date;
            public string Map;
            public float Minutes;
            public float AvgFps;
            public float LowFps;
            public int Deaths;
            public List<string> Mods = new List<string>();
        }

        /// <summary>
        /// Saves this raid and returns a Korean warning when FPS dropped by at least <paramref name="dropPercent"/>
        /// versus the previous raid on the same map (null otherwise). Raids shorter than 3 minutes are ignored.
        /// </summary>
        public string AddAndCompare(string map, float minutes, float avgFps, float lowFps, int deaths, int dropPercent, out string logLine)
        {
            logLine = null;
            if (minutes < 3f || avgFps <= 0f)
            {
                LastComparison = Loc.L("레이드가 너무 짧아서(3분 미만) 비교 안 함", "raid too short (under 3 min), not compared");
                return null;
            }

            var records = Load();
            var current = new Record
            {
                Date = DateTime.Now,
                Map = string.IsNullOrEmpty(map) ? "?" : map,
                Minutes = minutes,
                AvgFps = avgFps,
                LowFps = lowFps,
                Deaths = deaths,
                Mods = ModRegistry.Fingerprint(),
            };

            Record previous = records.LastOrDefault(r => r.Map == current.Map);
            records.Add(current);
            Save(records);

            if (previous == null)
            {
                LastComparison = Loc.L($"{current.Map}: 첫 기록 (평균 {avgFps:0}fps, 1% 저점 {lowFps:0}fps) — 다음 판부터 비교",
                                       $"{current.Map}: first record (avg {avgFps:0} fps, 1% low {lowFps:0} fps) — compared from the next raid");
                return null;
            }

            float change = (avgFps - previous.AvgFps) / previous.AvgFps * 100f;
            string changes = DescribeModChanges(previous.Mods, current.Mods, out int changedCount);
            LastComparison = Loc.L($"{current.Map}: 이전({previous.Date:MM-dd HH:mm}) {previous.AvgFps:0}fps → 이번 {avgFps:0}fps ({change:+0;-0}%)",
                                   $"{current.Map}: previous ({previous.Date:MM-dd HH:mm}) {previous.AvgFps:0} fps → this {avgFps:0} fps ({change:+0;-0}%)") +
                             (changedCount > 0 ? Loc.L($", 그 사이 바뀐 모드 {changedCount}개", $", {changedCount} mods changed in between") : Loc.L(", 모드 변화 없음", ", no mod changes"));
            logLine = $"[fps history] {current.Map}: previous raid {previous.Date:yyyy-MM-dd HH:mm} avg {previous.AvgFps:0} fps " +
                      $"(1% low {previous.LowFps:0}, deaths {previous.Deaths}) -> this raid {avgFps:0} fps (1% low {lowFps:0}, deaths {deaths}), " +
                      $"{change:+0;-0}% | mod changes: {(changedCount > 0 ? changes : "none")}";

            if (change > -dropPercent)
            {
                return null;
            }

            string because = changedCount > 0
                ? Loc.L($"그 사이 바뀐 모드: {changes}", $"mods changed in between: {changes}")
                : Loc.L("모드 변화는 없음 — 봇 수·맵 상황·그래픽 설정 차이일 수 있음", "no mod changes — could be bot count, map situation or graphics settings");
            return Loc.L($"[경고] 프레임 저하: {current.Map} 평균 {previous.AvgFps:0} → {avgFps:0}fps ({change:0}%), 이전 같은 맵 레이드 대비. {because}",
                         $"[Warning] FPS drop: {current.Map} avg {previous.AvgFps:0} → {avgFps:0} fps ({change:0}%) vs the previous raid on this map. {because}");
        }

        private static string DescribeModChanges(List<string> before, List<string> after, out int count)
        {
            Dictionary<string, string> Parse(List<string> list) => list
                .Select(s => s.Split('@'))
                .Where(p => p.Length >= 3)
                .GroupBy(p => p[0])
                .ToDictionary(g => g.Key, g => g.First()[1] + "@" + g.First()[2]);

            var old = Parse(before);
            var now = Parse(after);
            var parts = new List<string>();
            foreach (var kv in now)
            {
                if (!old.TryGetValue(kv.Key, out string was))
                {
                    parts.Add($"+{kv.Key}" + Loc.L("(추가)", " (added)"));
                }
                else if (was != kv.Value)
                {
                    string oldVersion = was.Split('@')[0];
                    string newVersion = kv.Value.Split('@')[0];
                    parts.Add(oldVersion != newVersion ? $"{kv.Key}({oldVersion}→{newVersion})" : $"{kv.Key}" + Loc.L("(파일 변경)", " (file changed)"));
                }
            }

            foreach (string name in old.Keys)
            {
                if (!now.ContainsKey(name))
                {
                    parts.Add($"-{name}" + Loc.L("(빠짐)", " (removed)"));
                }
            }

            count = parts.Count;
            return parts.Count <= 8 ? string.Join(", ", parts) : string.Join(", ", parts.Take(8)) + Loc.L($" 외 {parts.Count - 8}개", $" and {parts.Count - 8} more");
        }

        private List<Record> Load()
        {
            var records = new List<Record>();
            try
            {
                if (!File.Exists(_path))
                {
                    return records;
                }

                foreach (string line in File.ReadAllLines(_path))
                {
                    string[] p = line.Split(Sep);
                    if (p.Length < 7 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    records.Add(new Record
                    {
                        Date = DateTime.ParseExact(p[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                        Map = p[1],
                        Minutes = float.Parse(p[2], CultureInfo.InvariantCulture),
                        AvgFps = float.Parse(p[3], CultureInfo.InvariantCulture),
                        LowFps = float.Parse(p[4], CultureInfo.InvariantCulture),
                        Deaths = int.Parse(p[5], CultureInfo.InvariantCulture),
                        Mods = p[6].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Performance history unreadable, starting over: {ex.Message}");
                records.Clear();
            }

            return records;
        }

        private void Save(List<Record> records)
        {
            try
            {
                var lines = new List<string> { "# RAM cleaner raid history: date|map|minutes|avg fps|1% low fps|deaths|mods (name@version@dll time)" };
                foreach (Record r in records.Skip(Math.Max(0, records.Count - MaxRecords)))
                {
                    lines.Add(string.Join(Sep.ToString(),
                        r.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                        r.Map.Replace(Sep, '_'),
                        r.Minutes.ToString("0.0", CultureInfo.InvariantCulture),
                        r.AvgFps.ToString("0.0", CultureInfo.InvariantCulture),
                        r.LowFps.ToString("0.0", CultureInfo.InvariantCulture),
                        r.Deaths.ToString(CultureInfo.InvariantCulture),
                        string.Join(";", r.Mods)));
                }

                File.WriteAllLines(_path, lines);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Could not save performance history: {ex.Message}");
            }
        }
    }
}
