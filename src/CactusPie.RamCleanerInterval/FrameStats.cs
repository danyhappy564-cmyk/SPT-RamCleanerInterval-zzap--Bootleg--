using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Current FPS (smoothed over half a second), raid average FPS and "1% low" (the FPS of the slowest 1% of
    /// frames, i.e. how bad the stutters feel). Frame times go into a 1 ms histogram, so the percentile costs
    /// nothing per frame and no memory grows during the raid.
    /// </summary>
    internal sealed class FrameStats
    {
        private const int Buckets = 500; // 0..499 ms, last bucket = 499+

        private readonly int[] _histogram = new int[Buckets];
        private float _windowTime;
        private int _windowFrames;

        public float CurrentFps { get; private set; }

        public int Frames { get; private set; }

        public double TotalSeconds { get; private set; }

        public float AverageFps => TotalSeconds > 0 ? (float)(Frames / TotalSeconds) : 0f;

        public void Reset()
        {
            System.Array.Clear(_histogram, 0, _histogram.Length);
            Frames = 0;
            TotalSeconds = 0;
        }

        /// <summary>Call every frame. <paramref name="record"/> false = only the live FPS (menus, loading).</summary>
        public void Tick(bool record)
        {
            float dt = Time.unscaledDeltaTime;
            _windowTime += dt;
            _windowFrames++;
            if (_windowTime >= 0.5f)
            {
                CurrentFps = _windowFrames / _windowTime;
                _windowTime = 0f;
                _windowFrames = 0;
            }

            if (!record || dt <= 0f || !Application.isFocused)
            {
                return;
            }

            Frames++;
            TotalSeconds += dt;
            int bucket = Mathf.Clamp((int)(dt * 1000f), 0, Buckets - 1);
            _histogram[bucket]++;
        }

        /// <summary>FPS of the slowest 1% of frames (0 when there are too few frames).</summary>
        public float OnePercentLowFps()
        {
            if (Frames < 200)
            {
                return 0f;
            }

            int target = Mathf.Max(1, Frames / 100);
            int seen = 0;
            for (int ms = Buckets - 1; ms >= 0; ms--)
            {
                seen += _histogram[ms];
                if (seen >= target)
                {
                    return 1000f / (ms + 1f);
                }
            }

            return 0f;
        }
    }
}
