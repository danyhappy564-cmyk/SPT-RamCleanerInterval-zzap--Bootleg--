using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// One HTML page per game session (BepInEx\RamCleaner\RamCleaner-report-*.html): a table of every raid this
    /// session plus, per raid, memory and FPS over time, stutter causes and the advice the plugin gave. Rewritten after
    /// each raid, so it can be opened any time (F12 button). Self-contained - inline CSS/JS, no internet needed.
    /// </summary>
    internal sealed class SessionReport
    {
        private const int KeepFiles = 15;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly ManualLogSource _log;
        private readonly DateTime _sessionStart = DateTime.Now;
        private readonly List<Raid> _raids = new List<Raid>();
        private Raid _current;
        private int _writing;
        private List<HeavyItemTracker.Stat> _heavyMods;
        private List<HeavyItemTracker.Stat> _heavyBundles;

        public sealed class Raid
        {
            public DateTime Start;
            public string Map;
            public float Minutes;
            public float AvgFps;
            public float LowFps;
            public int Deaths;
            public double PerDeathMb = -1;
            public double PeakGameGb;
            public double MinSystemFreeGb = -1;
            public int Hitches;
            public float WorstHitchMs;
            public List<KeyValuePair<string, KeyValuePair<int, float>>> Causes = new List<KeyValuePair<string, KeyValuePair<int, float>>>();
            public double ServerStartGb = -1;
            public double ServerEndGb = -1;
            public string Bots;
            public string Waits;
            public double LowestRunwayMin = double.NaN;
            public string Kept;
            public string Restart;
            public List<string> Suspects = new List<string>();
            public readonly List<double[]> Points = new List<double[]>(); // minute, game GB, system free GB, server GB, fps
        }

        public SessionReport(ManualLogSource log, string directory)
        {
            _log = log;
            Directory = directory;
            FilePath = Path.Combine(directory, $"RamCleaner-report-{_sessionStart:yyyyMMdd-HHmmss}.html");
        }

        public string Directory { get; }

        public string FilePath { get; }

        public bool Written { get; private set; }

        public int RaidCount => _raids.Count;

        public void BeginRaid(string map, double serverGb)
        {
            _current = new Raid { Start = DateTime.Now, Map = string.IsNullOrEmpty(map) ? "?" : map, ServerStartGb = serverGb };
        }

        /// <summary>One point every ~10 s in raid.</summary>
        public void Sample(double minute, double gameGb, double systemFreeGb, double serverGb, double fps)
        {
            _current?.Points.Add(new[] { minute, gameGb, systemFreeGb, serverGb, fps });
        }

        /// <summary>Closes the raid; <paramref name="fill"/> copies the end-of-raid numbers in.</summary>
        public void EndRaid(Action<Raid> fill)
        {
            if (_current == null)
            {
                return;
            }

            fill(_current);
            if (_current.Minutes >= 1f)
            {
                _raids.Add(_current);
            }

            _current = null;
            Write();
        }

        /// <summary>After the post-raid cleanup: what stayed behind and the restart advice.</summary>
        public void AfterRaid(string kept, string restart)
        {
            if (_raids.Count == 0)
            {
                return;
            }

            Raid last = _raids[_raids.Count - 1];
            last.Kept = kept;
            last.Restart = restart;
            Write();
        }

        /// <summary>Experimental heavy-item ranking to include (null/empty = section left out).</summary>
        public void SetHeavy(List<HeavyItemTracker.Stat> mods, List<HeavyItemTracker.Stat> bundles)
        {
            _heavyMods = mods;
            _heavyBundles = bundles;
        }

        public void Write()
        {
            if (_raids.Count == 0)
            {
                return;
            }

            string html;
            try
            {
                html = Build(_heavyMods, _heavyBundles);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[report] could not build the session report: {ex.Message}");
                return;
            }

            if (Interlocked.Exchange(ref _writing, 1) == 1)
            {
                return; // a write is in flight; the next raid rewrites it anyway
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.WriteAllText(FilePath, html, new UTF8Encoding(false));
                    Written = true;
                    Prune();
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"[report] could not write {FilePath}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _writing, 0);
                }
            });
        }

        private void Prune()
        {
            foreach (FileInfo old in new DirectoryInfo(Directory).GetFiles("RamCleaner-report-*.html")
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepFiles))
            {
                try
                {
                    old.Delete();
                }
                catch (Exception)
                {
                    // in use
                }
            }
        }

        // ---------------------------------------------------------------- HTML

        private string Build(List<HeavyItemTracker.Stat> heavyMods, List<HeavyItemTracker.Stat> heavyBundles)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("<!doctype html><html lang=\"" + Loc.L("ko", "en") + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            string title = Loc.L("RAM 클리너 세션 보고서", "RAM Cleaner session report");
            sb.Append($"<title>{title}</title><style>{Css}</style></head><body><main class=\"viz-root\">");
            sb.Append($"<h1>{title}</h1><p class=\"sub\">" +
                      Loc.L($"세션 시작 {_sessionStart:yyyy-MM-dd HH:mm} · 레이드 {_raids.Count}판 · 마지막 갱신 {DateTime.Now:HH:mm:ss}",
                            $"session started {_sessionStart:yyyy-MM-dd HH:mm} · {_raids.Count} raids · updated {DateTime.Now:HH:mm:ss}") + "</p>");

            // --- session table
            sb.Append("<section><h2>").Append(Loc.L("레이드별 요약", "Raids")).Append("</h2><div class=\"scroll\"><table><thead><tr>")
              .Append(Loc.L("<th>#</th><th>시작</th><th>맵</th><th class=n>분</th><th class=n>평균 FPS</th><th class=n>1% 저점</th><th class=n>끊김</th>",
                            "<th>#</th><th>start</th><th>map</th><th class=n>min</th><th class=n>avg FPS</th><th class=n>1% low</th><th class=n>stutters</th>"))
              .Append(Loc.L("<th class=n>게임 최고(GB)</th><th class=n>시스템 최저 여유(GB)</th><th class=n>사망</th><th class=n>사망당(MB)</th>",
                            "<th class=n>game peak (GB)</th><th class=n>lowest system free (GB)</th><th class=n>deaths</th><th class=n>per death (MB)</th>"))
              .Append(Loc.L("<th class=n>서버(GB)</th><th>레이드 후 남은 메모리</th></tr></thead><tbody>",
                            "<th class=n>server (GB)</th><th>memory kept after the raid</th></tr></thead><tbody>"));
            for (int i = 0; i < _raids.Count; i++)
            {
                Raid r = _raids[i];
                sb.Append("<tr>")
                  .Append($"<td><a href=\"#raid{i + 1}\">{i + 1}</a></td><td>{r.Start:HH:mm}</td><td>{E(r.Map)}</td>")
                  .Append($"<td class=n>{F(r.Minutes, "0")}</td><td class=n>{F(r.AvgFps, "0")}</td><td class=n>{F(r.LowFps, "0")}</td>")
                  .Append($"<td class=n>{r.Hitches}</td><td class=n>{F(r.PeakGameGb, "0.0")}</td><td class=n>{(r.MinSystemFreeGb >= 0 ? F(r.MinSystemFreeGb, "0.0") : "-")}</td>")
                  .Append($"<td class=n>{r.Deaths}</td><td class=n>{(r.PerDeathMb >= 0 ? F(r.PerDeathMb, "0") : "-")}</td>")
                  .Append($"<td class=n>{(r.ServerStartGb >= 0 && r.ServerEndGb >= 0 ? $"{F(r.ServerStartGb, "0.0")} → {F(r.ServerEndGb, "0.0")}" : "-")}</td>")
                  .Append($"<td>{E(r.Kept ?? "-")}</td></tr>");
            }

            sb.Append("</tbody></table></div></section>");

            if (_raids.Count >= 2)
            {
                sb.Append("<section><h2>").Append(Loc.L("판별 비교", "Raid by raid")).Append("</h2><div class=\"grid2\">");
                AppendChart(sb, "cmp-fps", Loc.L("평균 FPS (판별)", "Average FPS per raid"), "fps", _raids.Select((r, i) => (double)(i + 1)).ToList(), true,
                    new[] { Series(Loc.L("평균 FPS", "avg FPS"), 1, _raids.Select(r => (double)r.AvgFps)), Series(Loc.L("1% 저점", "1% low"), 2, _raids.Select(r => (double)r.LowFps)) });
                AppendChart(sb, "cmp-mem", Loc.L("게임 최고 메모리 (GB, 판별)", "Game peak memory per raid (GB)"), "GB", _raids.Select((r, i) => (double)(i + 1)).ToList(), true,
                    new[] { Series(Loc.L("게임 최고 메모리", "game peak memory"), 1, _raids.Select(r => r.PeakGameGb)) });
                sb.Append("</div></section>");
            }

            // --- per raid
            for (int i = 0; i < _raids.Count; i++)
            {
                Raid r = _raids[i];
                sb.Append($"<section id=\"raid{i + 1}\" class=\"card\"><h2>" +
                          Loc.L($"{i + 1}판 — {E(r.Map)} · {r.Start:HH:mm} · {F(r.Minutes, "0")}분", $"Raid {i + 1} — {E(r.Map)} · {r.Start:HH:mm} · {F(r.Minutes, "0")} min") + "</h2>");
                sb.Append("<div class=\"tiles\">")
                  .Append(Tile(Loc.L("평균 FPS", "Average FPS"), F(r.AvgFps, "0"), Loc.L("1% 저점 ", "1% low ") + F(r.LowFps, "0")))
                  .Append(Tile(Loc.L("끊김", "Stutters"), r.Hitches.ToString(Inv),
                      r.Hitches > 0 ? Loc.L("최대 ", "worst ") + F(r.WorstHitchMs, "0") + " ms" : Loc.L("없음", "none")))
                  .Append(Tile(Loc.L("게임 최고 메모리", "Game peak memory"), F(r.PeakGameGb, "0.0") + " GB",
                      Loc.L("사망당 ", "per death ") + (r.PerDeathMb >= 0 ? F(r.PerDeathMb, "0") + " MB" : "-")))
                  .Append(Tile(Loc.L("가장 짧았던 여유 예상", "Shortest time left"),
                      double.IsNaN(r.LowestRunwayMin) ? "-" : r.LowestRunwayMin >= 600 ? Loc.L("10시간+", "10 h+") : F(r.LowestRunwayMin, "0") + Loc.L("분", " min"),
                      Loc.L("메모리 한도까지", "until the memory limit")))
                  .Append("</div>");

                if (r.Points.Count >= 2)
                {
                    List<double> x = r.Points.Select(p => p[0]).ToList();
                    var memory = new List<string>
                    {
                        Series(Loc.L("게임 커밋", "game commit"), 1, r.Points.Select(p => p[1])),
                        Series(Loc.L("시스템 여유 RAM", "system free RAM"), 2, r.Points.Select(p => p[2])),
                    };
                    if (r.Points.Any(p => p[3] > 0))
                    {
                        memory.Add(Series(Loc.L("SPT 서버", "SPT server"), 3, r.Points.Select(p => p[3] > 0 ? p[3] : double.NaN)));
                    }

                    sb.Append("<div class=\"grid2\">");
                    AppendChart(sb, $"mem{i}", Loc.L("메모리 (GB)", "Memory (GB)"), "GB", x, false, memory.ToArray());
                    AppendChart(sb, $"fps{i}", Loc.L("FPS (10초 평균)", "FPS (10 s average)"), "fps", x, false, new[] { Series("FPS", 1, r.Points.Select(p => p[4])) });
                    sb.Append("</div>");
                }

                if (r.Causes.Count > 0)
                {
                    int most = Math.Max(1, r.Causes.Max(c => c.Value.Key));
                    sb.Append("<h3>").Append(Loc.L("끊김 원인", "Stutter causes")).Append("</h3><div class=\"bars\">");
                    foreach (KeyValuePair<string, KeyValuePair<int, float>> c in r.Causes.Take(8))
                    {
                        sb.Append(Bar(c.Key, c.Value.Key / (double)most, Loc.L($"{c.Value.Key}회 · 최대 {F(c.Value.Value, "0")}ms", $"{c.Value.Key}x · worst {F(c.Value.Value, "0")} ms")));
                    }

                    sb.Append("</div>");
                }

                var notes = new List<string>();
                if (r.Bots != null)
                {
                    notes.Add(r.Bots);
                }

                if (r.Waits != null)
                {
                    notes.Add(r.Waits);
                }

                notes.AddRange(r.Suspects);
                if (r.Kept != null)
                {
                    notes.Add(Loc.L("레이드 후 남은 메모리: ", "memory kept after the raid: ") + r.Kept);
                }

                if (r.Restart != null)
                {
                    notes.Add(Loc.L("재시작 판단: ", "restart advice: ") + r.Restart);
                }

                if (notes.Count > 0)
                {
                    sb.Append("<h3>").Append(Loc.L("메모", "Notes")).Append("</h3><ul>");
                    foreach (string note in notes)
                    {
                        sb.Append("<li>").Append(E(note)).Append("</li>");
                    }

                    sb.Append("</ul>");
                }

                sb.Append("</section>");
            }

            // --- experimental heavy items
            if (heavyMods != null && heavyMods.Count > 0)
            {
                double most = Math.Max(1, heavyMods.Max(m => m.Mb));
                sb.Append("<section class=\"card\"><h2>").Append(Loc.L("[실험] 처음 로드 때 메모리를 많이 쓴 모드", "[Experimental] Mods whose items cost the most memory on first load")).Append("</h2>")
                  .Append("<p class=\"sub\">").Append(Loc.L(
                      "봇 장비 번들을 처음 불러올 때 늘어난 메모리를 나눠 붙인 값입니다. 동시에 다른 일이 일어나면 섞이므로, 여러 판에서 반복해서 큰 것만 믿으세요.",
                      "Memory added while bot gear bundles were loaded for the first time, split over those bundles. Other work at the same moment gets mixed in, so trust only what stays large across raids."))
                  .Append("</p><div class=\"bars\">");
                foreach (HeavyItemTracker.Stat m in heavyMods)
                {
                    sb.Append(Bar(m.Mod, m.Mb / most, Loc.L($"{F(m.Mb, "0")}MB · 번들 {m.Count}개", $"{F(m.Mb, "0")} MB · {m.Count} bundles") +
                        (m.Overlapped > 0 ? Loc.L($" (겹침 {m.Overlapped})", $" ({m.Overlapped} overlapped)") : string.Empty)));
                }

                sb.Append("</div>");
                if (heavyBundles != null && heavyBundles.Count > 0)
                {
                    sb.Append(Loc.L("<h3>번들별 (1회 로드당)</h3><div class=\"scroll\"><table><thead><tr><th>번들</th><th>모드</th><th class=n>MB/회</th><th class=n>로드</th></tr></thead><tbody>",
                                    "<h3>Per bundle (per load)</h3><div class=\"scroll\"><table><thead><tr><th>bundle</th><th>mod</th><th class=n>MB/load</th><th class=n>loads</th></tr></thead><tbody>"));
                    foreach (HeavyItemTracker.Stat b in heavyBundles)
                    {
                        sb.Append($"<tr><td>{E(b.Name)}</td><td>{E(b.Mod)}</td><td class=n>{F(b.Mb / Math.Max(1, b.Count), "0")}</td><td class=n>{b.Count}</td></tr>");
                    }

                    sb.Append("</tbody></table></div>");
                }

                sb.Append("</section>");
            }

            sb.Append("<p class=\"sub\">").Append(Loc.L("자세한 기록: BepInEx\\RamCleaner\\ 의 전용 로그, BepInEx\\LogOutput.log 의 RAM 클리너 줄",
                "Details: the dedicated log in BepInEx\\RamCleaner\\ and the RAM cleaner lines in BepInEx\\LogOutput.log")).Append("</p>");
            sb.Append("<div id=\"tip\" role=\"tooltip\"></div></main><script>").Append(Js).Append("</script></body></html>");
            return sb.ToString();
        }

        private static string Series(string name, int slot, IEnumerable<double> values)
        {
            return "{\"name\":\"" + JsonEscape(name) + "\",\"slot\":" + slot + ",\"v\":[" +
                   string.Join(",", values.Select(v => double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("0.###", Inv))) + "]}";
        }

        /// <param name="perRaid">x is a raid number (ticks "3판" / "raid 3") rather than minutes into the raid.</param>
        private static void AppendChart(StringBuilder sb, string id, string title, string unit, List<double> x, bool perRaid, string[] series)
        {
            string pre = perRaid ? Loc.L("", "raid ") : string.Empty;
            string suf = perRaid ? Loc.L("판", "") : Loc.L("분", " min");
            sb.Append($"<figure class=\"chart\"><figcaption>{E(title)}</figcaption><div class=\"plot\" id=\"{id}\" data-chart='")
              .Append("{\"unit\":\"").Append(JsonEscape(unit)).Append("\",\"raid\":").Append(perRaid ? "true" : "false")
              .Append(",\"pre\":\"").Append(JsonEscape(pre)).Append("\",\"suf\":\"").Append(JsonEscape(suf)).Append("\",\"x\":[")
              .Append(string.Join(",", x.Select(v => v.ToString("0.##", Inv)))).Append("],\"series\":[")
              .Append(string.Join(",", series)).Append("]}'></div></figure>");
        }

        private static string Tile(string label, string value, string note)
        {
            return $"<div class=\"tile\"><div class=\"lbl\">{E(label)}</div><div class=\"val\">{E(value)}</div><div class=\"note\">{E(note)}</div></div>";
        }

        private static string Bar(string label, double share, string value)
        {
            double pct = Math.Max(1, Math.Min(100, share * 100));
            return $"<div class=\"bar\"><span class=\"blbl\">{E(label)}</span><span class=\"track\"><span class=\"fill\" style=\"width:{pct.ToString("0.#", Inv)}%\"></span></span><span class=\"bval\">{E(value)}</span></div>";
        }

        private static string F(double value, string format) => value.ToString(format, Inv);

        private static string E(string text) => WebUtility.HtmlEncode(text ?? string.Empty);

        private static string JsonEscape(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\u0027"); break; // the JSON sits in a single-quoted attribute
                    case '<': sb.Append("\\u003c"); break;
                    case '&': sb.Append("\\u0026"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.ToString();
        }

        // Palette: reference data-viz palette, categorical slots 1-3 (validated all-pairs in both modes).
        private const string Css = @"
.viz-root{color-scheme:light;--page:#f9f9f7;--surface-1:#fcfcfb;--text-primary:#0b0b0b;--text-secondary:#52514e;--muted:#898781;
--grid:#e1e0d9;--axis:#c3c2b7;--border:rgba(11,11,11,.10);--series-1:#2a78d6;--series-2:#eb6834;--series-3:#1baf7a}
@media (prefers-color-scheme:dark){:root:where(:not([data-theme=light])) .viz-root{color-scheme:dark;--page:#0d0d0d;--surface-1:#1a1a19;
--text-primary:#fff;--text-secondary:#c3c2b7;--grid:#2c2c2a;--axis:#383835;--border:rgba(255,255,255,.10);--series-1:#3987e5;--series-2:#d95926;--series-3:#199e70}}
:root[data-theme=dark] .viz-root{color-scheme:dark;--page:#0d0d0d;--surface-1:#1a1a19;--text-primary:#fff;--text-secondary:#c3c2b7;--grid:#2c2c2a;
--axis:#383835;--border:rgba(255,255,255,.10);--series-1:#3987e5;--series-2:#d95926;--series-3:#199e70}
html,body{margin:0}body{background:#f9f9f7}@media (prefers-color-scheme:dark){body{background:#0d0d0d}}
.viz-root{background:var(--page);color:var(--text-primary);font:14px/1.5 system-ui,-apple-system,'Segoe UI','Malgun Gothic',sans-serif;
max-width:1180px;margin:0 auto;padding:24px 16px 48px;min-height:100vh;box-sizing:border-box}
h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:0 0 12px}h3{font-size:14px;margin:16px 0 8px;color:var(--text-secondary)}
.sub{color:var(--text-secondary);margin:0 0 20px}section{margin:0 0 28px}
.card{background:var(--surface-1);border:1px solid var(--border);border-radius:12px;padding:16px}
.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;background:var(--surface-1)}
th,td{padding:6px 10px;border-bottom:1px solid var(--grid);text-align:left;white-space:nowrap}th{color:var(--text-secondary);font-weight:600}
td.n,th.n{text-align:right}a{color:var(--series-1)}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin:0 0 16px}
.tile{border:1px solid var(--border);border-radius:10px;padding:10px 12px}.lbl{color:var(--text-secondary);font-size:12px}
.val{font-size:22px;font-weight:600}.note{color:var(--muted);font-size:12px}
.grid2{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,460px),1fr));gap:16px}
.chart{margin:0}figcaption{color:var(--text-secondary);font-size:13px;margin-bottom:4px}
.plot{position:relative}.plot svg{display:block;width:100%;height:auto;overflow:visible}
.legend{display:flex;flex-wrap:wrap;gap:12px;font-size:12px;color:var(--text-secondary);margin:0 0 4px}
.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px;vertical-align:-1px}
.bars{display:grid;gap:6px}.bar{display:grid;grid-template-columns:minmax(120px,260px) 1fr auto;gap:10px;align-items:center}
.blbl{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.track{height:10px;background:var(--grid);border-radius:4px;overflow:hidden}
.fill{display:block;height:100%;background:var(--series-1);border-radius:0 4px 4px 0}.bval{color:var(--text-secondary);font-variant-numeric:tabular-nums;white-space:nowrap}
#tip{position:fixed;pointer-events:none;display:none;background:var(--surface-1);color:var(--text-primary);border:1px solid var(--border);
border-radius:8px;padding:8px 10px;font-size:12px;box-shadow:0 4px 16px rgba(0,0,0,.18);z-index:10;font-variant-numeric:tabular-nums}
#tip i{display:inline-block;width:8px;height:8px;border-radius:2px;margin-right:6px}
@media (max-width:560px){.bar{grid-template-columns:1fr auto}.track{grid-column:1/-1;order:3}}";

        private const string Js = @"
