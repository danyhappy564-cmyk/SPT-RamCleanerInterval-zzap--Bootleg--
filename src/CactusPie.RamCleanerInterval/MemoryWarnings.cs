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

        public string LastText { get; private set; } = "없음";

        public void ResetRaid()
        {
            _vramFullSince = -1f;
            _vramWarnedThisRaid = false;
        }

        /// <summary>Returns a Korean warning to show, or null. Call once per second.</summary>
        public string Evaluate(MemorySnapshot s, long vramDedicated, int commitPercent, bool vramEnabled, int vramPercent, int vramSeconds, float now)
        {
            if (s.CommitLimit > 0)
            {
                double freePercent = s.CommitAvailable * 100.0 / s.CommitLimit;
                if ((freePercent < commitPercent || s.CommitAvailable < CommitFloorBytes) &&
                    now - _lastCommitWarning >= CommitCooldownSeconds)
                {
                    _lastCommitWarning = now;
                    return Remember($"[경고] 메모리 한도 임박: 남은 커밋 {MemoryStats.Gb(s.CommitAvailable)}GB / 한도 {MemoryStats.Gb(s.CommitLimit)}GB " +
                                    $"({freePercent:0}%). 한도를 넘으면 게임이 튕깁니다 — 페이지 파일을 늘리거나 이번 레이드를 마무리하세요.");
                }
            }

            long vramTotal = (long)SystemInfo.graphicsMemorySize * 1024 * 1024;
            if (!vramEnabled || vramDedicated <= 0 || vramTotal <= 0)
            {
                return null;
            }

            double vramUsed = vramDedicated * 100.0 / vramTotal;
            if (vramUsed < vramPercent)
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
            return Remember($"[경고] VRAM 포화: {MemoryStats.Gb(vramDedicated)} / {vramTotal / MemoryStats.BytesPerGb:0.0}GB ({vramUsed:0}%) 상태가 " +
                            $"{vramSeconds}초 넘게 계속됨. 넘친 텍스처는 시스템 메모리로 가서 끊김 원인이 될 수 있습니다 — 텍스처 품질을 한 단계 낮춰 보세요.");
        }

        private string Remember(string text)
        {
            LastText = $"{DateTime.Now:HH:mm:ss} {text}";
            return text;
        }
    }
}
