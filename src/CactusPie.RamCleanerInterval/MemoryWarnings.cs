using System;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Two warnings worth interrupting the player for:
    /// - commit limit almost used up: once RAM + page file is exhausted the game crashes on the next allocation
    ///   (a real raid reached 74.7 GB committed on a 64 GB PC, 2026-09-29);
    /// - VRAM full for a while: textures spill into shared (system) memory, a common stutter source
    ///   (15.5 of 16 GB in every SAIN-sim log).
    /// </summary>
    internal sealed class MemoryWarnings
    {
        private const float CommitCooldownSeconds = 300f;
        private const long CommitFloorBytes = 2L * 1024 * 1024 * 1024;

        private float _lastCommitWarning = float.NegativeInfinity;
        private float _vramFullSince = -1f;
        private bool _vramWarnedThisRaid;

        private string _lastText;

        public string LastText { get => _lastText ?? Loc.L("없음", "none"); private set => _lastText = value; }

        public void ResetRaid()
        {
            _vramFullSince = -1f;
            _vramWarnedThisRaid = false;
        }

        /// <summary>Returns a Korean warning to show, or null. Call once per second.</summary>
        public string Evaluate(MemorySnapshot s, VramMonitor.Reading vram, int commitPercent, bool vramEnabled, int vramPercent, int vramSeconds, float spillGb, float now)
        {
            if (s.CommitLimit > 0)
            {
                double freePercent = s.CommitAvailable * 100.0 / s.CommitLimit;
                if ((freePercent < commitPercent || s.CommitAvailable < CommitFloorBytes) &&
                    now - _lastCommitWarning >= CommitCooldownSeconds)
                {
                    _lastCommitWarning = now;
                    return Remember(Loc.L(
                        $"[경고] 메모리 한도 임박: 남은 커밋 {MemoryStats.Gb(s.CommitAvailable)}GB / 한도 {MemoryStats.Gb(s.CommitLimit)}GB " +
                        $"({freePercent:0}%). 한도를 넘으면 게임이 튕깁니다 — 페이지 파일을 늘리거나 이번 레이드를 마무리하세요.",
                        $"[Warning] Memory limit close: {MemoryStats.Gb(s.CommitAvailable)} GB commit left of {MemoryStats.Gb(s.CommitLimit)} GB " +
                        $"({freePercent:0}%). Past the limit the game crashes — enlarge the page file or wrap up this raid."));
                }
            }

            // The whole card the game runs on (every program on it, e.g. single-GPU frame generation), else the game alone.
            long vramTotal = vram != null && vram.CardTotal > 0 ? vram.CardTotal : (long)SystemInfo.graphicsMemorySize * 1024 * 1024;
            long vramDedicated = vram == null ? -1 : vram.CardUsed > 0 ? vram.CardUsed : vram.Game;
            if (!vramEnabled || vramDedicated <= 0 || vramTotal <= 0)
            {
                return null;
            }

            // A full card alone isn't a problem (games fill VRAM on purpose); it is once memory spills over into system RAM.
            long spill = vram.GameShared;
            double vramUsed = vramDedicated * 100.0 / vramTotal;
            if (vramUsed < vramPercent || (spillGb > 0f && spill >= 0 && spill < spillGb * MemoryStats.BytesPerGb))
            {
                _vramFullSince = -1f;
                return null;
            }

            if (_vramFullSince < 0f)
            {
                _vramFullSince = now;
            }

            if (_vramWarnedThisRaid || now - _vramFullSince < vramSeconds)
            {
                return null;
            }

            _vramWarnedThisRaid = true;
            return Remember(Loc.L(
                $"[경고] VRAM 넘침: 그래픽카드 {MemoryStats.Gb(vramDedicated)} / {vramTotal / MemoryStats.BytesPerGb:0.0}GB ({vramUsed:0}%), 시스템 메모리로 넘친 양 {MemoryStats.Gb(spill)}GB 상태가 " +
                $"{vramSeconds}초 넘게 계속됨. 넘친 텍스처는 끊김 원인이 될 수 있습니다 — 텍스처 품질을 한 단계 낮춰 보세요.",
                $"[Warning] VRAM spilling: card {MemoryStats.Gb(vramDedicated)} / {vramTotal / MemoryStats.BytesPerGb:0.0} GB ({vramUsed:0}%), {MemoryStats.Gb(spill)} GB spilled into system memory for more than " +
                $"{vramSeconds} s. Spilled textures can cause stutter — try one step lower texture quality."));
        }

        private string Remember(string text)
        {
            LastText = $"{DateTime.Now:HH:mm:ss} {text}";
            return text;
        }
    }
}