(function(){var tip=document.getElementById('tip');var NS='http://www.w3.org/2000/svg';
function el(n,a){var e=document.createElementNS(NS,n);for(var k in a)e.setAttribute(k,a[k]);return e}
function nice(m){if(!(m>0))return 1;var p=Math.pow(10,Math.floor(Math.log10(m)));var f=m/p;return (f<=1?1:f<=2?2:f<=5?5:10)*p}
function fmt(v){return v==null?'-':(Math.abs(v)>=100?v.toFixed(0):v.toFixed(1))}
document.querySelectorAll('.plot[data-chart]').forEach(function(box){var d=JSON.parse(box.getAttribute('data-chart'));
var W=640,H=230,L=44,R=12,T=10,B=26,x=d.x,n=x.length;var max=0;d.series.forEach(function(s){s.v.forEach(function(v){if(v!=null&&v>max)max=v})});
var top=nice(max*1.08);var x0=x[0],x1=x[n-1];if(x1==x0)x1=x0+1;
function sx(v){return L+(v-x0)/(x1-x0)*(W-L-R)}function sy(v){return T+(1-v/top)*(H-T-B)}
if(d.series.length>1){var lg=document.createElement('div');lg.className='legend';d.series.forEach(function(s){var sp=document.createElement('span');
sp.innerHTML='<i style=""background:var(--series-'+s.slot+')""></i>';sp.appendChild(document.createTextNode(s.name));lg.appendChild(sp)});box.appendChild(lg)}
var svg=el('svg',{viewBox:'0 0 '+W+' '+H,role:'img','aria-label':box.parentNode.querySelector('figcaption').textContent});
for(var i=0;i<=4;i++){var v=top*i/4,y=sy(v);svg.appendChild(el('line',{x1:L,x2:W-R,y1:y,y2:y,stroke:i?'var(--grid)':'var(--axis)','stroke-width':1}));
var t=el('text',{x:L-6,y:y+4,'text-anchor':'end','font-size':11,fill:'var(--muted)'});t.textContent=fmt(v);svg.appendChild(t)}
var ticks=Math.min(6,n);for(var j=0;j<ticks;j++){var xv=x0+(x1-x0)*j/Math.max(1,ticks-1);var tx=el('text',{x:sx(xv),y:H-6,'text-anchor':'middle','font-size':11,fill:'var(--muted)'});
tx.textContent=d.pre+(d.raid?Math.round(xv):xv.toFixed(0))+d.suf;svg.appendChild(tx)}
d.series.forEach(function(s){var p='',pen=false;for(var i=0;i<n;i++){var v=s.v[i];if(v==null){pen=false;continue}
p+=(pen?'L':'M')+sx(x[i]).toFixed(1)+' '+sy(v).toFixed(1);pen=true}
svg.appendChild(el('path',{d:p,fill:'none',stroke:'var(--series-'+s.slot+')','stroke-width':2,'stroke-linejoin':'round','stroke-linecap':'round'}));
if(n<=30)for(var i=0;i<n;i++){if(s.v[i]!=null)svg.appendChild(el('circle',{cx:sx(x[i]),cy:sy(s.v[i]),r:4,fill:'var(--series-'+s.slot+')',stroke:'var(--surface-1)','stroke-width':2}))}});
var cross=el('line',{y1:T,y2:H-B,stroke:'var(--axis)','stroke-width':1,visibility:'hidden'});svg.appendChild(cross);
var hit=el('rect',{x:L,y:0,width:W-L-R,height:H,fill:'transparent'});svg.appendChild(hit);box.appendChild(svg);
function show(ev){var r=svg.getBoundingClientRect();var px=(ev.clientX-r.left)/r.width*W;var best=0,bd=1e9;
for(var i=0;i<n;i++){var dd=Math.abs(sx(x[i])-px);if(dd<bd){bd=dd;best=i}}cross.setAttribute('x1',sx(x[best]));cross.setAttribute('x2',sx(x[best]));
cross.setAttribute('visibility','visible');var h='<b>'+d.pre+(d.raid?Math.round(x[best]):x[best].toFixed(1))+d.suf+'</b>';
d.series.forEach(function(s){h+='<div><i style=""background:var(--series-'+s.slot+')""></i>'+s.name.replace(/</g,'&lt;')+' '+fmt(s.v[best])+' '+d.unit+'</div>'});
tip.innerHTML=h;tip.style.display='block';var tx=ev.clientX+14,ty=ev.clientY+14;if(tx+tip.offsetWidth>innerWidth-8)tx=ev.clientX-tip.offsetWidth-14;
if(ty+tip.offsetHeight>innerHeight-8)ty=ev.clientY-tip.offsetHeight-14;tip.style.left=tx+'px';tip.style.top=ty+'px'}
hit.addEventListener('mousemove',show);hit.addEventListener('mouseleave',function(){tip.style.display='none';cross.setAttribute('visibility','hidden')})})})();";
    }
}
