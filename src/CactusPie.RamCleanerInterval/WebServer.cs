using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Small local web server (http://127.0.0.1:port/) so the live dashboard, the session report and the settings can
    /// be opened in a browser while the game runs - the way the SPT server's own pages are. It only moves bytes: requests
    /// are answered on a background thread, and the plugin's handler hands anything that touches the game to the main
    /// thread. Mono's HttpListener is fully managed, so no admin rights or URL reservation are needed, and the default
    /// 127.0.0.1 binding is not reachable from other PCs (no firewall prompt).
    ///
    /// <para>Safety: requests whose Host is not 127.0.0.1 are refused (DNS rebinding), and changes (POST) need a custom
    /// header that a browser only sends from this page itself, so another website cannot change settings
    /// (cross-site request forgery). With "allow LAN" on, anyone on the local network can use the page.</para>
    /// </summary>
    internal sealed class WebServer
    {
        internal const string ChangeHeader = "X-RamCleaner";
        private const int MaxBody = 64 * 1024;

        internal sealed class Response
        {
            public int Status = 200;
            public string ContentType = "text/html; charset=utf-8";
            public string Body = string.Empty;

            public static Response Json(string json, int status = 200) =>
                new Response { Status = status, ContentType = "application/json; charset=utf-8", Body = json };
        }

        private readonly ManualLogSource _log;
        private readonly Func<string, string, string, Response> _handler; // method, path, body
        private readonly object _gate = new object();
        private HttpListener _listener;
        private int _port;
        private bool _lan;
        private string _status;
        private int _lastRequestTick = int.MinValue;

        public WebServer(ManualLogSource log, Func<string, string, string, Response> handler)
        {
            _log = log;
            _handler = handler;
        }

        public bool Running => _listener != null;

        public string Url => Running ? $"http://127.0.0.1:{_port}/" : null;

        public string Status { get => _status ?? Loc.L("꺼짐", "off"); private set => _status = value; }

        /// <summary>True when a browser asked for something in the last <paramref name="seconds"/> seconds.</summary>
        public bool UsedWithin(int seconds) => unchecked(Environment.TickCount - _lastRequestTick) < seconds * 1000 && _lastRequestTick != int.MinValue;

        /// <param name="serverPort">The SPT server's own port (6969): never taken here — it would collide with the server.</param>
        public void Start(int port, bool lan, int serverPort)
        {
            Stop();
            lock (_gate)
            {
                if (port == serverPort)
                {
                    Status = Loc.L($"시작 안 함 — {port}번은 SPT 서버가 쓰는 포트입니다. 다른 번호(기본 6977)로 바꾸세요. 런처 '모드 페이지'의 6969 주소는 서버 부품이 서버 안에서 열어 줍니다.",
                                   $"not started — port {port} belongs to the SPT server. Pick another number (default 6977). The 6969 address in the launcher's mod pages is served by the server part, inside the server.");
                    _log.LogWarning($"[web] port {port} is the SPT server's port - not starting the page");
                    return;
                }

                var listener = new HttpListener();
                try
                {
                    // "*" = every network card (LAN); otherwise only this PC. Mono matches the Host header against the
                    // prefix, so the local page is http://127.0.0.1:port/ exactly (not "localhost").
                    listener.Prefixes.Add(lan ? $"http://*:{port}/" : $"http://127.0.0.1:{port}/");
                    listener.Start();
                }
                catch (Exception ex)
                {
                    try
                    {
                        listener.Close();
                    }
                    catch (Exception)
                    {
                        // already broken
                    }

                    Status = Loc.L($"시작 실패 — 포트 {port}를 다른 프로그램이 쓰는 중일 수 있음 (설정에서 포트 번호를 바꿔 보세요): {ex.Message}",
                                   $"could not start — port {port} may be in use by another program (try another port number in the settings): {ex.Message}");
                    _log.LogWarning($"[web] could not listen on port {port}: {ex.Message}");
                    return;
                }

                _listener = listener;
                _port = port;
                _lan = lan;
                Status = Loc.L($"켜짐 — http://127.0.0.1:{port}/", $"on — http://127.0.0.1:{port}/") +
                         (lan ? Loc.L(" (같은 네트워크의 다른 기기에서도 이 PC의 IP로 접속 가능)", " (other devices on the network can use this PC's IP)") : string.Empty);
                _log.LogInfo($"[web] dashboard at http://127.0.0.1:{port}/" + (lan ? " (LAN allowed)" : string.Empty));

                var thread = new Thread(() => Loop(listener)) { IsBackground = true, Name = "RamCleaner web" };
                thread.Start();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_listener == null)
                {
                    return;
                }

                try
                {
                    _listener.Stop();
                    _listener.Close();
                }
                catch (Exception)
                {
                    // closing anyway
                }

                _listener = null;
                Status = null;
            }
        }

        private void Loop(HttpListener listener)
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception)
                {
                    return; // stopped
                }

                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        private void Handle(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            Response response;
            try
            {
                _lastRequestTick = Environment.TickCount;
                string host = request.UserHostName ?? string.Empty;
                if (!_lan && host != "127.0.0.1" && !host.StartsWith("127.0.0.1:", StringComparison.Ordinal))
                {
                    response = new Response { Status = 403, ContentType = "text/plain; charset=utf-8", Body = "Open http://127.0.0.1:" + _port + "/" };
                }
                else if (request.HttpMethod == "POST" && request.Headers[ChangeHeader] != "1")
                {
                    response = new Response { Status = 403, ContentType = "text/plain; charset=utf-8", Body = "Changes are only accepted from the RAM cleaner page." };
                }
                else
                {
                    string body = request.HttpMethod == "POST" ? ReadBody(request) : null;
                    response = _handler(request.HttpMethod, request.Url.AbsolutePath, body) ??
                               new Response { Status = 404, ContentType = "text/plain; charset=utf-8", Body = "Not found" };
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[web] {request.HttpMethod} {request.Url?.AbsolutePath}: {ex.Message}");
                response = new Response { Status = 500, ContentType = "text/plain; charset=utf-8", Body = ex.Message };
            }

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(response.Body ?? string.Empty);
                HttpListenerResponse output = context.Response;
                output.StatusCode = response.Status;
                output.ContentType = response.ContentType;
                output.Headers["Cache-Control"] = "no-store";
                output.Headers["X-Content-Type-Options"] = "nosniff";
                output.ContentLength64 = bytes.Length;
                output.OutputStream.Write(bytes, 0, bytes.Length);
                output.OutputStream.Close();
            }
            catch (Exception)
            {
                // the browser went away
            }
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                var buffer = new char[4096];
                var sb = new StringBuilder();
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sb.Append(buffer, 0, read);
                    if (sb.Length > MaxBody)
                    {
                        throw new InvalidDataException("request too large");
                    }
                }

                return sb.ToString();
            }
        }

        /// <summary>Value of one field of an application/x-www-form-urlencoded body (null if missing).</summary>
        public static string Form(string body, string name)
        {
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            foreach (string pair in body.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                if (key == name)
                {
                    return eq < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- tiny JSON writer

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Str(string text)
        {
            if (text == null)
            {
                return "null";
            }

            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break; // safe to embed in a <script> block too
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

            return sb.Append('"').ToString();
        }

        public static string Num(double value, string format = "0.###") =>
            double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString(format, Inv);

        public static string Bool(bool value) => value ? "true" : "false";
    }
}
