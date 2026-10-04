using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// The browser side (18. Web page): http://127.0.0.1:6977/ shows a live view, the session report with the raid in
    /// progress, and every F12 setting (changes apply at once). <see cref="WebServer"/> answers on its own thread;
    /// everything here that reads or changes game state runs on the main thread through <see cref="OnMain{T}"/>.
    /// </summary>
    public partial class CustomRamCleanerIntervalPlugin
    {
        private const int WebHistoryPoints = 300; // 10 minutes at one point per 2 s
        private const int MainThreadTimeoutMs = 5000;

        private WebServer _web;
        private readonly List<float[]> _webHistory = new List<float[]>(); // time, game commit GB, system free GB, server GB (-1), fps
        private float _webNextSample;
        private float _webRestartAt = -1f;
        private int _webLivePending; // 1 while a live update waits for the main thread (a long load screen must not pile them up)
        private static Dictionary<string, string> s_webAssets;

        private void StartWebServer()
        {
            _web = new WebServer(Logger, HandleWeb);
            EventHandler restart = (_, __) => _webRestartAt = Time.realtimeSinceStartup + 0.5f; // after the reply has gone out
            _webEnabled.SettingChanged += restart;
            _webPort.SettingChanged += restart;
            _webLan.SettingChanged += restart;
            if (_webEnabled.Value)
            {
                _web.Start(_webPort.Value, _webLan.Value);
            }
        }

        /// <summary>Once a second (main thread): pending restarts and the 10-minute history behind the live charts.</summary>
        private void TickWeb(float now)
        {
            SyncCompoundingPerfLanguage(now);
            if (_webRestartAt >= 0f && now >= _webRestartAt)
            {
                _webRestartAt = -1f;
                _web.Stop();
                if (_webEnabled.Value)
                {
                    _web.Start(_webPort.Value, _webLan.Value);
                }
            }

            if (!_web.Running || now < _webNextSample)
            {
                return;
            }

            _webNextSample = now + 2f;
            _webHistory.Add(new[]
            {
                now,
                (float)(_snapshot.PrivateBytes / MemoryStats.BytesPerGb),
                (float)(_snapshot.SystemAvailable / MemoryStats.BytesPerGb),
                _serverEnabled.Value && _server.PrivateBytes > 0 ? (float)(_server.PrivateBytes / MemoryStats.BytesPerGb) : -1f,
                _frames.CurrentFps,
            });
            if (_webHistory.Count > WebHistoryPoints)
            {
                _webHistory.RemoveAt(0);
            }
        }

        private void OpenWebPage()
        {
            if (!_web.Running)
            {
                Notify(Loc.L("웹 페이지가 꺼져 있습니다 — '18. 웹 페이지'에서 켜세요. ", "The web page is off — turn it on in '18. Web page'. ") + _web.Status, false);
                return;
            }

            Application.OpenURL(_web.Url);
        }

        /// <summary>Runs <paramref name="work"/> in Update and waits for it (web thread). Throws on timeout.</summary>
        private T OnMain<T>(Func<T> work)
        {
            T result = default(T);
            Exception error = null;
            var done = new ManualResetEvent(false); // not disposed: a late Set after a timeout must not throw in Update
            _mainThread.Enqueue(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.WaitOne(MainThreadTimeoutMs))
            {
                throw new TimeoutException(Loc.L("게임이 응답하지 않습니다 (로딩 중이면 잠시 뒤 다시 시도)", "the game did not answer (if it is loading, try again in a moment)"));
            }

            if (error != null)
            {
                throw new InvalidOperationException(error.Message, error);
            }

            return result;
        }

        // ---------------------------------------------------------------- routing (web thread)

        private WebServer.Response HandleWeb(string method, string path, string body)
        {
            bool post = method == "POST";
            try
            {
                switch (path)
                {
                    case "/":
                    case "/index.html":
                        return new WebServer.Response { Body = DashboardPage() };
                    case "/report":
                        return new WebServer.Response { Body = OnMain(ReportPage) };
                    case "/settings":
                        return new WebServer.Response { Body = SettingsPage() };
                    case "/api/live":
                        if (Interlocked.Exchange(ref _webLivePending, 1) == 1)
                        {
                            return WebServer.Response.Json("{\"busy\":true}", 503);
                        }

                        return WebServer.Response.Json(OnMain(() =>
                        {
                            try
                            {
                                return BuildLiveJson();
                            }
                            finally
                            {
                                Interlocked.Exchange(ref _webLivePending, 0);
                            }
                        }));
                    case "/api/settings":
                        string key = WebServer.Form(body, "key");
                        string value = WebServer.Form(body, "value");
                        return WebServer.Response.Json(post ? OnMain(() => ApplyWebSetting(key, value)) : OnMain(BuildSettingsJson));
                    case "/server":
                        return new WebServer.Response { Body = ServerPage() };
                    case "/api/cp/settings":
                        string cpKey = WebServer.Form(body, "key");
                        string cpValue = WebServer.Form(body, "value");
                        return WebServer.Response.Json(post ? OnMain(() => ApplyCompoundingPerfSetting(cpKey, cpValue)) : OnMain(BuildCompoundingPerfSettingsJson));
                    case "/api/cp/status":
                        return WebServer.Response.Json(OnMain(() => BuildCompoundingPerfStatusJson(post)));
                    case "/api/action":
                        string name = WebServer.Form(body, "name");
                        return post ? WebServer.Response.Json(OnMain(() => RunWebAction(name))) : null;
                    case "/favicon.ico":
                        return new WebServer.Response { Status = 204, ContentType = "image/x-icon" };
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                string message = ex is InvalidOperationException && ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return path.StartsWith("/api/", StringComparison.Ordinal)
                    ? WebServer.Response.Json("{\"error\":" + WebServer.Str(message) + "}", 503)
                    : new WebServer.Response { Status = 503, Body = Shell(Loc.L("RAM 클리너", "RAM Cleaner"), null, "<p class=\"sub\">" + WebUtility.HtmlEncode(message) + "</p>", null, null) };
            }
        }

        // ---------------------------------------------------------------- pages

        private static string Asset(string name)
        {
            if (s_webAssets == null)
            {
                var assets = new Dictionary<string, string>();
                var assembly = typeof(CustomRamCleanerIntervalPlugin).Assembly;
                foreach (string file in new[] { "web.css", "common.js", "dashboard.js", "settings.js" })
                {
                    using (Stream stream = assembly.GetManifestResourceStream("web." + file))
                    {
                        assets[file] = stream == null ? string.Empty : new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                    }
                }

                s_webAssets = assets;
            }

            return s_webAssets[name];
        }

        private static string Nav(string active)
        {
            string Link(string href, string id, string text) =>
                $"<a href=\"{href}\"" + (id == active ? " class=\"on\" aria-current=\"page\"" : string.Empty) + $">{text}</a>";
            return "<nav class=\"nav\"><span class=\"brand\">" + Loc.L("RAM 클리너", "RAM Cleaner") + "</span>" +
                   Link("/", "live", Loc.L("실시간", "Live")) +
                   Link("/report", "report", Loc.L("세션 보고서", "Session report")) +
                   Link("/settings", "settings", Loc.L("설정", "Settings")) +
                   (!(CompoundingPerfPlugin is null) ? Link("/server", "server", Loc.L("서버 최적화", "Server optimisation")) : string.Empty) +
                   "<button type=\"button\" id=\"lang\" class=\"lang\" data-to=\"" + (Loc.En ? LanguageKorean : LanguageEnglish) + "\">" +
                   (Loc.En ? "한국어" : "English") + "</button></nav>";
        }

        /// <summary>A full page: the report's styles + the web styles, nav, content, then scripts.</summary>
        private static string Shell(string title, string active, string content, string strings, string script)
        {
            var sb = new StringBuilder(32 * 1024);
            sb.Append("<!doctype html><html lang=\"").Append(Loc.L("ko", "en")).Append("\"><head><meta charset=\"utf-8\">")
              .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title>")
              .Append("<style>").Append(SessionReport.Css).Append('\n').Append(Asset("web.css")).Append("</style></head><body><main class=\"viz-root\">")
              .Append(Nav(active)).Append(content).Append("<div id=\"tip\" role=\"tooltip\"></div></main>")
              .Append("<script>").Append(SessionReport.Js).Append("</script>");
            if (strings != null)
            {
                sb.Append("<script>window.RC=").Append(strings).Append(";</script>");
            }

            sb.Append("<script>").Append(Asset("common.js")).Append("</script>");
            if (script != null)
            {
                sb.Append("<script>").Append(Asset(script)).Append("</script>");
            }

            return sb.Append("</body></html>").ToString();
        }

        private static string Strings(params string[] pairs)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                sb.Append(i > 0 ? "," : string.Empty).Append(WebServer.Str(pairs[i])).Append(':').Append(WebServer.Str(pairs[i + 1]));
            }

            return sb.Append('}').ToString();
        }

        private static string Offline => Loc.L("게임과 연결이 끊겼습니다 — 게임이 꺼졌거나 웹 페이지 설정이 꺼졌을 수 있습니다. 다시 연결을 시도하는 중...",
                                               "Lost the connection to the game — it was closed or the web page was turned off. Retrying...");

        private static string DashboardPage()
        {
            string H(string text) => WebUtility.HtmlEncode(text);
            string BarCard(string id, string title, string note) =>
                $"<section class=\"card hidden\" id=\"{id}Card\"><h2>{H(title)}</h2>" + (note != null ? $"<p class=\"sub\">{H(note)}</p>" : string.Empty) +
                $"<div class=\"bars\" id=\"{id}\"></div></section>";

            string content =
                "<h1>" + H(Loc.L("실시간 현황", "Live view")) + "</h1><p class=\"sub\" id=\"sub\">" + H(Loc.L("연결 중...", "Connecting...")) + "</p>" +
                "<div id=\"alerts\" role=\"status\"></div><div class=\"tiles\" id=\"tiles\"></div>" +
                "<section><div class=\"grid2\">" +
                "<figure class=\"chart\"><figcaption>" + H(Loc.L("메모리 (GB, 최근 10분)", "Memory (GB, last 10 min)")) + "</figcaption><div class=\"plot\" id=\"memChart\"></div></figure>" +
                "<figure class=\"chart\"><figcaption>" + H(Loc.L("FPS (최근 10분)", "FPS (last 10 min)")) + "</figcaption><div class=\"plot\" id=\"fpsChart\"></div></figure>" +
                "</div></section>" +
                "<section class=\"card\"><h2>" + H(Loc.L("수동 실행", "Manual actions")) + "</h2><div class=\"actions\">" +
                "<button type=\"button\" class=\"primary\" data-act=\"gc\">" + H(Loc.L("GC 정리", "Run GC")) + "</button>" +
                "<button type=\"button\" data-act=\"trim\">" + H(Loc.L("워킹셋 정리", "Trim working set")) + "</button>" +
                "<button type=\"button\" data-act=\"assets\">" + H(Loc.L("에셋 정리 (1~3초 멈춤)", "Unload assets (1-3 s pause)")) + "</button>" +
                "<button type=\"button\" data-act=\"leak\">" + H(Loc.L("누수 추적 기록", "Take a leak snapshot")) + "</button>" +
                "<button type=\"button\" data-act=\"diag\" id=\"diagBtn\">" + H(Loc.L("원인 추적 켜기", "Turn diagnostic mode on")) + "</button>" +
                "</div><p class=\"msg\" id=\"actmsg\" role=\"status\"></p></section>" +
                "<div class=\"grid2\">" +
                BarCard("hitch", Loc.L("끊김 원인 (이번 레이드)", "Stutter causes (this raid)"), null) +
                BarCard("mods", Loc.L("모드별 프레임 부하 (ms/프레임)", "Per-mod frame cost (ms/frame)"), null) +
                BarCard("alloc", Loc.L("모드별 메모리 생성 (MB/분)", "Per-mod memory creation (MB/min)"), null) +
                BarCard("heavy", Loc.L("[실험] 처음 로드 때 메모리를 많이 쓴 모드", "[Experimental] Mods costing the most memory on first load"),
                    Loc.L("여러 판에서 반복해서 큰 것만 믿으세요.", "Trust only what stays large across raids.")) +
                "</div>" +
                "<section class=\"card\"><details><summary>" + H(Loc.L("자세한 상태 (F12 '현재 상태'와 같은 내용)", "Full status (same as F12 'Status')")) +
                "</summary><pre id=\"status\"></pre></details></section>" +
                "<p class=\"sub\">" + H(Loc.L("이 페이지는 1초마다 게임에서 숫자를 받아 옵니다. 탭을 숨기면 5초마다로 줄어듭니다.",
                    "This page fetches numbers from the game once a second (every 5 s while the tab is hidden).")) + "</p>";

            string strings = Strings(
                "offline", Offline,
                "collecting", Loc.L("기록 모으는 중 (2초마다 한 점)", "collecting (one point every 2 s)"),
                "diagOn", Loc.L("원인 추적 켜기", "Turn diagnostic mode on"),
                "diagOff", Loc.L("원인 추적 끄기", "Turn diagnostic mode off"));
            return Shell(Loc.L("RAM 클리너 — 실시간", "RAM Cleaner — live"), "live", content, strings, "dashboard.js");
        }

        private static string SettingsPage()
        {
            string H(string text) => WebUtility.HtmlEncode(text);
            string content =
                "<h1>" + H(Loc.L("설정", "Settings")) + "</h1><p class=\"sub\">" +
                H(Loc.L("F12와 같은 설정입니다. 바꾸면 게임에 바로 적용되고 저장됩니다(F12 창은 닫았다 열면 바뀐 값이 보임). '모드'를 고르면 아래 설정들이 한 번에 바뀝니다.",
                        "The same settings as F12. Changes apply in the game at once and are saved (reopen the F12 window to see them there). Picking a 'mode' changes many settings at once.")) +
                "</p><div class=\"toolbar\"><input type=\"search\" id=\"q\" placeholder=\"" + H(Loc.L("설정 검색 (예: 서버, 끊김, GC)", "Search settings (e.g. server, stutter, GC)")) +
                "\" aria-label=\"" + H(Loc.L("설정 검색", "Search settings")) + "\"></div><p class=\"msg\" id=\"msg\" role=\"status\"></p><div id=\"list\"></div>";
            return Shell(Loc.L("RAM 클리너 — 설정", "RAM Cleaner — settings"), "settings", content, SettingsStrings("/api/settings", null), "settings.js");
        }

        /// <summary>Texts and endpoints for settings.js (this plugin's settings, or CompoundingPerf's with a status endpoint).</summary>
        private static string SettingsStrings(string api, string statusApi)
        {
            return Strings(
                "api", api,
                "statusApi", statusApi ?? string.Empty,
                "offline", Offline,
                "saving", Loc.L("저장 중", "saving"),
                "saved", Loc.L("✓ 적용됨", "✓ applied"),
                "failed", Loc.L("실패", "failed"),
                "reset", Loc.L("기본값으로", "Reset"),
                "defaultIs", Loc.L("기본값:", "Default:"),
                "on", Loc.L("켜짐", "on"),
                "off", Loc.L("꺼짐", "off"),
                "presetApplied", Loc.L("모드를 적용했습니다 — 아래 값들이 바뀌었습니다.", "Mode applied — the values below changed."),
                "moving", Loc.L("포트를 {0}(으)로 바꿨습니다. 새 주소로 이동합니다...", "Port changed to {0}. Moving to the new address..."),
                "webOff", Loc.L("웹 페이지를 껐습니다. 다시 켜려면 F12 '18. 웹 페이지'에서 켜세요.", "The web page is off now. Turn it back on in F12 '18. Web page'."),
                "cpSent", Loc.L("게임에 적용됨 — 0.8초 뒤 서버로 보냅니다. 결과는 위 '서버 상태'에 나옵니다.", "Applied in the game — sent to the server 0.8 s later. The result shows under 'Server status' above."));
        }

        /// <summary>Main thread: the session report including the raid in progress.</summary>
        private string ReportPage()
        {
            if (_heavyEnabled.Value)
            {
                _sessionReport.SetHeavy(_heavy.TopMods(10), _heavy.TopBundles(15));
            }

            float now = Time.realtimeSinceStartup;
            string html = _sessionReport.BuildLive(
                r => FillSessionRaid(r, CurrentSuspects(), _raidStartedAt >= 0f ? (now - _raidStartedAt) / 60.0 : 0.0),
                "<style>" + Asset("web.css") + "</style>" + Nav("report") + "<script>" + Asset("common.js") + "</script>");
            return _sessionReportEnabled.Value
                ? html
                : html.Replace("<h1>", "<p class=\"alert\">" + WebUtility.HtmlEncode(Loc.L("'16. 세션 보고서'가 꺼져 있어서 레이드 그래프가 기록되지 않습니다.",
                    "'16. Session report' is off, so raid charts are not recorded.")) + "</p><h1>");
        }

        // ---------------------------------------------------------------- live data (main thread)

        private string BuildLiveJson()
        {
            float now = Time.realtimeSinceStartup;
            MemorySnapshot s = _snapshot;
            bool inRaid = _inGame && _raidStartedAt >= 0f;
            var tiles = new List<string>();

            void Tile(string label, string value, string note, int level) =>
                tiles.Add("{\"k\":" + WebServer.Str(label) + ",\"v\":" + WebServer.Str(value) + ",\"n\":" + WebServer.Str(note ?? string.Empty) + ",\"lv\":" + level + "}");

            Tile(Loc.L("게임 메모리 (커밋)", "Game memory (commit)"), MemoryStats.Gb(s.PrivateBytes) + " GB",
                Loc.L($"실제 RAM {MemoryStats.Gb(s.WorkingSet)} GB · 관리 {MemoryStats.Gb(s.MonoUsed)} GB", $"RAM in use {MemoryStats.Gb(s.WorkingSet)} GB · managed {MemoryStats.Gb(s.MonoUsed)} GB"), 0);
            if (s.SystemTotal > 0)
            {
                float pct = s.SystemAvailablePercent;
                Tile(Loc.L("시스템 여유 RAM", "System free RAM"), MemoryStats.Gb(s.SystemAvailable) + " GB",
                    Loc.L($"전체 {MemoryStats.Gb(s.SystemTotal)} GB 중 {pct:0}%", $"{pct:0}% of {MemoryStats.Gb(s.SystemTotal)} GB"), pct < 7 ? 2 : pct < 15 ? 1 : 0);
            }

            if (s.CommitLimit > 0)
            {
                long floor = CommitFloor(s);
                Tile(Loc.L("커밋 여유 (튕김 한도까지)", "Commit headroom (to the crash limit)"), MemoryStats.Gb(s.CommitAvailable) + " GB",
                    Loc.L($"한도 {MemoryStats.Gb(s.CommitLimit)} GB · 경고선 {MemoryStats.Gb(floor)} GB", $"limit {MemoryStats.Gb(s.CommitLimit)} GB · warning line {MemoryStats.Gb(floor)} GB"),
                    s.CommitAvailable < floor ? 2 : s.CommitAvailable < floor * 2 ? 1 : 0);
            }

            Tile("FPS", _frames.CurrentFps.ToString("0"),
                _frames.Frames > 0
                    ? Loc.L($"레이드 평균 {_frames.AverageFps:0} · 1% 저점 {_frames.OnePercentLowFps():0}", $"raid average {_frames.AverageFps:0} · 1% low {_frames.OnePercentLowFps():0}")
                    : Loc.L("평균은 레이드 중에 계산", "averages are worked out in raid"), 0);

            if (_hitchEnabled.Value)
            {
                Tile(Loc.L("끊김 (이번 레이드)", "Stutters (this raid)"), _hitch.Count.ToString(),
                    _hitch.Count > 0 ? Loc.L($"최대 {_hitch.MaxMs:0} ms · 그중 이 모드 {_hitch.Ours}회", $"worst {_hitch.MaxMs:0} ms · {_hitch.Ours} by this mod") : Loc.L("없음", "none"),
                    _hitch.Suspect != null ? 1 : 0);
            }

            if (_vram.Dedicated >= 0)
            {
                Tile("VRAM", MemoryStats.Gb(_vram.Dedicated) + " GB",
                    Loc.L($"그래픽카드 {SystemInfo.graphicsMemorySize / 1024f:0.0} GB · 공유 {MemoryStats.Gb(_vram.Shared)} GB", $"graphics card {SystemInfo.graphicsMemorySize / 1024f:0.0} GB · shared {MemoryStats.Gb(_vram.Shared)} GB"), 0);
            }

            if (_serverEnabled.Value)
            {
                Tile(Loc.L("SPT 서버 메모리", "SPT server memory"), _server.Found ? MemoryStats.Gb(_server.PrivateBytes) + " GB" : "-",
                    _server.Found ? Loc.L($"실제 RAM {MemoryStats.Gb(_server.WorkingSet)} GB · {_server.ProcessName}", $"RAM in use {MemoryStats.Gb(_server.WorkingSet)} GB · {_server.ProcessName}")
                                  : Loc.L("프로세스를 찾는 중 (30초마다)", "looking for the process (every 30 s)"), 0);
            }

            if (_forecastEnabled.Value)
            {
                if (inRaid)
                {
                    double m = _forecast.MinutesLeft;
                    string value = double.IsNaN(m) ? "-" : m >= 600 ? Loc.L("10시간+", "10 h+") : m >= 120 ? Loc.L($"{m / 60:0.0}시간", $"{m / 60:0.0} h") : Loc.L($"{m:0}분", $"{m:0} min");
                    string note = double.IsNaN(m)
                        ? _forecast.DescribeRunway(RecentPerDeathMb(), s.CommitAvailable, CommitFloor(s)) ?? Loc.L("레이드 3분 뒤부터 계산", "worked out from 3 min into the raid")
                        : (_forecast.Limit == "RAM" ? Loc.L("RAM 부족(끊김 시작)까지", "until low RAM (stutter starts)") : Loc.L("커밋 한도(튕김)까지", "until the commit limit (crash)")) +
                          Loc.L($" · 분당 -{_forecast.DropMbPerMin:0}MB", $" · -{_forecast.DropMbPerMin:0} MB/min");
                    Tile(Loc.L("여유 예상", "Time left"), value, note, !double.IsNaN(m) && m < _forecastWarnMinutes.Value ? 2 : !double.IsNaN(m) && m < _forecastWarnMinutes.Value * 2 ? 1 : 0);
                }

                int left = _forecast.RaidsLeft;
                Tile(Loc.L("재시작 판단", "Restart advice"),
                    left == 0 ? Loc.L("지금 재시작", "restart now") : left == 1 ? Loc.L("다음 판 뒤", "after next raid") : left > 1 ? Loc.L($"약 {left}판 더", $"~{left} more raids") : "-",
                    _forecast.RestartText, left == 0 ? 2 : left == 1 ? 1 : 0);
            }

            var alerts = new List<string>(CurrentSuspects());
            if (inRaid && _forecastEnabled.Value && !double.IsNaN(_forecast.MinutesLeft) && _forecast.MinutesLeft < _forecastWarnMinutes.Value)
            {
                alerts.Insert(0, Loc.L("메모리 여유 부족 예상 — 이번 레이드를 마무리하는 걸 권장합니다.", "Memory is running low — consider wrapping up this raid."));
            }

            var json = new StringBuilder(16 * 1024);
            string where = inRaid
                ? Loc.L($"레이드 중 — {_map ?? "?"} {(now - _raidStartedAt) / 60f:0}분째", $"in raid — {_map ?? "?"}, {(now - _raidStartedAt) / 60f:0} min")
                : _inGame ? Loc.L("레이드 로딩·시작 대기", "raid loading / waiting to start") : Loc.L("메뉴·은신처", "menu / hideout");
            json.Append("{\"sub\":").Append(WebServer.Str(Loc.L($"{DateTime.Now:HH:mm:ss} 갱신 · {where} · 모드: {PresetShortName()}",
                $"updated {DateTime.Now:HH:mm:ss} · {where} · mode: {PresetShortName()}")));
            json.Append(",\"diagOn\":").Append(WebServer.Bool(_diagMode.Value));
            json.Append(",\"tiles\":[").Append(string.Join(",", tiles)).Append(']');
            json.Append(",\"alerts\":[").Append(string.Join(",", alerts.Select(WebServer.Str))).Append(']');
            AppendHistory(json, now);

            if (_hitchEnabled.Value)
            {
                List<KeyValuePair<string, KeyValuePair<int, float>>> causes = _hitch.Causes();
                int most = causes.Count > 0 ? Math.Max(1, causes.Max(c => c.Value.Key)) : 1;
                AppendBars(json, "hitch", causes.Take(8).Select(c => Bar(c.Key, c.Value.Key / (double)most,
                    Loc.L($"{c.Value.Key}회 · 최대 {c.Value.Value:0}ms", $"{c.Value.Key}x · worst {c.Value.Value:0} ms"),
                    c.Key == HitchMonitor.CauseGame ? "gray" : null)));
            }

            if (_profilerEnabled.Value && _profiler.LastResult.Count > 0)
            {
                double most = Math.Max(0.01, _profiler.LastResult.Max(x => x.Value));
                AppendBars(json, "mods", _profiler.LastResult.Take(8).Select(x => Bar(x.Key, x.Value / most, x.Value.ToString("0.00") + " ms",
                    _profiler.Suspect != null && _profiler.Suspect.Contains(x.Key) ? "bad" : null)));
            }

            if (_profilerEnabled.Value && _profiler.LastAlloc.Count > 0)
            {
                double most = Math.Max(0.01, _profiler.LastAlloc.Max(x => x.Value));
                AppendBars(json, "alloc", _profiler.LastAlloc.Take(8).Select(x => Bar(x.Key, x.Value / most, x.Value.ToString("0") + Loc.L(" MB/분", " MB/min"),
                    _profiler.AllocSuspect != null && _profiler.AllocSuspect.Contains(x.Key) ? "bad" : null)));
            }

            if (_heavyEnabled.Value)
            {
                List<HeavyItemTracker.Stat> heavy = _heavy.TopMods(8);
                double most = heavy.Count > 0 ? Math.Max(1, heavy.Max(x => x.Mb)) : 1;
                AppendBars(json, "heavy", heavy.Select(x => Bar(x.Mod, x.Mb / most,
                    Loc.L($"{x.Mb:0}MB · 번들 {x.Count}개", $"{x.Mb:0} MB · {x.Count} bundles") + (x.Overlapped > 0 ? Loc.L($" (겹침 {x.Overlapped})", $" ({x.Overlapped} overlapped)") : string.Empty), null)));
            }

            json.Append(",\"status\":").Append(WebServer.Str(_statusText));
            return json.Append('}').ToString();
        }

        private static string Bar(string label, double share, string value, string cls) =>
            "{\"l\":" + WebServer.Str(label) + ",\"s\":" + WebServer.Num(share) + ",\"v\":" + WebServer.Str(value) + (cls != null ? ",\"c\":" + WebServer.Str(cls) : string.Empty) + "}";

        private static void AppendBars(StringBuilder json, string name, IEnumerable<string> bars)
        {
            json.Append(",\"").Append(name).Append("\":[").Append(string.Join(",", bars)).Append(']');
        }

        /// <summary>The two live charts in the same shape the session report's charts use (window.rcChart).</summary>
        private void AppendHistory(StringBuilder json, float now)
        {
            string x = string.Join(",", _webHistory.Select(p => WebServer.Num((p[0] - now) / 60.0, "0.##")));
            string Series(string name, int slot, Func<float[], double> value) =>
                "{\"name\":" + WebServer.Str(name) + ",\"slot\":" + slot + ",\"v\":[" + string.Join(",", _webHistory.Select(p => WebServer.Num(value(p), "0.###"))) + "]}";
            string Chart(string unit, params string[] series) =>
                "{\"unit\":" + WebServer.Str(unit) + ",\"raid\":false,\"pre\":\"\",\"suf\":" + WebServer.Str(Loc.L("분", " min")) +
                ",\"x\":[" + x + "],\"series\":[" + string.Join(",", series) + "]}";

            var memory = new List<string>
            {
                Series(Loc.L("게임 커밋", "game commit"), 1, p => p[1]),
                Series(Loc.L("시스템 여유 RAM", "system free RAM"), 2, p => p[2]),
            };
            if (_webHistory.Any(p => p[3] > 0))
            {
                memory.Add(Series(Loc.L("SPT 서버", "SPT server"), 3, p => p[3] > 0 ? p[3] : double.NaN));
            }

            json.Append(",\"mem\":").Append(Chart("GB", memory.ToArray()));
            json.Append(",\"fps\":").Append(Chart("fps", Series("FPS", 1, p => p[4])));
        }

        private string PresetShortName()
        {
            switch (_preset.Value)
            {
                case PresetAuto: return Loc.L("자동 정리", "Auto cleanup");
                case PresetQuick: return Loc.L("간단 확인", "Quick view");
                case PresetDeep: return Loc.L("집중 분석", "Deep analysis");
                default: return Loc.L("직접 설정", "Custom") + (_diagMode.Value ? Loc.L(" + 원인 추적", " + diagnostics") : string.Empty);
            }
        }

        private string RunWebAction(string name)
        {
            string message;
            switch (name)
            {
                case "gc":
                    message = _gc.Running ? Loc.L("이미 GC 정리 중입니다.", "GC is already running.")
                        : _gc.Start("manual", true, false) ? Loc.L("GC 정리를 시작했습니다. 결과는 아래 '자세한 상태'의 '마지막 GC 정리'에 나옵니다.", "GC started. The result shows under 'last GC' in the full status.")
                        : Loc.L("GC 정리를 시작하지 못했습니다.", "GC could not start.");
                    break;
                case "trim":
                    message = StartTrim("manual") ? Loc.L("워킹셋 정리를 시작했습니다 (별도 스레드).", "Working set trim started (on its own thread).")
                        : Loc.L("이미 워킹셋 정리 중입니다.", "A working set trim is already running.");
                    break;
                case "assets":
                    if (_assetPhase != AssetPhase.Idle)
                    {
                        message = Loc.L("이미 에셋 정리 중입니다.", "Assets are already being unloaded.");
                        break;
                    }

                    StartAssetUnload("manual", _unloadGcFirst.Value, false);
                    message = Loc.L("에셋 정리를 시작했습니다 (게임이 1~3초 멈출 수 있음).", "Asset unload started (the game may pause for 1-3 s).");
                    break;
                case "leak":
                    RunLeakSnapshot("manual");
                    message = Loc.L("누수 추적 기록을 남겼습니다 (전용 로그).", "Leak snapshot taken (dedicated log).");
                    break;
                case "diag":
                    _diagMode.Value = !_diagMode.Value;
                    message = _diagMode.Value ? Loc.L("원인 추적 모드를 켰습니다.", "Diagnostic mode on.") : Loc.L("원인 추적 모드를 껐습니다. 요약은 전용 로그에 저장됩니다.", "Diagnostic mode off. The summary went to the dedicated log.");
                    break;
                default:
                    return "{\"error\":\"unknown action\"}";
            }

            return "{\"msg\":" + WebServer.Str(message) + "}";
        }

        // ---------------------------------------------------------------- settings (main thread)

        /// <summary>One settings row for the web page — this plugin's own entries or another plugin's (CompoundingPerf).</summary>
        private sealed class WebSetting
        {
            public string Key;
            public string Category;
            public string SortKey;
            public string Name;
            public string Description;
            public int Order;
            public ConfigEntryBase Entry;
        }

        private IEnumerable<WebSetting> OwnWebSettings() =>
            _localized.Where(e => e.Entry != null && e.Attributes.CustomDrawer == null && e.Attributes.Browsable != false)
                .Select(e => new WebSetting
                {
                    Key = e.Key,
                    Category = e.Attributes.Category,
                    SortKey = e.CategoryKo,
                    Name = e.Attributes.DispName,
                    Description = e.Attributes.Description ?? string.Empty,
                    Order = e.Attributes.Order ?? 0,
                    Entry = e.Entry,
                });

        private string BuildSettingsJson() => SettingsJson(OwnWebSettings());

        private string ApplyWebSetting(string key, string value) => ApplySetting(OwnWebSettings(), key, value);

        /// <summary>Grouped by category (numbered, so ordinal order is the F12 order), higher Order first like F12.</summary>
        private static string SettingsJson(IEnumerable<WebSetting> settings)
        {
            var json = new StringBuilder(64 * 1024);
            json.Append("{\"groups\":[");
            bool firstGroup = true;
            foreach (IGrouping<string, WebSetting> group in settings
                         .Select((e, i) => new { e, i })
                         .OrderBy(x => x.e.SortKey, StringComparer.Ordinal)
                         .ThenByDescending(x => x.e.Order)
                         .ThenBy(x => x.i)
                         .Select(x => x.e)
                         .GroupBy(e => e.SortKey))
            {
                json.Append(firstGroup ? string.Empty : ",").Append("{\"cat\":").Append(WebServer.Str(group.First().Category)).Append(",\"items\":[");
                firstGroup = false;
                json.Append(string.Join(",", group.Select(SettingJson)));
                json.Append("]}");
            }

            return json.Append("]}").ToString();
        }

        private static string SettingJson(WebSetting e)
        {
            ConfigEntryBase entry = e.Entry;
            Type type = entry.SettingType;
            AcceptableValueBase acceptable = entry.Description.AcceptableValues;
            string kind = type == typeof(bool) ? "bool" : type == typeof(int) ? "int" : type == typeof(float) ? "float" : "text";
            string extra = string.Empty;
            switch (acceptable)
            {
                case AcceptableValueRange<int> ints:
                    extra = ",\"min\":" + ints.MinValue + ",\"max\":" + ints.MaxValue;
                    break;
                case AcceptableValueRange<float> floats:
                    extra = ",\"min\":" + WebServer.Num(floats.MinValue) + ",\"max\":" + WebServer.Num(floats.MaxValue);
                    break;
                case AcceptableValueList<string> list:
                    kind = "list";
                    extra = ",\"opts\":[" + string.Join(",", list.AcceptableValues.Select(WebServer.Str)) + "]";
                    break;
            }

            return "{\"key\":" + WebServer.Str(e.Key) + ",\"name\":" + WebServer.Str(e.Name) + ",\"desc\":" + WebServer.Str(e.Description) +
                   ",\"type\":\"" + kind + "\",\"v\":" + WebServer.Str(entry.GetSerializedValue()) +
                   ",\"def\":" + WebServer.Str(TomlTypeConverter.ConvertToString(entry.DefaultValue, type)) + extra + "}";
        }

        private string ApplySetting(IEnumerable<WebSetting> settings, string key, string value)
        {
            WebSetting target = settings.FirstOrDefault(e => e.Key == key);
            if (target == null || value == null)
            {
                return "{\"ok\":false,\"error\":" + WebServer.Str(Loc.L("모르는 설정입니다.", "Unknown setting.")) + "}";
            }

            string before = target.Entry.GetSerializedValue();
            try
            {
                // Parses, clamps to the allowed range, raises SettingChanged (same as F12) and saves the .cfg.
                target.Entry.BoxedValue = TomlTypeConverter.ConvertToValue(value, target.Entry.SettingType);
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":" + WebServer.Str(Loc.L("값을 읽을 수 없습니다: ", "Can't read that value: ") + ex.Message) + ",\"v\":" + WebServer.Str(before) + "}";
            }

            string after = target.Entry.GetSerializedValue();
            if (before != after)
            {
                Logger.LogInfo($"[web] setting {key}: {before} -> {after}");
            }

            return "{\"ok\":true,\"v\":" + WebServer.Str(after) + "}";
        }
    }
}
