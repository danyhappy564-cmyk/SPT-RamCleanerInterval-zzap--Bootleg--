using System;
using System.Collections.Generic;
using System.Linq;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Two predictions from numbers the plugin already samples:
    /// <list type="bullet">
    ///   <item><b>Runway</b> (in raid): how long until memory runs out at the current pace — the commit limit (the game
    ///     crashes there) or physical RAM (heavy paging stutter starts there), whichever comes first — and how many more
    ///     bot deaths that is at the current memory per death. Pace = least-squares slope over the last 10 minutes.</item>
    ///   <item><b>Restart advice</b> (after a raid): memory the game keeps after its post-raid cleanup piles up raid after
    ///     raid (2026-10-02 log: +0.8 GB managed, +13 GB commit). From that per-raid pile-up and the biggest in-raid
    ///     growth seen this session, estimate how many raids fit before the next one would hit the limit.</item>
    /// </list>
    /// System-wide numbers are used on purpose: the server, the browser and everything else share the same RAM.
    /// </summary>
    internal sealed class MemoryForecast
    {
        private const float SampleEverySeconds = 10f;
        private const float WindowSeconds = 600f;
        private const float MinSpanSeconds = 180f;
        private const long Mb = 1024L * 1024L;

        private readonly List<Point> _samples = new List<Point>();
        private readonly List<AfterRaid> _afterRaids = new List<AfterRaid>();
        private float _nextSample;

        private struct Point
        {
            public float Time;
            public long CommitAvailable;
            public long PhysAvailable;
        }

        private struct AfterRaid
        {
            public long GamePrivate;
            public long InRaidGrowth;
        }

        /// <summary>Minutes until the nearer limit, or NaN when memory is not shrinking / not enough data.</summary>
        public double MinutesLeft { get; private set; } = double.NaN;

        /// <summary>"RAM" (physical) or "커밋" (crash limit) — which limit <see cref="MinutesLeft"/> refers to.</summary>
        public string Limit { get; private set; }

        public double DropMbPerMin { get; private set; } = double.NaN;

        /// <summary>Lowest runway seen this raid, minutes (NaN if never computed).</summary>
        public double LowestMinutes { get; private set; } = double.NaN;

        public string RestartText { get; private set; } = "판단 대기 (레이드가 끝나고 메뉴 정리 뒤에 계산)";

        /// <summary>Raids that still fit before a restart is advised; -1 unknown/not needed, 0 = restart now.</summary>
        public int RaidsLeft { get; private set; } = -1;

        public void ResetRaid()
        {
            _samples.Clear();
            _nextSample = 0f;
            MinutesLeft = DropMbPerMin = LowestMinutes = double.NaN;
            Limit = null;
        }

        /// <summary>Call once per second in raid.</summary>
        public void Sample(float now, MemorySnapshot s, long commitFloor, long physFloor)
        {
            if (s.CommitLimit <= 0 || s.SystemTotal <= 0 || now < _nextSample)
            {
                return;
            }

            _nextSample = now + SampleEverySeconds;
            _samples.Add(new Point { Time = now, CommitAvailable = s.CommitAvailable, PhysAvailable = s.SystemAvailable });
            _samples.RemoveAll(x => now - x.Time > WindowSeconds);

            if (_samples.Count < 3 || now - _samples[0].Time < MinSpanSeconds)
            {
                MinutesLeft = DropMbPerMin = double.NaN;
                return;
            }

            double commitDrop = -Slope(x => x.CommitAvailable); // bytes per second, positive = shrinking
            double physDrop = -Slope(x => x.PhysAvailable);
            DropMbPerMin = commitDrop * 60.0 / Mb;

            double commitMinutes = commitDrop > 0 ? (s.CommitAvailable - commitFloor) / commitDrop / 60.0 : double.PositiveInfinity;
            double physMinutes = physDrop > 0 ? (s.SystemAvailable - physFloor) / physDrop / 60.0 : double.PositiveInfinity;
            if (double.IsPositiveInfinity(commitMinutes) && double.IsPositiveInfinity(physMinutes))
            {
                MinutesLeft = double.NaN;
                Limit = null;
                return;
            }

            bool physFirst = physMinutes < commitMinutes;
            MinutesLeft = Math.Max(0, physFirst ? physMinutes : commitMinutes);
            Limit = physFirst ? "RAM" : "커밋";
            if (physFirst)
            {
                DropMbPerMin = physDrop * 60.0 / Mb;
            }

            LowestMinutes = double.IsNaN(LowestMinutes) ? MinutesLeft : Math.Min(LowestMinutes, MinutesLeft);
        }

        /// <summary>Least-squares slope of a value over time, per second.</summary>
        private double Slope(Func<Point, long> value)
        {
            double meanT = _samples.Average(x => x.Time);
            double meanV = _samples.Average(x => (double)value(x));
            double num = 0, den = 0;
            foreach (Point x in _samples)
            {
                double dt = x.Time - meanT;
                num += dt * (value(x) - meanV);
                den += dt * dt;
            }

            return den > 0 ? num / den : 0;
        }

        /// <summary>Korean runway line for the overlay/F12, or null when there is nothing to say.</summary>
        public string DescribeRunway(double perDeathMb, long commitAvailable, long commitFloor)
        {
            if (double.IsNaN(MinutesLeft))
            {
                return _samples.Count > 0 && !double.IsNaN(DropMbPerMin) && DropMbPerMin <= 0
                    ? "여유 예상: 줄어들지 않음"
                    : null;
            }

            string time = MinutesLeft >= 600 ? "10시간 이상" : MinutesLeft >= 120 ? $"약 {MinutesLeft / 60:0.0}시간" : $"약 {MinutesLeft:0}분";
            string what = Limit == "RAM" ? "RAM 부족(끊김 시작)" : "커밋 한도(튕김)";
            string deaths = perDeathMb > 1 && commitAvailable > commitFloor
                ? $" · 봇 약 {(commitAvailable - commitFloor) / Mb / perDeathMb:0}명 더 죽으면 한도"
                : string.Empty;
            return $"여유 예상: {time} 뒤 {what} (분당 -{DropMbPerMin:0}MB){deaths}";
        }

        // ---------------------------------------------------------------- restart advice

        public void ResetSession()
        {
            _afterRaids.Clear();
            RaidsLeft = -1;
        }

        /// <summary>
        /// Call once after the post-raid cleanup is done. <paramref name="inRaidGrowth"/> = peak game commit in the raid
        /// minus game commit at raid start. Returns a Korean notification when a restart is due soon, else null.
        /// </summary>
        public string EvaluateRestart(long gamePrivateNow, long inRaidGrowth, long commitAvailable, long commitFloor)
        {
            if (gamePrivateNow <= 0 || commitAvailable <= 0)
            {
                return null;
            }

            _afterRaids.Add(new AfterRaid { GamePrivate = gamePrivateNow, InRaidGrowth = Math.Max(0, inRaidGrowth) });
            long need = _afterRaids.Skip(Math.Max(0, _afterRaids.Count - 3)).Max(x => x.InRaidGrowth);
            long headroom = commitAvailable - commitFloor;

            if (headroom < need)
            {
                RaidsLeft = 0;
                RestartText = $"지금 재시작 권장 — 다음 레이드 중 메모리 한도에 닿을 수 있음 (레이드 중 늘어나는 양 {MemoryStats.Gb(need)}GB, 남은 여유 {MemoryStats.Gb(Math.Max(0, headroom))}GB)";
                return RestartText;
            }

            if (_afterRaids.Count < 2)
            {
                RaidsLeft = -1;
                RestartText = $"판단 대기 (2판째부터 계산) · 지금 여유 {MemoryStats.Gb(headroom)}GB, 레이드 중 +{MemoryStats.Gb(need)}GB";
                return null;
            }

            long perRaid = (_afterRaids[_afterRaids.Count - 1].GamePrivate - _afterRaids[0].GamePrivate) / (_afterRaids.Count - 1);
            if (perRaid < 200 * Mb)
            {
                RaidsLeft = -1;
                RestartText = $"재시작 필요 없음 (판마다 남는 양 {perRaid / Mb:+0;-0}MB, {_afterRaids.Count}판 기준)";
                return null;
            }

            // The next raid fits (headroom >= need); each raid after it starts perRaid lower.
            RaidsLeft = 1 + (int)((headroom - need) / perRaid);
            RestartText = RaidsLeft <= 1
                ? $"다음 판까지 하고 재시작 권장 (판마다 +{MemoryStats.Gb(perRaid)}GB 남음, 여유 {MemoryStats.Gb(headroom)}GB, 레이드 중 +{MemoryStats.Gb(need)}GB)"
                : $"약 {RaidsLeft}판 더 가능, 그 뒤 재시작 권장 (판마다 +{MemoryStats.Gb(perRaid)}GB 남음, 여유 {MemoryStats.Gb(headroom)}GB, 레이드 중 +{MemoryStats.Gb(need)}GB)";
            return RaidsLeft <= 1 ? RestartText : null;
        }
    }
}
