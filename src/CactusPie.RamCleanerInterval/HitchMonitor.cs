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

        public void Reset()
        {
            Count = 0;
            CountOver100 = 0;
            Ours = 0;
            MaxMs = 0f;
            MaxWhat = "-";
            LastText = "아직 없음";
            _suppressed = 0;
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
        public void Check(bool active, float thresholdMs, System.Func<string> context)
        {
            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (!active || !Application.isFocused || frameMs < thresholdMs)
            {
                Clear();
                return;
            }

            Judge(frameMs, thresholdMs, context);
            Clear();
        }

        private void Clear()
        {
            _prevGcMs = _prevLeakMs = 0;
            _prevAssetUnload = _prevTrimRunning = false;
        }

        private void Judge(float frameMs, float thresholdMs, System.Func<string> context)
        {

            string ours = DescribeOurs(out bool isOurs);
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
                MaxWhat = isOurs ? ours : "다른 원인";
            }

            float now = Time.realtimeSinceStartup;
            LastText = $"{System.DateTime.Now:HH:mm:ss} {frameMs:0}ms — {(isOurs ? "RAM 클리너: " + ours : "RAM 클리너 아님")}";

            // Rate limit: a stutter storm (loading, alt-tab) must not flood the log.
            if (now - _lastLogTime < MinLogGapSeconds)
            {
                _suppressed++;
                return;
            }

            string extra = _suppressed > 0 ? $" (+{_suppressed} more long frames in the last moments)" : string.Empty;
            _suppressed = 0;
            _lastLogTime = now;
            _log.LogInfo($"[hitch] {frameMs:0}ms frame (threshold {thresholdMs:0}) — " +
                         (isOurs ? "RAM cleaner: " + DescribeOursEnglish() : "not RAM cleaner") +
                         $" | {context()}{extra}");
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
