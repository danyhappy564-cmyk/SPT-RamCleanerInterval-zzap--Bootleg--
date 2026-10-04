using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RamCleanerInterval.Server;

/// <summary>
/// <c>/ramcleaner/...</c> on the SPT server → <c>http://127.0.0.1:&lt;port&gt;/...</c>, the page the RAM cleaner plugin
/// serves from inside the game. The game's pages use relative links, so they work unchanged under this prefix.
/// Port, on/off and language come from the plugin's own config file (BepInEx\config next to SPT_Runtime), so this
/// only works when the server and the game run on the same PC. Viewing follows the SPT web panel's login rules;
/// changes (POST) need an administrator, like SPT's own config editor.
/// </summary>
[Route("ramcleaner")]
[Authorize]
[IgnoreAntiforgeryToken]
public sealed class PageProxyController : ControllerBase
{
    private const string ChangeHeader = "X-RamCleaner";
    private const string ConfigFile = "com.cactuspie.ramcleanerinterval.cfg";

    // The game answers within its own 5 s main-thread timeout; keep a little more than that.
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(7),
    };

    [HttpGet("")]
    public Task<IActionResult> Root() => Request.Path.Value?.EndsWith('/') == true
        ? Forward(string.Empty)
        : Task.FromResult<IActionResult>(Redirect("/ramcleaner/")); // the game's relative links need the trailing slash

    [HttpGet("{**path}")]
    public Task<IActionResult> Get(string path) => Forward(path);

    [HttpPost("{**path}")]
    [Authorize(Policy = "Administrator")]
    public Task<IActionResult> Post(string path) => Forward(path);

    private async Task<IActionResult> Forward(string path)
    {
        Response.Headers.CacheControl = "no-store";
        var settings = PluginSettings.Read();
        bool api = path.StartsWith("api/", StringComparison.Ordinal);
        if (!settings.Enabled)
        {
            return Offline(api, settings, T(settings,
                "RAM 클리너의 웹 페이지가 꺼져 있습니다. 게임 F12 → RAM 클리너 → '18. 웹 페이지' → '웹 페이지 켜기'를 켜세요.",
                "The RAM cleaner's web page is turned off. In the game: F12 → RAM Cleaner → '18. Web page' → 'Enable the web page'."));
        }

        using var request = new HttpRequestMessage(new HttpMethod(Request.Method), $"http://127.0.0.1:{settings.Port}/{path}{Request.QueryString}");
        if (HttpMethods.IsPost(Request.Method))
        {
            if (Request.Headers[ChangeHeader] != "1")
            {
                return StatusCode(403, "Changes are only accepted from the RAM cleaner page.");
            }

            request.Headers.Add(ChangeHeader, "1");
            if (Request.HasFormContentType)
            {
                // MVC's form value provider has already read the body while binding {path}: re-encode the parsed form.
                var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
                request.Content = new FormUrlEncodedContent(form.SelectMany(field => field.Value.Select(value =>
                    new KeyValuePair<string, string>(field.Key, value ?? string.Empty))));
            }
            else
            {
                using var body = new MemoryStream();
                await Request.Body.CopyToAsync(body, HttpContext.RequestAborted);
                request.Content = new ByteArrayContent(body.ToArray());
                request.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType ?? "application/octet-stream");
            }
        }

        try
        {
            using var reply = await Client.SendAsync(request, HttpContext.RequestAborted);
            var bytes = await reply.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted);
            Response.StatusCode = (int)reply.StatusCode;
            Response.ContentType = reply.Content.Headers.ContentType?.ToString() ?? "text/plain; charset=utf-8";
            await Response.Body.WriteAsync(bytes, HttpContext.RequestAborted);
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Refused = nothing listens (game closed, or its page failed to start); timeout = the game is busy (loading).
            var why = ex is TaskCanceledException
                ? T(settings, "시간 초과 — 게임이 켜져 있지만 응답이 늦습니다(레이드 로딩 중일 수 있음).", "timed out — the game is running but slow to answer (maybe loading a raid).")
                : T(settings, "연결 거부 — 게임이 꺼져 있거나, 게임 안 웹 페이지가 시작되지 못했습니다.", "connection refused — the game is closed, or its web page failed to start.");
            return Offline(api, settings, T(settings,
                $"게임 안의 RAM 클리너 페이지(127.0.0.1:{settings.Port})에 연결할 수 없습니다. {why}",
                $"Cannot reach the RAM cleaner page inside the game (127.0.0.1:{settings.Port}): {why}"));
        }
    }

    /// <summary>Past session reports (BepInEx\RamCleaner\RamCleaner-report-*.html) — readable without the game.</summary>
    [HttpGet("reports/{name}")]
    public IActionResult Report(string name)
    {
        var dir = PluginSettings.Read().ReportsDir;
        if (dir is null || Path.GetFileName(name) != name || !name.StartsWith("RamCleaner-report-", StringComparison.Ordinal) ||
            !name.EndsWith(".html", StringComparison.Ordinal) || !System.IO.File.Exists(Path.Combine(dir, name)))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(Path.Combine(dir, name), "text/html; charset=utf-8");
    }

    /// <summary>JSON for the page's scripts (they show "error"), or a small page that retries every 5 s.</summary>
    private ContentResult Offline(bool api, PluginSettings settings, string message)
    {
        if (api)
        {
            return new ContentResult
            {
                StatusCode = 503,
                ContentType = "application/json; charset=utf-8",
                Content = System.Text.Json.JsonSerializer.Serialize(new { error = message }),
            };
        }

        var title = T(settings, "RAM 클리너", "RAM Cleaner");
        var checks = string.Join("", new[]
        {
            settings.ConfigPath is null
                ? T(settings, "게임 쪽 설정 파일을 못 찾음(BepInEx\\config) — 기본 포트 6977로 시도", "game-side config file not found (BepInEx\\config) — trying the default port 6977")
                : T(settings, $"설정 파일: {settings.ConfigPath} · 포트 {settings.Port}", $"config file: {settings.ConfigPath} · port {settings.Port}"),
            T(settings, "게임이 켜져 있는데도 이 화면이면: 게임 F12 → RAM 클리너 → '현재 상태'의 '웹 페이지:' 줄을 확인하세요.",
                        "If the game is running and you still see this: check the 'web page:' line in the game's F12 → RAM Cleaner → 'Status'."),
        }.Select(line => "<li>" + WebUtility.HtmlEncode(line) + "</li>"));
        var reports = ReportList(settings);
        var html = $$"""
            <!doctype html><html lang="{{T(settings, "ko", "en")}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{title}}</title>
            {{OfflineCss}}</head><body><main><h1>{{title}}</h1><div class="card"><p>{{WebUtility.HtmlEncode(message)}}</p>
            <ul class="sub">{{checks}}</ul>
            <p class="sub" id="retry">{{T(settings, "게임 쪽 페이지가 열리면 자동으로 넘어갑니다(5초마다 확인).", "Switches over by itself once the game's page answers (checked every 5 s).")}}</p></div>
            <h2>{{T(settings, "지난 세션 보고서 (게임 없이 열람)", "Past session reports (no game needed)")}}</h2>{{reports}}</main>
            <script>setInterval(function(){fetch('api/live',{cache:'no-store'}).then(function(r){if(r.ok)location.reload()},function(){})},5000)</script></body></html>
            """;
        return new ContentResult { StatusCode = 503, ContentType = "text/html; charset=utf-8", Content = html };
    }

    private const string OfflineCss = """
            <style>
            :root{color-scheme:light;--page:#f9f9f7;--surface:#fcfcfb;--text:#0b0b0b;--text2:#52514e;--border:rgba(11,11,11,.10)}
            @media (prefers-color-scheme:dark){:root{color-scheme:dark;--page:#0d0d0d;--surface:#1a1a19;--text:#fff;--text2:#c3c2b7;--border:rgba(255,255,255,.10)}}
            html,body{margin:0;background:var(--page)}body{color:var(--text);font:14px/1.6 system-ui,-apple-system,'Segoe UI','Malgun Gothic',sans-serif;word-break:keep-all}
            main{max-width:760px;margin:0 auto;padding:32px 16px}h1{font-size:22px;margin:0 0 16px}
            .card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:16px}.sub{color:var(--text2)}
            h2{font-size:17px;margin:28px 0 10px}ul{padding-left:20px}a{color:#3987e5}.reports li{margin:4px 0}
            </style>
        """;

    private static string T(PluginSettings settings, string korean, string english) => settings.English ? english : korean;

    private static string ReportList(PluginSettings settings)
    {
        var files = settings.ReportsDir is null
            ? []
            : new DirectoryInfo(settings.ReportsDir).GetFiles("RamCleaner-report-*.html").OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(15).ToArray();
        if (files.Length == 0)
        {
            return "<p class=\"sub\">" + WebUtility.HtmlEncode(T(settings, "아직 없습니다. 레이드가 한 판 끝나면 만들어집니다('16. 세션 보고서'가 켜져 있을 때).",
                "None yet. One is written after a raid ('16. Session report' on).")) + "</p>";
        }

        return "<ul class=\"reports\">" + string.Join("", files.Select(f =>
            $"<li><a href=\"reports/{WebUtility.HtmlEncode(f.Name)}\">{f.LastWriteTime:yyyy-MM-dd HH:mm}</a> <span class=\"sub\">· {f.Length / 1024} KB</span></li>")) + "</ul>";
    }

    /// <summary>The plugin's port, on/off and language, read from its BepInEx config file on every request (it is tiny).</summary>
    private sealed class PluginSettings
    {
        public int Port = 6977;
        public bool Enabled = true;
        public bool English;
        public string? ConfigPath;
        public string? ReportsDir;

        public static PluginSettings Read()
        {
            var result = new PluginSettings();
            var file = FindConfig();
            if (file is null)
            {
                return result;
            }

            result.ConfigPath = file;
            var reports = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(file)!)!, "RamCleaner"); // BepInEx\RamCleaner
            result.ReportsDir = Directory.Exists(reports) ? reports : null;

            try
            {
                var section = string.Empty;
                foreach (var raw in System.IO.File.ReadLines(file))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        section = line[1..^1];
                        continue;
                    }

                    var eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith('#'))
                    {
                        continue;
                    }

                    var key = line[..eq].Trim();
                    var value = line[(eq + 1)..].Trim();
                    if (section == "18. Web page" && key == "Port" && int.TryParse(value, out var port) && port is > 0 and < 65536)
                    {
                        result.Port = port;
                    }
                    else if (section == "18. Web page" && key == "Enabled")
                    {
                        result.Enabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (section == "0. Mode" && key == "Language")
                    {
                        result.English = value == "English";
                    }
                }
            }
            catch (IOException)
            {
                // being written by the game right now: defaults for this request
            }

            return result;
        }

        private static string? FindConfig()
        {
            // SPT_Runtime\user\mods\RamCleanerInterval.Server\ → up to the SPT folder, which holds BepInEx\config.
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            for (var i = 0; i < 6 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            {
                var candidate = Path.Combine(dir, "BepInEx", "config", ConfigFile);
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
