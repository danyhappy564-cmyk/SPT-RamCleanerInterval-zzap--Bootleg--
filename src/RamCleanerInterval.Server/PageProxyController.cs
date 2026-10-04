using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RamCleanerInterval.Server;

/// <summary>
/// The RAM cleaner's launcher mod page, <c>/ramcleaner/</c> on the SPT server.
/// <list type="bullet">
///   <item>Game running: forwards to the page the plugin serves inside the game (<c>http://127.0.0.1:&lt;port&gt;/...</c>) —
///     live numbers, session report with the raid in progress, settings applied at once. Its links are relative, so
///     they work unchanged under this prefix.</item>
///   <item>Game closed (like the other mod pages, which only need the server): this server's own page — every
///     setting, edited straight in the plugin's .cfg (used at the next game start), and the past session reports.
///     It switches to the live page by itself once the game answers.</item>
/// </list>
/// Same PC only (it reads BepInEx next to SPT_Runtime). Viewing follows the SPT web panel's login rules; changes
/// (POST) need an administrator, like SPT's own config editor, plus the page's own header.
/// </summary>
[Route("ramcleaner")]
[Authorize]
[IgnoreAntiforgeryToken]
public sealed class PageProxyController : ControllerBase
{
    private const string ChangeHeader = "X-RamCleaner";

    private static readonly Lazy<string> OfflineHtml = new(() =>
    {
        using var stream = typeof(PageProxyController).Assembly.GetManifestResourceStream("RamCleanerInterval.Server.offline.html")
                           ?? throw new InvalidOperationException("offline.html is not embedded");
        return new StreamReader(stream).ReadToEnd();
    });

