using System.Collections.Generic;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>Feeding <see cref="LeakCulprits"/> from the trackers and handing its list to the session report.</summary>
    public partial class CustomRamCleanerIntervalPlugin
    {
        /// <summary>Bot gear first-load memory worth a row of its own (smaller mods only show when they have other evidence).</summary>
        private const double GearRowMb = 300;

        private float _culpritReportNext;

        private List<LeakCulprits.Row> CulpritRows() => _culprits.Rows(_allocSuspectMbPerMin.Value, _objectsSuspectGrowth.Value);

        /// <summary>Once a second (from the memory suspect check): the latest numbers in, the report's copy every 10 s.</summary>
        private void FeedCulprits()
        {
            if (_profilerEnabled.Value && _profiler.LastAlloc.Count > 0)
            {
                _culprits.AddAlloc(_profiler.LastAlloc, _profiler.AllocSuspect, _allocSuspectMbPerMin.Value);
            }

            if (_heavyEnabled.Value && _heavy.Loads > 0)
            {
                _culprits.SetGear(_heavy.TopMods(10), GearRowMb);
            }

            if (Time.unscaledTime >= _culpritReportNext)
            {
                _culpritReportNext = Time.unscaledTime + 10f;
                UpdateCulpritReport();
            }
        }

        /// <summary>"1. SAIN [leaking] held last raid 1,234 · 2 raid(s) / 2. ..." or the empty text, for notifications and the log.</summary>
        private string CulpritSummary(int top)
        {
            List<LeakCulprits.Row> rows = CulpritRows();
            if (rows.Count == 0)
            {
                return _culprits.EmptyText;
            }

            var parts = new List<string>();
            for (int i = 0; i < rows.Count && i < top; i++)
            {
                parts.Add($"{i + 1}. {rows[i].Mod} [{rows[i].Verdict}] {rows[i].Short}");
            }

            return string.Join(" / ", parts);
        }

        private void UpdateCulpritReport()
        {
            bool show = LeakHunt || _culprits.Any;
            _sessionReport.SetCulprits(show ? CulpritRows() : null, _culprits.EmptyText, _culprits.Scans);
        }
    }
}
