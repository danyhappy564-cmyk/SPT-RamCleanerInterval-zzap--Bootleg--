using System;
using System.Text;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Collects one raid's numbers and turns them into a one-paragraph summary at raid end: peaks, memory per
    /// death, what the cleaner did, and how many long frames there were (and how many were ours). Makes it
    /// easy to compare raids after changing SAIN / APBS settings without reading the whole log.
    /// </summary>
    internal sealed class RaidReport
    {
        private DateTime _start;
        private long _peakPrivate;
        private long _peakNative;
        private long _peakWorkingSet;
        private long _peakVram;
        private long _minCommitAvailable = long.MaxValue;
        private long _minSystemAvailable = long.MaxValue;
        private long _nativeAtStart = -1;
        private long _nativeAtEnd = -1;
        private int _deadAtStart;
        private int _deadAtEnd;

        public bool Active { get; private set; }

        public int GcCount { get; private set; }

        public long GcReclaimed { get; private set; }

        public double GcMaxSliceMs { get; private set; }

        public int AssetUnloads { get; private set; }

        public int Trims { get; private set; }

        public int Warnings { get; private set; }

        public string LastSummaryKorean { get; private set; } = "아직 없음";

        /// <summary>Length of the last finished raid, minutes.</summary>
        public double LastMinutes { get; private set; }

        /// <summary>Highest game commit (private bytes) seen this raid.</summary>
        public long PeakPrivate => _peakPrivate;

        /// <summary>Lowest system free RAM this raid, bytes (-1 unknown).</summary>
        public long MinSystemAvailable => _minSystemAvailable == long.MaxValue ? -1 : _minSystemAvailable;

        public int Deaths => Math.Max(0, _deadAtEnd - _deadAtStart);

        /// <summary>Native memory per death over the raid, MB (-1 under 3 deaths).</summary>
        public double PerDeathMb => Deaths >= 3 && _nativeAtStart >= 0 ? (_nativeAtEnd - _nativeAtStart) / (1024d * 1024d) / Deaths : -1;

        public void Begin(MemorySnapshot s, int dead)
        {
            Active = true;
            _start = DateTime.Now;
            _peakPrivate = _peakNative = _peakWorkingSet = _peakVram = 0;
            _minCommitAvailable = long.MaxValue;
            _minSystemAvailable = long.MaxValue;
            _nativeAtStart = s.Native;
            _nativeAtEnd = s.Native;
            _deadAtStart = Math.Max(0, dead);
            _deadAtEnd = _deadAtStart;
            GcCount = AssetUnloads = Trims = Warnings = 0;
            GcReclaimed = 0;
            GcMaxSliceMs = 0;
        }

        public void Sample(MemorySnapshot s, long vram, int dead)
        {
            if (!Active)
            {
                return;
            }

            _peakPrivate = Math.Max(_peakPrivate, s.PrivateBytes);
            _peakNative = Math.Max(_peakNative, s.Native);
            _peakWorkingSet = Math.Max(_peakWorkingSet, s.WorkingSet);
            _peakVram = Math.Max(_peakVram, vram);
            if (s.CommitLimit > 0)
            {
                _minCommitAvailable = Math.Min(_minCommitAvailable, s.CommitAvailable);
            }

            if (s.SystemTotal > 0)
            {
                _minSystemAvailable = Math.Min(_minSystemAvailable, s.SystemAvailable);
            }

            if (s.Native >= 0)
            {
                _nativeAtEnd = s.Native;
            }

            if (dead >= 0)
            {
                _deadAtEnd = dead;
            }
        }

        public void AddGc(long reclaimed, double maxSliceMs)
        {
            if (!Active)
            {
                return;
            }

            GcCount++;
            GcReclaimed += Math.Max(0, reclaimed);
            GcMaxSliceMs = Math.Max(GcMaxSliceMs, maxSliceMs);
        }

        public void AddAssetUnload()
        {
            if (Active)
            {
                AssetUnloads++;
            }
        }

        public void AddTrim()
        {
            if (Active)
            {
                Trims++;
            }
        }

        public void AddWarning()
        {
            if (Active)
            {
                Warnings++;
            }
        }

        /// <summary>Ends the raid and returns (log line, short Korean text for the in-game notification).</summary>
        public void End(HitchMonitor hitches, float avgFps, float lowFps, string suspect, out string logLine, out string notification)
        {
            Active = false;
            double minutes = (DateTime.Now - _start).TotalMinutes;
            LastMinutes = minutes;
            int deaths = Math.Max(0, _deadAtEnd - _deadAtStart);
            string perDeath = deaths >= 3 && _nativeAtStart >= 0
                ? $"{(_nativeAtEnd - _nativeAtStart) / (1024d * 1024d) / deaths:0} MB"
                : "n/a";
            string perDeathKo = perDeath == "n/a" ? "표본 부족" : perDeath;
            string commit = _minCommitAvailable == long.MaxValue ? "?" : MemoryStats.Gb(_minCommitAvailable);

            logLine = $"[raid report] {minutes:0} min, deaths {deaths}, per death {perDeath} | " +
                      $"peak private {MemoryStats.Gb(_peakPrivate)} GB, native {MemoryStats.Gb(_peakNative)} GB, " +
                      $"working set {MemoryStats.Gb(_peakWorkingSet)} GB, VRAM {MemoryStats.Gb(_peakVram)} GB, lowest free commit {commit} GB | " +
                      $"GC {GcCount}x reclaimed {MemoryStats.Gb(GcReclaimed)} GB (longest frame {GcMaxSliceMs:0}ms), " +
                      $"asset unloads {AssetUnloads}, trims {Trims}, warnings {Warnings} | " +
                      $"long frames {hitches.Count} (100ms+ {hitches.CountOver100}, by RAM cleaner {hitches.Ours}, worst {hitches.MaxMs:0}ms: {hitches.MaxWhat}) | " +
                      $"fps avg {avgFps:0}, 1% low {lowFps:0}" + (suspect != null ? $" | suspect mod: {suspect}" : string.Empty);

            var sb = new StringBuilder();
            sb.Append($"RAM 클리너 레이드 결산 ({minutes:0}분, 사망 {deaths}명)\n");
            sb.Append($"최고 메모리 {MemoryStats.Gb(_peakPrivate)}GB · 사망 1명당 {perDeathKo}\n");
            sb.Append($"FPS 평균 {avgFps:0} · 1% 저점 {lowFps:0} · 끊김 {hitches.Count}회(이 모드 {hitches.Ours}회, 최대 {hitches.MaxMs:0}ms)\n");
            sb.Append($"GC {GcCount}회 {MemoryStats.Gb(GcReclaimed)}GB 회수");
            if (suspect != null)
            {
                sb.Append($"\n의심 모드: {suspect}");
            }
            notification = sb.ToString();
            LastSummaryKorean = $"{DateTime.Now:HH:mm} — " + notification.Replace("\n", " / ");
        }
    }
}