    // The game answers within its own 5 s main-thread timeout; keep a little more than that.
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(7),
    };

    [HttpGet("")]
    public Task<IActionResult> Root() => Request.Path.Value?.EndsWith('/') == true
        ? Forward(string.Empty)
        : Task.FromResult<IActionResult>(Redirect("/ramcleaner/")); // relative links need the trailing slash

    [HttpGet("{**path}")]
    public Task<IActionResult> Get(string path) => Forward(path);

    [HttpPost("{**path}")]
    [Authorize(Policy = "Administrator")]
    public Task<IActionResult> Post(string path) => Forward(path);

    // ---------------------------------------------------------------- game closed: this server's own page

    /// <summary>Settings (catalog + .cfg values), reports and why the game's page is not answering.</summary>
    [HttpGet("offline/api/state")]
    public async Task<IActionResult> OfflineState()
    {
        var files = PluginFiles.Read();
        var (online, reason) = await Probe(files);
        var catalog = files.Catalog();
        var entries = catalog?.GetProperty("entries").EnumerateArray().Select(e =>
        {
            var id = e.GetProperty("section").GetString() + "|" + e.GetProperty("key").GetString();
            return new { id, entry = e, v = files.Values.GetValueOrDefault(id) };
        }).Where(x => x.v is not null).ToList();

        return Json(new
        {
            online,
            reason,
            lang = files.English ? "en" : "ko",
            port = files.Port,
            webEnabled = files.Enabled,
            configPath = files.ConfigPath,
            catalogVersion = catalog?.GetProperty("version").GetString(),
            settings = entries?.Select(x => new { x.id, x.v, meta = x.entry }),
            reports = files.Reports(15).Select(f => new { name = f.Name, time = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), kb = f.Length / 1024 }),
        });
    }

    [HttpPost("offline/api/set")]
    [Authorize(Policy = "Administrator")]
    public async Task<IActionResult> OfflineSet()
    {
        if (Request.Headers[ChangeHeader] != "1")
        {
            return StatusCode(403, "Changes are only accepted from the RAM cleaner page.");
        }

        var form = await Request.ReadFormAsync();
        string id = form["id"].ToString(), value = form["value"].ToString();
        var files = PluginFiles.Read();
        if ((await Probe(files)).online)
        {
            // The running game holds these values in memory and would write over the file: use the live page.
            return Json(new { ok = false, online = true, error = files.English ? "the game is running — reload for the live page" : "게임이 켜져 있습니다 — 새로 고침하면 실시간 화면으로 바뀝니다" });
        }

        var entry = files.Catalog()?.GetProperty("entries").EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("section").GetString() + "|" + e.GetProperty("key").GetString() == id);
        if (entry is null || entry.Value.ValueKind == JsonValueKind.Undefined)
        {
            return Json(new { ok = false, error = "unknown setting" });
        }

        try
        {
            return Json(new { ok = true, v = files.Write(entry.Value, value) });
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return Json(new { ok = false, error = ex.Message, v = files.Values.GetValueOrDefault(id) });
        }
    }

    /// <summary>Past session reports (BepInEx\RamCleaner\RamCleaner-report-*.html) — readable without the game.</summary>
    [HttpGet("reports/{name}")]
    public IActionResult Report(string name)
    {
        var dir = PluginFiles.Read().DataDir;
        if (dir is null || Path.GetFileName(name) != name || !name.StartsWith("RamCleaner-report-", StringComparison.Ordinal) ||
            !name.EndsWith(".html", StringComparison.Ordinal) || !System.IO.File.Exists(Path.Combine(dir, name)))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(Path.Combine(dir, name), "text/html; charset=utf-8");
    }

    // ---------------------------------------------------------------- game running: forward

    private async Task<IActionResult> Forward(string path)
    {
        Response.Headers.CacheControl = "no-store";
        var files = PluginFiles.Read();
        bool api = path.StartsWith("api/", StringComparison.Ordinal);
        if (!files.Enabled)
        {
            return Offline(api, files.English ? "the game's web page is turned off (F12 → RAM Cleaner → 18)" : "게임 쪽 웹 페이지가 꺼져 있음(F12 → RAM 클리너 → 18번)");
        }

        using var request = new HttpRequestMessage(new HttpMethod(Request.Method), $"http://127.0.0.1:{files.Port}/{path}{Request.QueryString}");
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
            return Offline(api, Reason(ex, files));
        }
    }

    /// <summary>The game pages' scripts get JSON they show as an error; a page load gets this server's own page.</summary>
    private IActionResult Offline(bool api, string reason) => api
        ? new ContentResult { StatusCode = 503, ContentType = "application/json; charset=utf-8", Content = JsonSerializer.Serialize(new { error = reason }) }
        : new ContentResult { StatusCode = 200, ContentType = "text/html; charset=utf-8", Content = OfflineHtml.Value };

    private static async Task<(bool online, string reason)> Probe(PluginFiles files)
    {
        if (!files.Enabled)
        {
            return (false, files.English ? "the game's web page is turned off (F12 → RAM Cleaner → 18)" : "게임 쪽 웹 페이지가 꺼져 있음(F12 → RAM 클리너 → 18번)");
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var reply = await Client.GetAsync($"http://127.0.0.1:{files.Port}/favicon.ico", cts.Token);
            return (true, string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return (false, Reason(ex, files));
        }
    }

    /// <summary>Refused = nothing listens (game closed, or its page failed to start); timeout = the game is busy.</summary>
    private static string Reason(Exception ex, PluginFiles files) => ex is HttpRequestException
        ? files.English ? $"no game page on 127.0.0.1:{files.Port} (the game is closed, or its page failed to start)" : $"127.0.0.1:{files.Port}에 게임 쪽 페이지 없음 (게임이 꺼져 있거나 게임 쪽 페이지가 시작되지 못함)"
        : files.English ? $"127.0.0.1:{files.Port} did not answer in time (the game may be loading a raid)" : $"127.0.0.1:{files.Port}이(가) 제때 응답하지 않음 (레이드 로딩 중일 수 있음)";

    private ContentResult Json(object value)
    {
        Response.Headers.CacheControl = "no-store";
        return Content(JsonSerializer.Serialize(value), "application/json; charset=utf-8");
    }
}
