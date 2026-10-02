using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Logs frames that took longer than a threshold and says whether this plugin was doing something heavy in
    /// that frame (a GC slice, an asset unload, a leak snapshot, a background working-set trim). The question it
    /// answers: "is this stutter the RAM cleaner, or something else?"
    ///
    /// Time.unscaledDeltaTime read at the start of frame N is the length of frame N-1, so at the start of each
    /// Update the plugin first reports what it did in the previous frame (Note*), then calls <see cref="Check"/>.
    /// Costs one float compare per frame; it only builds a string when a frame is actually long.
    /// </summary>
    internal sealed class HitchMonitor
    {
        private const float MinLogGapSeconds = 0.5f;

        private readonly ManualLogSource _log;

        // What this plugin did in the previous frame (the one Check measures).
        private double _prevGcMs;
        private double _prevLeakMs;
        private bool _prevAssetUnload;
        private bool _prevTrimRunning;

        public const string CauseRamCleaner = "RAM 클리너 (GC 등)";
        public const string CauseSpawn = "봇 스폰";
        public const string CauseGcIndirect = "GC 진행 중 (간접)";
        public const string CauseGame = "게임 자체 / 측정 밖";

        private sealed class CauseStats
        {
            public int Count;
            public float MaxMs;
            public double TotalMs;
        }

        private readonly Dictionary<string, CauseStats> _causes = new Dictionary<string, CauseStats>();

        private float _lastLogTime = float.NegativeInfinity;
        private int _suppressed;

        public HitchMonitor(ManualLogSource log)
        {
            _log = log;
        }

        public int Count { get; private set; }

        public int CountOver100 { get; private set; }

        public int Ours { get; private set; }

        public float MaxMs { get; private set; }

        public string MaxWhat { get; private set; } = "-";

        public string LastText { get; private set; } = "아직 없음";

        /// <summary>A cause (mod, spawn, RAM cleaner) behind >= 30% of this raid's long frames and >= 3 of them; null otherwise.</summary>
        public string Suspect { get; private set; }

        /// <summary>Causes of this raid's long frames: (name, count, worst ms), most frequent first.</summary>
        public List<KeyValuePair<string, KeyValuePair<int, float>>> Causes()
        {
            return _causes
                .OrderByDescending(kv => kv.Value.Count)
                .ThenByDescending(kv => kv.Value.MaxMs)
                .Select(kv => new KeyValuePair<string, KeyValuePair<int, float>>(kv.Key, new KeyValuePair<int, float>(kv.Value.Count, kv.Value.MaxMs)))
                .ToList();
        }

        public void Reset()
        {
            Count = 0;
            CountOver100 = 0;
            Ours = 0;
            MaxMs = 0f;
            MaxWhat = "-";
            LastText = "아직 없음";
            _suppressed = 0;
            _causes.Clear();
            Suspect = null;
            Clear();
        }

        public void NoteGc(double ms)
        {
            _prevGcMs += ms;
        }

        public void NoteLeakSnapshot(double ms)
        {
            _prevLeakMs += ms;
        }

        public void NoteAssetUnload()
        {
            _prevAssetUnload = true;
        }

        public void NoteTrimRunning()
        {
            _prevTrimRunning = true;
        }

        /// <summary>Call at the start of Update, after the Note* calls for the previous frame.</summary>
        public void Check(bool active, float thresholdMs, System.Func<string> context, ModCostProfiler profiler, bool spawnRecent, bool gcInProgress)
        {
            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (!active || !Application.isFocused || frameMs < thresholdMs)
            {
                Clear();
                return;
            }

            Judge(frameMs, thresholdMs, context, profiler, spawnRecent, gcInProgress);
            Clear();
        }

        private void Clear()
        {
            _prevGcMs = _prevLeakMs = 0;
            _prevAssetUnload = _prevTrimRunning = false;
        }

        private void Judge(float frameMs, float thresholdMs, System.Func<string> context, ModCostProfiler profiler, bool spawnRecent, bool gcInProgress)
        {
            // Which mod spent the most time in that frame (needs the profiler measuring every frame).
            string topMod = null;
            float topMs = 0f;
            float modsMs = 0f;
            bool attributed = profiler != null && profiler.LastFrameTop(out topMod, out topMs, out modsMs);


            string ours = DescribeOurs(out bool didSomething);

            // Cause, in order: our own measured work when it is a real share of the frame (a 2 ms GC slice in a
            // 300 ms frame is not the cause - 2026-10-02 log), an asset unload (heavy but unmeasured), a mod with a
            // big share, "GC in progress" (frames that are slow while an incremental GC cycle runs, without our
            // slice being big: allocations/write barriers do GC work too), a bot spawn, else the game itself.
            double ourMs = _prevGcMs + _prevLeakMs;
            bool isOurs = ourMs >= System.Math.Max(8.0, frameMs * 0.3) || _prevAssetUnload;
            string cause;
            if (isOurs)
            {
                cause = CauseRamCleaner;
            }
            else if (attributed && topMod != null && topMs >= System.Math.Max(8f, frameMs * 0.3f))
            {
                cause = topMod;
            }
            else if (gcInProgress)
            {
                cause = CauseGcIndirect;
            }
            else if (spawnRecent)
            {
                cause = CauseSpawn;
            }
            else
            {
                cause = CauseGame;
            }

            if (!_causes.TryGetValue(cause, out CauseStats stats))
            {
                stats = new CauseStats();
                _causes[cause] = stats;
            }

            stats.Count++;
            stats.TotalMs += frameMs;
            stats.MaxMs = System.Math.Max(stats.MaxMs, frameMs);
            UpdateSuspect();
            Count++;
            if (frameMs >= 100f)
            {
                CountOver100++;
            }

            if (isOurs)
            {
                Ours++;
            }

            if (frameMs > MaxMs)
            {
                MaxMs = frameMs;
                MaxWhat = isOurs ? ours : cause;
            }

            float now = Time.realtimeSinceStartup;
            LastText = $"{System.DateTime.Now:HH:mm:ss} {frameMs:0}ms — 원인: {cause}" + (didSomething ? $" (이 모드 작업: {ours})" : string.Empty);

            // Rate limit: a stutter storm (loading, alt-tab) must not flood the log.
            if (now - _lastLogTime < MinLogGapSeconds)
            {
                _suppressed++;
                return;
            }

            string extra = _suppressed > 0 ? $" (+{_suppressed} more long frames in the last moments)" : string.Empty;
            _suppressed = 0;
            _lastLogTime = now;
            string modPart = attributed && topMod != null
                ? $" | mod code in that frame {modsMs:0}ms, top {topMod} {topMs:0}ms"
                : attributed ? " | mod code in that frame ~0ms" : string.Empty;
            _log.LogInfo($"[hitch] {frameMs:0}ms frame (threshold {thresholdMs:0}) — cause: {cause}" +
                         (didSomething ? " (RAM cleaner work in that frame: " + DescribeOursEnglish() + ")" : string.Empty) +
                         $"{modPart} | {context()}{extra}");
        }

        private void UpdateSuspect()
        {
            Suspect = null;
            int total = _causes.Values.Sum(c => c.Count);
            KeyValuePair<string, CauseStats> top = _causes
                .Where(kv => kv.Key != CauseGame)
                .OrderByDescending(kv => kv.Value.Count)
                .FirstOrDefault();
            if (top.Key != null && top.Value.Count >= 3 && top.Value.Count >= total * 0.3f)
            {
                Suspect = $"{top.Key} — 끊김 {total}회 중 {top.Value.Count}회의 원인 (최대 {top.Value.MaxMs:0}ms)" +
                          (top.Key == CauseGcIndirect ? " · 자동 GC를 오래 미룰수록 한 번이 커져서 길어짐(04. GC 최대 대기를 줄여 보세요)" : string.Empty);
            }
        }

        private string DescribeOurs(out bool isOurs)
        {
            var sb = new StringBuilder();
            if (_prevGcMs > 1.0)
            {
                sb.Append($"GC {_prevGcMs:0}ms ");
            }

            if (_prevLeakMs > 1.0)
            {
                sb.Append($"누수 추적 {_prevLeakMs:0}ms ");
            }

            if (_prevAssetUnload)
            {
                sb.Append("에셋 정리 중 ");
            }

            if (_prevTrimRunning)
            {
                sb.Append("워킹셋 정리 중(별도 스레드) ");
            }

            isOurs = sb.Length > 0;
            return sb.ToString().TrimEnd();
        }

        private string DescribeOursEnglish()
        {
            var sb = new StringBuilder();
            if (_prevGcMs > 1.0)
            {
                sb.Append($"GC slice {_prevGcMs:0}ms, ");
            }

            if (_prevLeakMs > 1.0)
            {
                sb.Append($"leak snapshot {_prevLeakMs:0}ms, ");
            }

            if (_prevAssetUnload)
            {
                sb.Append("asset unload running, ");
            }

            if (_prevTrimRunning)
            {
                sb.Append("working set trim running (background), ");
            }

            return sb.ToString().TrimEnd(',', ' ');
        }
    }
}
