using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The server-side monitor (Agents/docs/dev/debugging.md, "The monitor"): a read-only page on 127.0.0.1 that shows what
/// the Gateway is doing right now — who is in game, in which area and where, their life, the monsters around them, and
/// the last lines of this run's log. It only reads what the server already publishes and answers GET, so watching costs
/// a player nothing and cannot change a game.
/// </summary>
/// <remarks>
/// Game_MONITOR_PORT picks the port: the Gateway's own port + 1 by default, which keeps a stack's page on its own block
/// (live.ps1 -PortBase N → N+5); 0 turns it off. The page is one HTML file and one JSON object per second; nothing is
/// cached, so a browser left open always shows the present.
/// </remarks>
static class ServerMonitor
{
    /// <summary>What live.ps1 redirects the Gateway's console to; the page shows its last lines.</summary>
    const string LogPath = "logs/live-gateway.log";
    const int LogLines = 40;
    const int MaxRequest = 8192;
    const int LogTailBytes = 64 * 1024;

    static readonly DateTime StartedAt = DateTime.UtcNow;
    // When the page first saw each connection in game: how long it has been playing (the page polls every second).
    static readonly ConcurrentDictionary<ClientState, DateTime> FirstSeen = new();

    /// <summary>Where the page listens: the loopback interface unless <c>Game_MONITOR_BIND</c> names another address
    /// (live.ps1 -Lan sets 0.0.0.0, so the people on the same network can trade there). Anything but loopback needs the
    /// page's own sign-in, which is why the reads and the buys below check who is asking.</summary>
    internal static IPAddress Bind =>
        Environment.GetEnvironmentVariable("Game_MONITOR_BIND") is { Length: > 0 } address
            && IPAddress.TryParse(address, out IPAddress? parsed) ? parsed : IPAddress.Loopback;

    /// <summary>The page's port: Game_MONITOR_PORT, else the Gateway's port + 1 (0 or less: no page).</summary>
    internal static int Port =>
        Environment.GetEnvironmentVariable("Game_MONITOR_PORT") is { Length: > 0 } set
            ? int.TryParse(set, out int port) ? port : GatewayEndpoint.Port + 1
            : GatewayEndpoint.Port + 1;

    /// <summary>Starts the page in the background. A port it cannot take is one log line, never a stopped Gateway.</summary>
    public static void Start(CancellationToken stop)
    {
        if (Port > 0) _ = ServeAsync(Port, stop);
    }

    static async Task ServeAsync(int port, CancellationToken stop)
    {
        var listener = new TcpListener(Bind, port);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            Console.WriteLine($"[monitor] 127.0.0.1:{port} is taken, so there is no monitor page: {e.Message}");
            return;
        }
        Console.WriteLine($"[monitor] http://127.0.0.1:{port}/ (read-only: who is in game, and what the log last said)");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    break;
                }
                _ = ServeOne(client);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>One HTTP/1.1 request, one response, then the connection closes. Only GET is answered.</summary>
    static async Task ServeOne(TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                byte[] buffer = new byte[MaxRequest];
                int read = 0, headersEnd = -1;
                while (read < buffer.Length)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    int got = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                    if (got == 0) break;
                    read += got;
                    if (headersEnd < 0 && buffer.AsSpan(0, read).IndexOf("\r\n\r\n"u8) is int at and >= 0) headersEnd = at + 4;
                    // A write carries a JSON body: wait for it as well, so /grant sees the whole request.
                    if (headersEnd >= 0 && read >= headersEnd + ContentLength(Encoding.ASCII.GetString(buffer, 0, headersEnd))) break;
                }
                if (read == 0) return;
                string request = Encoding.ASCII.GetString(buffer, 0, read);
                string[] parts = request.Split(' ');
                string method = parts.Length > 0 ? parts[0] : "GET";
                string path = parts.Length > 1 ? parts[1] : "/";
                string query = "";
                if (path.IndexOf('?') is int start and >= 0)
                {
                    query = path[(start + 1)..];
                    path = path[..start];
                }

                string? session = MarketSession.AccountOf(MarketSession.TokenOf(Header(request, "Authorization"), Query(query, "session")));
                bool admin = MonitorGrant.Token is { } owner && (owner == Header(request, "X-Admin-Token") || owner == Query(query, "admin"));
                string wanted = Query(query, "account") ?? "";
                (string Body, string Type, int Status) answer = path switch
                {
                    "/" or "/index.html" => (Page, "text/html; charset=utf-8", 200),
                    "/state" => (Snapshot(), "application/json; charset=utf-8", 200),
                    // Who is signed in (Market/MarketSession.cs): the page asks this before it offers to buy.
                    "/me" => session is { } me
                        ? (new JsonObject { ["ok"] = true, ["account"] = me }.ToJsonString(), "application/json; charset=utf-8", 200)
                        : (new JsonObject { ["ok"] = false, ["error"] = "not signed in" }.ToJsonString(), "application/json; charset=utf-8", 401),
                    "/login" when method == "POST" => WithBody(request, SignIn),
                    "/logout" when method == "POST" => WithBody(request, element =>
                    {
                        MarketSession.SignOut(Text(element, "session"));
                        return (new JsonObject { ["ok"] = true }.ToJsonString(), "application/json; charset=utf-8", 200);
                    }),
                    // What an account's stash holds (MonitorData.cs): own account with a session, any with the admin token.
                    "/stash" when (admin || (session is { } self && string.Equals(self, wanted, StringComparison.OrdinalIgnoreCase)))
                        => MonitorData.Stash(wanted, Query(query, "league")) is { } stash
                            ? (stash, "application/json; charset=utf-8", 200)
                            : ("account= is required", "text/plain; charset=utf-8", 400),
                    "/stash" => ("sign in as that account, or use the owner's token", "text/plain; charset=utf-8", 403),
                    "/accounts" when admin => (MonitorData.Accounts(), "application/json; charset=utf-8", 200),
                    // The currency exchange (Market/MarketExchange.cs): Faustus's book, with nobody in game.
                    // The affix texts the page offers while a search is typed (MonitorData.StatSuggestions).
                    "/stats" => (MonitorData.StatSuggestions(Query(query, "q") ?? ""), "application/json; charset=utf-8", 200),
                    "/exchange" => (MarketExchange.View(session), "application/json; charset=utf-8", 200),
                    "/exchange/order" when method == "POST" => WithBody(request, element => Order(element, session)),
                    "/exchange/cancel" when method == "POST" => WithBody(request, element => Cancel(element, session)),
                    "/accounts" => ("the owner's token is required", "text/plain; charset=utf-8", 403),
                    // The market index (Market/MarketIndex.cs): every public premium tab's priced items.
                    "/market" => (MarketIndex.Listings(new MarketIndex.Filter(
                        Query(query, "q"), Query(query, "class"), Query(query, "rarity"), Query(query, "currency"),
                        long.TryParse(Query(query, "min"), out long min) ? min : null,
                        long.TryParse(Query(query, "max"), out long max) ? max : null,
                        Query(query, "online") is "1" or "true", Query(query, "stats"))), "application/json; charset=utf-8", 200),
                    // The writes: a player's own listing with a session, anything with the owner's admin token.
                    "/market/set" when method == "POST" => WithBody(request, element => (session is { } own
                        ? MarketAdmin.Apply(element, own, out int ownStatus)
                        : MarketAdmin.Write(element, out ownStatus), "application/json; charset=utf-8", ownStatus)),
                    "/grant" when method == "POST" => WithBody(request, element => (MonitorGrant.Write(element, out int grantStatus), "application/json; charset=utf-8", grantStatus)),
                    // The item forge (Monitoring/MonitorForge.cs): an admin composes an item from the base's own
                    // affix pools and it is granted like /grant above; /forge/preview shows the card without placing.
                    "/forge/catalog" or "/forge" or "/forge/preview" =>
                        MonitorForge.Answer(path, method, method == "POST" ? BodyElement(request) : null)
                            ?? ("POST a JSON body: {token, account, base, ...}", "text/plain; charset=utf-8", 405),
                    // Buying needs a signed-in buyer: that is who pays, and the seller never has to be here.
                    "/buy" when method == "POST" => WithBody(request, element => Buy(element, session)),
                    "/grant" or "/market/set" or "/buy" or "/exchange/order" or "/exchange/cancel" or "/forge" or "/forge/preview" => ("POST a JSON body: {token, …}", "text/plain; charset=utf-8", 405),
                    _ => ("not found", "text/plain; charset=utf-8", 404),
                };
                byte[] body = Encoding.UTF8.GetBytes(answer.Body);
                string head = $"HTTP/1.1 {answer.Status} {Phrase(answer.Status)}\r\n"
                    + $"Content-Type: {answer.Type}\r\nContent-Length: {body.Length}\r\n"
                    + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // A browser that closed the tab, or one that sent something that is not a request: nothing to answer.
            }
            catch (Exception e)
            {
                // A request that could not be answered (a stash the save database refuses, say) is one line and a 500:
                // the page and the next request carry on.
                Console.WriteLine($"[monitor] {tcp.Client.RemoteEndPoint} {e.GetType().Name}: {e.Message}");
                try
                {
                    await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes(
                        "HTTP/1.1 500 Internal Server Error\r\nContent-Type: text/plain; charset=utf-8\r\n"
                        + $"Content-Length: {Encoding.UTF8.GetByteCount(e.Message)}\r\nConnection: close\r\n\r\n{e.Message}"));
                }
                catch (Exception)
                {
                    // The client is gone as well.
                }
            }
        }
    }

    /// <summary>The reason phrase of a status a request can be answered with.</summary>
    static string Phrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        _ => "Not Found",
    };

    /// <summary>The <c>Content-Length</c> a request's headers announce, or 0.</summary>
    static int ContentLength(string headers)
    {
        if (headers.IndexOf("\r\n\r\n", StringComparison.Ordinal) is int end and >= 0) headers = headers[..end];
        foreach (string line in headers.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[15..].Trim(), out int length)) return length;
        return 0;
    }

    /// <summary>A write: the JSON body of a POST, handed to the route that answers it.</summary>
    static (string Body, string Type, int Status) WithBody(string request, Func<JsonElement, (string Body, string Type, int Status)> route)
    {
        const string json = "application/json; charset=utf-8";
        int at = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string body = at < 0 ? "" : request[(at + 4)..];
        if (ContentLength(request) is int length and > 0 && body.Length > length) body = body[..length];
        try
        {
            using var document = JsonDocument.Parse(body);
            return route(document.RootElement);
        }
        catch (JsonException e)
        {
            return (new JsonObject { ["ok"] = false, ["error"] = $"the body is not JSON: {e.Message}" }.ToJsonString(), json, 400);
        }
    }

    /// <summary>A POST body parsed for a route that takes the element itself (the forge), or null when it is not JSON.
    /// The forge's own errors answer as JSON, so a parse failure does too.</summary>
    static JsonElement? BodyElement(string request)
    {
        int at = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string body = at < 0 ? "" : request[(at + 4)..];
        if (ContentLength(request) is int length and > 0 && body.Length > length) body = body[..length];
        try
        {
            var document = JsonDocument.Parse(body);
            return document.RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One request header's value, or null.</summary>
    static string? Header(string request, string name)
    {
        int end = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = end < 0 ? request : request[..end];
        foreach (string line in head.Split("\r\n").Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return line[(colon + 1)..].Trim();
        }
        return null;
    }

    /// <summary>A string field of a JSON body, or null.</summary>
    static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    /// <summary>A sign-in (Market/MarketSession.cs): the same account and password the game itself takes.</summary>
    static (string Body, string Type, int Status) SignIn(JsonElement body)
    {
        const string json = "application/json; charset=utf-8";
        string? who = MarketSession.SignIn(Text(body, "account") ?? "", Text(body, "password") ?? "", out string token, out string error);
        return who is null
            ? (new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(), json, 401)
            : (new JsonObject { ["ok"] = true, ["account"] = who, ["token"] = token }.ToJsonString(), json, 200);
    }

    /// <summary>A buy (Market/MarketTrade.cs): the signed-in buyer pays, and the seller needs to be nowhere.</summary>
    static (string Body, string Type, int Status) Buy(JsonElement body, string? buyer)
    {
        const string json = "application/json; charset=utf-8";
        if (buyer is null) return (new JsonObject { ["ok"] = false, ["error"] = "sign in first" }.ToJsonString(), json, 403);
        string seller = Text(body, "seller") ?? "";
        string? league = Text(body, "league");
        int tab = body.TryGetProperty("tab", out var tabValue) && tabValue.TryGetInt32(out int wantedTab) ? wantedTab : -1;
        uint itemId = body.TryGetProperty("itemId", out var idValue) && idValue.TryGetUInt32(out uint id) ? id : 0;
        return (MarketTrade.Buy(buyer, seller, league, tab, itemId, out int status), json, status);
    }

    /// <summary>Placing an exchange order (Market/MarketExchange.cs): the currency offered leaves the stash now.</summary>
    static (string Body, string Type, int Status) Order(JsonElement body, string? account)
    {
        const string json = "application/json; charset=utf-8";
        if (account is null) return (new JsonObject { ["ok"] = false, ["error"] = "sign in first" }.ToJsonString(), json, 403);
        string give = Text(body, "give") ?? "";
        string want = Text(body, "want") ?? "";
        string? league = Text(body, "league");
        int giveAmount = body.TryGetProperty("giveAmount", out var g) && g.TryGetInt32(out int ga) ? ga : 0;
        int wantAmount = body.TryGetProperty("wantAmount", out var w) && w.TryGetInt32(out int wa) ? wa : 0;
        return (MarketExchange.Place(account, league, give, giveAmount, want, wantAmount, out int status), json, status);
    }

    /// <summary>Cancelling one of your exchange orders, returning what it still holds.</summary>
    static (string Body, string Type, int Status) Cancel(JsonElement body, string? account)
    {
        const string json = "application/json; charset=utf-8";
        if (account is null) return (new JsonObject { ["ok"] = false, ["error"] = "sign in first" }.ToJsonString(), json, 403);
        long id = body.TryGetProperty("id", out var value) && value.TryGetInt64(out long wanted) ? wanted : 0;
        return (MarketExchange.Cancel(account, id, out int status), json, status);
    }

    /// <summary>One value from a request's query string (<c>?account=exile&amp;league=Standard</c>), or null.</summary>
    static string? Query(string query, string key)
    {
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string name = equals < 0 ? pair : pair[..equals];
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            string value = equals < 0 ? "" : pair[(equals + 1)..];
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return null;
    }

    /// <summary>What the page shows: the server, the players in game, and the log's last lines.</summary>
    internal static string Snapshot()
    {
        DateTime now = DateTime.UtcNow;
        var players = new JsonArray();
        foreach (ClientState conn in Program.InGame.Keys)
        {
            try
            {
                players.Add(Player(conn, now));
            }
            catch (Exception e) when (e is ObjectDisposedException or NullReferenceException or InvalidOperationException)
            {
                // The connection went away between the key and the read; the next poll leaves it out.
            }
        }
        // A player who left is forgotten, so the page does not keep the connections of a whole session alive.
        foreach (ClientState gone in FirstSeen.Keys.Where(c => !Program.InGame.ContainsKey(c)).ToList())
            FirstSeen.TryRemove(gone, out _);

        return new JsonObject
        {
            ["server"] = new JsonObject
            {
                ["uptimeSeconds"] = (long)(now - StartedAt).TotalSeconds,
                ["port"] = GatewayEndpoint.Port,
                ["players"] = players.Count,
                ["memoryMb"] = GC.GetTotalMemory(false) / (1024 * 1024),
            },
            ["players"] = players,
            ["log"] = new JsonArray([.. LogTail().Select(line => (JsonNode)line)]),
        }.ToJsonString();
    }

    static JsonObject Player(ClientState conn, DateTime now)
    {
        (int X, int Y) at;
        try
        {
            at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        }
        catch (NullReferenceException)
        {
            at = default;
        }
        DateTime seen = FirstSeen.GetOrAdd(conn, _ => now);
        return new JsonObject
        {
            ["name"] = Program.CharacterName(conn),
            ["level"] = conn.Pools.Level,
            ["area"] = conn.Layout.Id,
            ["seed"] = conn.Seed,
            ["x"] = at.X,
            ["y"] = at.Y,
            ["life"] = conn.PlayerLife,
            ["maxLife"] = conn.PlayerMaxLife,
            ["monsters"] = conn.Monsters.Count,
            ["seconds"] = (long)Math.Max(0, (now - seen).TotalSeconds),
        };
    }

    /// <summary>The last lines of this run's Gateway log (live.ps1's logs/live-gateway.log), or none.</summary>
    static string[] LogTail()
    {
        try
        {
            if (!File.Exists(LogPath)) return [];
            using var file = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Max(0, file.Length - LogTailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            return [.. reader.ReadToEnd().Replace("\r", "").Split('\n').Where(line => line.Length > 0).TakeLast(LogLines)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The page: one table of the players and the log tail, drawn from /state once a second.</summary>
    const string Page = """
        <!doctype html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>LocalExile 服务器监测</title>
        <style>
          :root { color-scheme: dark; }
          body { margin: 0; padding: 18px 22px; background: #14161a; color: #d8dee9;
                 font: 14px/1.5 "Segoe UI", "Microsoft YaHei", sans-serif; }
          h1 { font-size: 18px; margin: 0 0 4px; font-weight: 600; }
          h1 small { font-size: 12px; color: #7b8494; font-weight: 400; margin-left: 8px; }
          h2 { font-size: 14px; margin: 22px 0 6px; color: #9aa4b2; font-weight: 600; }
          #summary { color: #9aa4b2; font-size: 13px; }
          #status { font-size: 12px; color: #6b7385; margin-left: 10px; }
          table { border-collapse: collapse; width: 100%; margin-top: 9px; }
          th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid #232833; white-space: nowrap; }
          th { color: #7b8494; font-weight: 500; font-size: 12px; }
          td.num { font-variant-numeric: tabular-nums; }
          .bar { display: inline-block; width: 90px; height: 7px; background: #2a303c; border-radius: 4px;
                 vertical-align: middle; margin-right: 7px; overflow: hidden; }
          .bar i { display: block; height: 100%; background: #4d9a63; }
          .low i { background: #b8564d; }
          .empty { color: #6b7385; padding: 12px 0; }
          #log { margin: 0; padding: 10px 12px; background: #0f1115; border: 1px solid #232833; border-radius: 6px;
                 height: 300px; overflow: auto; font: 12px/1.45 Consolas, monospace; color: #a8b3c2; white-space: pre-wrap; }
          #log b { color: #e0a458; }
          .row { display: flex; gap: 8px; align-items: center; margin-top: 8px; flex-wrap: wrap; }
          select { background: #1b1f27; color: #d8dee9; border: 1px solid #2a303c; border-radius: 6px;
                   padding: 4px 8px; font: 13px "Segoe UI", "Microsoft YaHei", sans-serif; }
          .dim { color: #6b7385; font-size: 12px; }
          .chips { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 8px; }
          .chip { background: #1b1f27; border: 1px solid #2a303c; border-radius: 10px; padding: 2px 9px;
                  font-size: 12px; color: #c8b06a; }
          td.name { color: #d8dee9; }
          input { background: #1b1f27; color: #d8dee9; border: 1px solid #2a303c; border-radius: 6px;
                  padding: 5px 8px; font: 13px "Segoe UI", "Microsoft YaHei", sans-serif; }
          #gitem { width: 260px; }
          #gcount { width: 64px; }
          #gtoken { width: 230px; }
          #mstats { width: 330px; }
          button { background: #2c6e4a; color: #eaf3ea; border: 1px solid #37865a; border-radius: 6px;
                   padding: 5px 14px; font: 13px "Segoe UI", "Microsoft YaHei", sans-serif; cursor: pointer; }
          button:hover { background: #37865a; }
          button.copy { padding: 2px 9px; font-size: 12px; background: #2b3a4d; border-color: #3a4d63; }
          button.buy { padding: 2px 9px; font-size: 12px; margin-left: 6px; }
          button.card-btn { padding: 2px 9px; font-size: 12px; background: #3d3a2b; border-color: #55503a; margin-right: 6px; }
          button.cancel { padding: 2px 9px; font-size: 12px; background: #4d2b2b; border-color: #632f2f; }
          .price { color: #c8b06a; }
          .on { color: #4d9a63; font-size: 12px; }
          .card { display: none; margin: 10px 0 0; padding: 12px 14px; background: #0f1115;
                  border: 1px solid #232833; border-radius: 6px; font: 13px/1.6 "Segoe UI", "Microsoft YaHei", sans-serif; }
          .card.open { display: block; }
          .card .name { font-size: 15px; font-weight: 600; }
          .card .base { color: #9aa4b2; }
          .card .sep { height: 1px; background: #232833; margin: 8px 0; }
          .card .enchant { color: #b4b4ff; }
          .card .implicit { color: #8888ff; }
          .card .explicit { color: #8888ff; }
          .card .fractured { color: #a29162; }
          .card .facts { color: #7b8494; font-size: 12px; }
          .card .close { float: right; cursor: pointer; color: #6b7385; font-size: 12px; }
          .card .tier { display: inline-block; min-width: 22px; margin-right: 6px; padding: 0 5px; border-radius: 4px;
                        background: #232833; color: #c8b06a; font-size: 11px; text-align: center; }
          .card .copy-item { cursor: pointer; color: #4d9a63; font-size: 12px; margin-right: 10px; }
          .off { color: #6b7385; font-size: 12px; }
        </style>
        </head>
        <body>
        <h1>LocalExile 服务器监测<small>只读 · 每秒刷新</small><span id="status">连接中…</span></h1>
        <div class="row">
          <input id="lacc" placeholder="账号（和游戏一样）" spellcheck="false">
          <input id="lpw" type="password" placeholder="密码">
          <button id="lsignin">登录</button>
          <button id="lsignout">退出</button>
          <span id="lwho" class="dim">未登录：只能浏览市场，登录后才能上架和购买</span>
        </div>
        <div id="summary"></div>
        <table>
          <thead><tr><th>角色</th><th>等级</th><th>区域</th><th>位置</th><th>生命</th><th>区域怪物</th><th>已在线</th></tr></thead>
          <tbody id="players"><tr><td colspan="7" class="empty">读取中…</td></tr></tbody>
        </table>
        <h2>仓库<small id="who"></small></h2>
        <div class="row">
          <select id="account" title="账号"></select>
          <select id="tab" title="仓库页"></select>
          <span id="stashinfo" class="dim"></span>
        </div>
        <div id="currency" class="chips"></div>
        <table>
          <thead><tr><th>物品</th><th>类别</th><th>稀有度</th><th>物品等级</th><th>词缀</th><th>数量</th><th>格子</th></tr></thead>
          <tbody id="items"><tr><td colspan="7" class="empty">读取中…</td></tr></tbody>
        </table>
        <h2>市场<small>官方规则：只有高级页能公开，备注写 ~price 5 chaos</small></h2>
        <div class="row">
          <input id="mq" placeholder="物品名关键词" spellcheck="false">
          <select id="mclass"><option value="">全部类别</option></select>
          <select id="mrarity"><option value="">全部稀有度</option></select>
          <select id="mcur"><option value="">全部通货</option></select>
          <input id="mmin" type="number" placeholder="最低价" title="最低价">
          <input id="mmax" type="number" placeholder="最高价" title="最高价">
          <input id="mstats" list="statlist" placeholder="词缀：to maximum Life&gt;=60; Fire Resistance&gt;=30" spellcheck="false" title="按词缀搜：打两个字会给出客户端自己的文案，可加 &gt;= 数值，分号分隔多条">
          <label class="dim"><input id="monline" type="checkbox"> 只看在线</label>
          <button id="mfind">搜索</button>
          <span id="minfo" class="dim"></span>
        </div>
        <div class="row">
          <span class="dim">上架当前页：</span>
          <input id="mtoken" type="password" placeholder="密码 = Game_MONITOR_ADMIN_TOKEN">
          <input id="mnote" placeholder="整页备注，如 ~price 5 chaos" spellcheck="false">
          <label class="dim"><input id="mpub" type="checkbox"> 公开「<span id="mtabname">—</span>」页</label>
          <button id="msave">保存上架</button>
          <span id="mres" class="dim"></span>
        </div>
        <table>
          <thead><tr><th>物品</th><th>类别</th><th>稀有度</th><th>物品等级</th><th>数量</th><th>价格</th><th>卖家</th><th>密语</th></tr></thead>
          <tbody id="mlist"><tr><td colspan="8" class="empty">点「搜索」看市场上有哪些挂单</td></tr></tbody>
        </table>
        <div id="mcard" class="card"></div>
        <datalist id="statlist"></datalist>
        <h2>通货兑换<small>浮士德那套规则：挂单撮合 · 收金币手续费 · 谁都不用在线</small></h2>
        <div class="row">
          <span class="dim">我给</span>
          <select id="xgive"></select>
          <input id="xgiveAmount" type="number" value="100" min="1" title="给多少">
          <span class="dim">我要</span>
          <select id="xwant"></select>
          <input id="xwantAmount" type="number" value="1" min="1" title="要多少">
          <button id="xplace">挂单</button>
          <span id="xres" class="dim"></span>
        </div>
        <table>
          <thead><tr><th>谁</th><th>给</th><th>还剩</th><th>要</th><th>要多少</th><th>比例</th><th></th></tr></thead>
          <tbody id="xbook"><tr><td colspan="7" class="empty">还没有挂单</td></tr></tbody>
        </table>
        <h2>发放<small>放进「市场」页 · 需要密码</small></h2>
        <div class="row">
          <input id="gitem" placeholder="物品名，如 Chaos Orb" spellcheck="false">
          <input id="gcount" type="number" value="1" min="1" max="20" title="数量">
          <input id="gtoken" type="password" placeholder="密码 = Game_MONITOR_ADMIN_TOKEN">
          <button id="ggive">发放</button>
          <span id="gres" class="dim"></span>
        </div>
        <h2>自定义装备<small>原版词缀池 · 前缀后缀照稀有度上限 · 发放进「市场」页</small></h2>
        <div class="row">
          <select id="fclass"><option value="">全部类别</option></select>
          <select id="fbase"><option value="">先选类别，再选底子</option></select>
          <select id="frarity">
            <option value="rare">稀有（各 3 词缀）</option>
            <option value="magic">魔法（各 1 词缀）</option>
            <option value="normal">普通（无词缀）</option>
          </select>
          <input id="filvl" type="number" value="84" min="1" max="100" title="物品等级：决定能出哪些词缀">
          <input id="fquality" type="number" value="20" min="0" max="100" title="品质">
          <input id="fsockets" placeholder="孔位，可空，如 R-R-G" spellcheck="false" title="和 /sockets 一样；留空按底子默认">
          <input id="fstates" placeholder="状态，可空，如 corrupted, shaper" spellcheck="false" title="corrupted, mirrored, split, synthesised, shaper, elder, crusader, redeemer, hunter, warlord">
          <input id="ftoken" type="password" placeholder="密码 = Game_MONITOR_ADMIN_TOKEN">
          <button id="fload">载入词缀</button>
        </div>
        <div class="row" id="faffixrow" style="display:none">
          <div id="faffixes" style="width:100%"></div>
        </div>
        <div class="row" id="fcardrow" style="display:none">
          <div id="fcard" class="card open" style="display:block"></div>
        </div>
        <div class="row">
          <button id="fpreview">预览</button>
          <button id="fforge">发放</button>
          <input id="faccount" placeholder="发给哪个账号" spellcheck="false" title="留空发给上面登录的账号">
          <span id="fres" class="dim"></span>
        </div>
        <h2>最近日志</h2>
        <pre id="log"></pre>
        <script>
        const $ = s => document.querySelector(s);
        const log = $('#log');
        let pinned = true;
        log.addEventListener('scroll', () => { pinned = log.scrollTop + log.clientHeight >= log.scrollHeight - 20; });
        const esc = t => String(t).replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
        function dur(s) {
          if (s < 60) return s + ' 秒';
          if (s < 3600) return Math.floor(s / 60) + ' 分 ' + (s % 60) + ' 秒';
          return Math.floor(s / 3600) + ' 时 ' + Math.floor(s % 3600 / 60) + ' 分';
        }
        function render(s) {
          $('#summary').textContent = '运行 ' + dur(s.server.uptimeSeconds) + ' · 端口 ' + s.server.port
            + ' · 内存 ' + s.server.memoryMb + ' MB · 在线 ' + s.server.players + ' 人';
          $('#players').innerHTML = s.players.length ? s.players.map(p => {
            const pct = p.maxLife ? Math.round(p.life / p.maxLife * 100) : 0;
            return '<tr><td>' + esc(p.name) + '</td><td class="num">' + p.level + '</td><td>' + esc(p.area) + '</td>'
              + '<td class="num">' + p.x + ', ' + p.y + '</td>'
              + '<td class="num"><span class="bar' + (pct < 35 ? ' low' : '') + '"><i style="width:' + pct + '%"></i></span>'
              + p.life + ' / ' + p.maxLife + '</td>'
              + '<td class="num">' + p.monsters + '</td><td class="num">' + dur(p.seconds) + '</td></tr>';
          }).join('') : '<tr><td colspan="7" class="empty">现在没有人在游戏里</td></tr>';
          log.innerHTML = s.log.map(l => esc(l).replace(/^(\d\d:\d\d:\d\d\.\d{3})/, '<b>$1</b>')).join('\n');
          if (pinned) log.scrollTop = log.scrollHeight;
        }
        const acct = $('#account'), tabSel = $('#tab');
        const who = () => acct.value || (accounts[0] ? accounts[0].name : '');
        let session = localStorage.getItem('lx_session') || '';
        let me = '';
        const adminToken = () => $('#gtoken').value;
        const api = (path, opts) => {
          const options = opts || {};
          if (session) options.headers = Object.assign({}, options.headers, { Authorization: 'Bearer ' + session });
          return fetch(path, options);
        };
        function showWho() {
          $('#lwho').textContent = me ? ('已登录：' + me) : '未登录：只能浏览市场，登录后才能上架和购买';
          $('#lacc').value = me || $('#lacc').value;
        }
        async function loadMe() {
          if (!session) { me = ''; showWho(); return; }
          try {
            const d = await (await api('/me', { cache: 'no-store' })).json();
            me = d.ok ? d.account : '';
          } catch (e) { me = ''; }
          if (!me) { session = ''; localStorage.removeItem('lx_session'); }
          showWho();
        }
        async function signIn() {
          $('#lwho').textContent = '登录中…';
          try {
            const d = await (await fetch('/login', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ account: $('#lacc').value, password: $('#lpw').value }),
            })).json();
            if (!d.ok) { $('#lwho').textContent = '登录失败：' + d.error; return; }
            session = d.token; me = d.account; localStorage.setItem('lx_session', session);
            $('#lpw').value = '';
            showWho();
            await loadAccounts();
            await loadMarket();
          } catch (e) { $('#lwho').textContent = '登录失败'; }
        }
        async function signOut() {
          try {
            await fetch('/logout', { method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ session: session }) });
          } catch (e) { }
          session = ''; me = ''; localStorage.removeItem('lx_session'); showWho();
          await loadAccounts();
        }
        let accounts = [], stash = null, tab = 0;
        const cards = {}, rarities = {};
        const RARITY_COLOUR = { Normal: '#c8c8c8', Magic: '#8888ff', Rare: '#ffff77', Unique: '#af6025' };
        function showCard(id) {
          const card = cards[id];
          const box = $('#mcard');
          if (!card) { box.classList.remove('open'); return; }
          const head = card[0] ? esc(card[0].text) : '';
          const colour = RARITY_COLOUR[rarities[id]] || '#d8dee9';
          const body = card.slice(1).map(line =>
            '<div class="' + line.kind + '">' + (line.tier ? '<span class="tier">T' + line.tier + '</span>' : '')
            + esc(line.text) + '</div>').join('');
          box.innerHTML = '<span class="close">关闭</span><span class="copy-item">复制物品文本</span>'
            + '<div class="name" style="color:' + colour + '">' + head + '</div>'
            + '<div class="sep"></div>' + body;
          box.classList.add('open');
          const close = box.querySelector('.close');
          if (close) close.addEventListener('click', () => box.classList.remove('open'));
          const copyItem = box.querySelector('.copy-item');
          if (copyItem) copyItem.addEventListener('click', async () => {
            const text = card.map(line => line.text).join('\n');
            try { await navigator.clipboard.writeText(text); copyItem.textContent = '已复制'; }
            catch (e) { copyItem.textContent = '复制失败'; }
          });
        }
        async function loadAccounts() {
          try {
            accounts = me ? [{ name: me, characters: [] }]
              : adminToken() ? (await (await fetch('/accounts?admin=' + encodeURIComponent(adminToken()), { cache: 'no-store' })).json()).accounts
              : [];
          } catch (e) { accounts = []; }
          acct.innerHTML = accounts.map(a => '<option>' + esc(a.name) + '</option>').join('');
          if (accounts.length) await loadStash(accounts[0].name);
          else $('#items').innerHTML = '<tr><td colspan="7" class="empty">没有账号</td></tr>';
        }
        async function loadStash(name) {
          const a = accounts.find(x => x.name === name);
          $('#who').textContent = a && a.characters.length
            ? ' ' + a.characters.map(c => c.name + ' Lv' + c.level + ' ' + c.class).join(' · ') : '';
          try {
            const q = session ? '&session=' + encodeURIComponent(session) : (adminToken() ? '&admin=' + encodeURIComponent(adminToken()) : '');
            const r = await api('/stash?account=' + encodeURIComponent(name) + q, { cache: 'no-store' });
            stash = r.ok ? await r.json() : null;
            if (!r.ok) $('#mres').textContent = '看不了这个仓库：不是你的账号，或者没登录';
          } catch (e) { stash = null; }
          tab = 0;
          renderStash();
        }
        function renderStash() {
          if (!stash) { $('#stashinfo').textContent = '读取失败'; $('#items').innerHTML = ''; return; }
          const tabs = stash.tabs || [];
          tabSel.innerHTML = tabs.length
            ? tabs.map(t => '<option value="' + t.tab + '">' + esc(t.name) + '（' + t.items.length + '件'
                + (t.premium ? (t.public ? ' · 已公开' : ' · 可挂单') : ' · 普通页，不能挂单') + '）</option>').join('')
            : '<option value="0">空</option>';
          // The market's own page is what a listing is about, so it is what the panel opens on when it has one.
          const chosen = tabs.find(t => t.name === '市场') || tabs[0];
          tab = chosen ? chosen.tab : 0;
          tabSel.value = String(tab);
          showTab();
          $('#stashinfo').textContent = '金币 ' + stash.gold + ' · 物品 ' + stash.itemCount + ' 件 · 仓库页 ' + stash.tabCount;
          $('#currency').innerHTML = Object.entries(stash.currency || {}).slice(0, 14)
            .map(([n, c]) => '<span class="chip">' + esc(n) + ' ×' + c + '</span>').join('');
          renderItems();
        }
        function renderItems() {
          const t = (stash.tabs || []).find(x => x.tab === tab);
          if (!t) { $('#items').innerHTML = '<tr><td colspan="7" class="empty">这一页是空的</td></tr>'; return; }
          $('#items').innerHTML = t.items.map(i =>
            '<tr><td class="name">' + esc(i.name) + (i.corrupted ? '（腐化）' : '') + '</td><td>' + esc(i.class) + '</td>'
            + '<td>' + esc(i.rarity) + '</td><td class="num">' + i.ilvl + '</td><td class="num">' + i.mods + '</td>'
            + '<td class="num">' + i.stack + '</td><td class="num">' + i.x + ', ' + i.y + '</td></tr>').join('');
        }
        acct.addEventListener('change', () => loadStash(who()));
        tabSel.addEventListener('change', () => { tab = parseInt(tabSel.value, 10); renderItems(); showTab(); });
        function showTab() {
          const t = (stash && stash.tabs ? stash.tabs : []).find(x => x.tab === tab);
          $('#mtabname').textContent = t ? t.name : '—';
          // The page shows what the chosen tab is now, so a save cannot silently undo a listing.
          if (t) { $('#mpub').checked = !!t.public; $('#mnote').value = t.note || ''; }
          $('#mres').textContent = t && !t.premium
            ? '普通仓库页不能公开挂单：先把物品放到高级页（Premium / Loot / Quad / 市场）' : '';
        }
        async function loadMarket() {
          const q = new URLSearchParams();
          if ($('#mq').value) q.set('q', $('#mq').value);
          if ($('#mclass').value) q.set('class', $('#mclass').value);
          if ($('#mrarity').value) q.set('rarity', $('#mrarity').value);
          if ($('#mcur').value) q.set('currency', $('#mcur').value);
          if ($('#mmin').value) q.set('min', $('#mmin').value);
          if ($('#mmax').value) q.set('max', $('#mmax').value);
          if ($('#monline').checked) q.set('online', '1');
          if ($('#mstats').value) q.set('stats', $('#mstats').value);
          let d;
          try { d = await (await fetch('/market?' + q, { cache: 'no-store' })).json(); }
          catch (e) { $('#minfo').textContent = '读取失败'; return; }
          $('#minfo').textContent = d.count + ' 条挂单' + (d.unlisted ? '（' + d.unlisted + ' 件没写备注，不上架）' : '');
          for (const l of d.listings) { cards[l.id] = l.card || []; rarities[l.id] = l.rarity; }
          const have = $('#mcur').value;
          const keep = (id, first, values) => {
            const chosen = $(id).value;
            $(id).innerHTML = '<option value="">' + first + '</option>'
              + (values || []).map(v => '<option' + (v === chosen ? ' selected' : '') + '>' + esc(v) + '</option>').join('');
          };
          keep('#mclass', '全部类别', d.classes);
          keep('#mrarity', '全部稀有度', d.rarities);
          $('#mcur').innerHTML = '<option value="">全部通货</option>'
            + (d.currencies || []).map(c => '<option' + (c === have ? ' selected' : '') + '>' + esc(c) + '</option>').join('');
          $('#mlist').innerHTML = d.listings.length ? d.listings.map(l =>
            '<tr><td class="name">' + esc(l.name) + (l.corrupted ? '（腐化）' : '') + '</td><td>' + esc(l.class) + '</td>'
            + '<td>' + esc(l.rarity) + '</td><td class="num">' + l.ilvl + '</td><td class="num">' + l.stack + '</td>'
            + '<td class="price">' + esc(l.price.text) + '</td>'
            + '<td>' + esc(l.seller) + (l.online ? ' <span class="on">在线</span>' : ' <span class="off">离线</span>') + '</td>'
            + '<td><button class="copy" data-w="' + esc(l.whisper) + '">复制密语</button>'
            + '<button class="card-btn" data-id="' + l.id + '">详情</button>'
            + '<button class="buy" data-seller="' + esc(l.seller) + '" data-league="' + esc(l.league) + '" data-tab="' + l.tab
            + '" data-id="' + l.id + '">购买</button></td></tr>').join('')
            : '<tr><td colspan="8" class="empty">没有符合条件的挂单</td></tr>';
          for (const button of document.querySelectorAll('#mlist .copy'))
            button.addEventListener('click', async () => {
              try { await navigator.clipboard.writeText(button.dataset.w); button.textContent = '已复制'; }
              catch (e) { button.textContent = button.dataset.w.slice(0, 24); }
            });
          for (const button of document.querySelectorAll('#mlist .buy'))
            button.addEventListener('click', () => buy(button));
          for (const button of document.querySelectorAll('#mlist .card-btn'))
            button.addEventListener('click', () => showCard(button.dataset.id));
        }
        async function saveListing() {
          const body = {
            token: adminToken(), account: who(), tab: tab,
            public: $('#mpub').checked, note: $('#mnote').value,
          };
          $('#mres').textContent = '保存中…';
          try {
            const d = await (await api('/market/set', {
              method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
            })).json();
            $('#mres').textContent = d.ok ? ('已保存：' + (d.public ? '公开' : '私有') + (d.note ? ' · ' + d.note : '')) : ('失败：' + d.error);
            if (d.ok) { await loadStash(who()); await loadMarket(); }
          } catch (e) { $('#mres').textContent = '请求失败'; }
        }
        async function buy(button) {
          if (!session) { $('#minfo').textContent = '先登录再购买'; return; }
          button.disabled = true;
          $('#minfo').textContent = '购买中…';
          try {
            const d = await (await api('/buy', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ seller: button.dataset.seller, league: button.dataset.league || null,
                tab: parseInt(button.dataset.tab, 10), itemId: parseInt(button.dataset.id, 10) }),
            })).json();
            const said = d.ok
              ? ('买到了 ' + d.item + '：付 ' + d.price + '，手续费 ' + d.fee + ' 金币，剩 ' + d.goldLeft + ' 金币')
              : ('没买成：' + d.error);
            await loadMarket();
            await loadStash(who());
            $('#minfo').textContent = said;
          } catch (e) { $('#minfo').textContent = '请求失败'; }
          button.disabled = false;
        }
        async function loadExchange() {
          let d;
          try { d = await (await api("/exchange", { cache: "no-store" })).json(); }
          catch (e) { return; }
          const names = d.currencies || [];
          const keep = (id, chosen) => {
            const el = $(id);
            el.innerHTML = names.map(n => "<option" + (n === chosen ? " selected" : "") + ">" + esc(n) + "</option>").join("");
          };
          keep("#xgive", $("#xgive").value || "Chaos Orb");
          keep("#xwant", $("#xwant").value || "Divine Orb");
          const rows = (d.orders || []).map(o =>
            "<tr><td>" + esc(o.account) + "</td><td>" + esc(o.give) + "</td><td class=\"num\">" + o.giveLeft + "</td><td>"
            + esc(o.want) + "</td><td class=\"num\">" + o.wantLeft + "</td><td class=\"dim\">" + esc(o.rate) + "</td>"
            + "<td>" + (me && o.account === me ? "<button class=\"cancel\" data-id=\"" + o.id + "\">撤单</button>" : "") + "</td></tr>").join("");
          $("#xbook").innerHTML = rows
            ? rows + (d.mine && d.mine.length ? "" : "")
            : "<tr><td colspan=\"7\" class=\"empty\">还没有挂单</td></tr>";
          for (const button of document.querySelectorAll("#xbook .cancel"))
            button.addEventListener("click", () => cancelOrder(button.dataset.id));
        }
        async function placeOrder() {
          if (!session) { $("#xres").textContent = "先登录再挂单"; return; }
          $("#xres").textContent = "挂单中…";
          try {
            const d = await (await api("/exchange/order", {
              method: "POST", headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ give: $("#xgive").value, giveAmount: parseInt($("#xgiveAmount").value, 10) || 0,
                want: $("#xwant").value, wantAmount: parseInt($("#xwantAmount").value, 10) || 0 }),
            })).json();
            if (!d.ok) { $("#xres").textContent = "挂单失败：" + d.error; return; }
            const fills = (d.filled || []).map(f => f.gave + " → " + f.got).join("，");
            $("#xres").textContent = "已挂上 #" + d.id + "，还剩 " + d.left.give + " " + d.give
              + (fills ? ("；立刻成交：" + fills) : "；等别人来换");
            await loadExchange();
            await loadStash(who());
          } catch (e) { $("#xres").textContent = "请求失败"; }
        }
        async function cancelOrder(id) {
          $("#xres").textContent = "撤单中…";
          try {
            const d = await (await api("/exchange/cancel", {
              method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ id: parseInt(id, 10) }),
            })).json();
            $("#xres").textContent = d.ok ? ("已撤单，退回 " + d.returned) : ("撤单失败：" + d.error);
            await loadExchange();
            await loadStash(who());
          } catch (e) { $("#xres").textContent = "请求失败"; }
        }
        $("#xplace").addEventListener("click", placeOrder);
        $("#lsignin").addEventListener("click", signIn);
        $('#lsignout').addEventListener('click', signOut);
        $('#mfind').addEventListener('click', loadMarket);
        let statTimer = 0;
        $('#mstats').addEventListener('input', () => {
          clearTimeout(statTimer);
          statTimer = setTimeout(async () => {
            const last = $('#mstats').value.split(';').pop().split('>=')[0].trim();
            if (last.length < 2) return;
            try {
              const d = await (await fetch('/stats?q=' + encodeURIComponent(last), { cache: 'no-store' })).json();
              $('#statlist').innerHTML = (d.stats || []).map(text => '<option value="' + esc(text) + '">').join('');
            } catch (e) { }
          }, 250);
        });
        $('#msave').addEventListener('click', saveListing);
        // ── 自定义装备（the forge）：原版词缀池里挑，发给指定账号 ──
        let forge = { loaded: false, bases: [], mods: {}, pools: {}, chosen: [] };
        const ftoken = () => $('#ftoken').value;
        async function loadForgeCatalog(quiet) {
          if (!quiet) $('#fres').textContent = '载入底子与词缀…';
          let d;
          try { d = await (await fetch('/forge/catalog', { cache: 'no-store' })).json(); }
          catch (e) { $('#fres').textContent = '载入失败：服务器没应答'; return false; }
          if (!d.ok) { $('#fres').textContent = '载入失败：' + d.error; return false; }
          forge.bases = d.bases || [];
          forge.mods = {};
          for (const m of d.mods || []) forge.mods[m.id] = m;
          forge.pools = {};
          for (const p of d.pools || []) forge.pools[p.id] = p.mods || [];
          const classes = [...new Set(forge.bases.map(b => b.class))].sort();
          $('#fclass').innerHTML = '<option value="">全部类别</option>'
            + classes.map(c => '<option>' + esc(c) + '</option>').join('');
          fillBaseList();
          forge.loaded = true;
          if (!quiet) $('#fres').textContent = '底子 ' + forge.bases.length + ' 个，选好底子点「载入词缀」';
          return true;
        }
        function fillBaseList() {
          const want = $('#fclass').value;
          const list = forge.bases.filter(b => !want || b.class === want);
          const keep = $('#fbase').value;
          $('#fbase').innerHTML = '<option value="">选底子</option>'
            + list.map(b => '<option value="' + esc(b.id) + '"' + (b.id === keep ? ' selected' : '') + '>'
              + esc(b.name) + '</option>').join('');
        }
        function currentBase() { return forge.bases.find(b => b.id === $('#fbase').value); }
        function basePoolIds(base) {
          // A base's affixes: its own pool plus whatever its influences open.
          const ids = forge.pools[base.pool] ? [...forge.pools[base.pool]] : [];
          for (const pid of Object.values(base.influence || {}))
            for (const id of forge.pools[pid] || []) if (!ids.includes(id)) ids.push(id);
          return ids;
        }
        function loadAffixes() {
          const base = currentBase();
          if (!base) { $('#fres').textContent = '先选一个底子'; return; }
          const ilvl = parseInt($('#filvl').value, 10) || 84;
          const ids = basePoolIds(base);
          const rows = [];
          for (const id of ids) {
            const mod = forge.mods[id];
            if (!mod || mod.generation !== 1 && mod.generation !== 2) continue;
            if (mod.level > ilvl) continue; // the server refuses it anyway; the list only shows what can roll
            const ranges = (mod.stats || []).map(s => (s.min === s.max ? s.min : s.min + '–' + s.max)).join(' / ');
            rows.push('<div class="row faffix" data-id="' + esc(id) + '" style="margin-top:4px">'
              + '<label style="min-width:290px"><input type="checkbox" class="faffixon"> ' + esc(mod.name || id) + '</label>'
              + '<span class="dim" style="min-width:150px">Lv' + mod.level + ' · ' + (mod.generation === 1 ? '前缀' : '后缀') + '</span>'
              + '<span class="dim" style="min-width:110px">' + esc(ranges) + '</span>'
              + '<span class="fvals"></span></div>');
          }
          $('#faffixrow').style.display = rows.length ? 'flex' : 'none';
          $('#faffixes').innerHTML = rows.length ? rows.join('')
            : '<span class="dim">这个底子在物品等级 ' + ilvl + ' 下没有可出的词缀</span>';
          for (const row of document.querySelectorAll('#faffixes .faffix'))
            row.querySelector('.faffixon').addEventListener('change', () => affixValues(row));
          $('#fres').textContent = '按物品等级 ' + ilvl + ' 挑词缀，勾上可填数值（不填就随机），然后预览或发放';
        }
        // A checked affix grows one input per stat, pre-filled at the range's top; unchecked takes it away again.
        function affixValues(row) {
          const box = row.querySelector('.fvals');
          if (!row.querySelector('.faffixon').checked) { box.innerHTML = ''; return; }
          const mod = forge.mods[row.dataset.id] || { stats: [] };
          box.innerHTML = (mod.stats || []).map(s =>
            '<input class="fvalue" type="number" value="' + (s.min === s.max ? s.min : s.max)
            + '" min="' + s.min + '" max="' + s.max + '" title="' + s.min + '–' + s.max + '，清 0 按随机">').join(' ');
        }
        async function forgePreview() {
          const body = forgeBody(false);
          if (!body) return;
          $('#fres').textContent = '预览中…';
          try {
            const d = await (await fetch('/forge/preview', {
              method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
            })).json();
            if (!d.ok) { $('#fres').textContent = '失败：' + d.error; return; }
            $('#fcardrow').style.display = 'flex';
            $('#fcard').innerHTML = (d.card || []).map(l =>
              '<div class="' + esc(l.kind) + '">' + esc(l.text) + '</div>').join('');
            $('#fres').textContent = '这是会发出去的样子（没填的数值已随机好）';
          } catch (e) { $('#fres').textContent = '请求失败'; }
        }
        function forgeBody(needAccount) {
          const base = currentBase();
          if (!base) { $('#fres').textContent = '先选一个底子'; return null; }
          const mods = [];
          for (const row of document.querySelectorAll('#faffixes .faffix')) {
            if (!row.querySelector('.faffixon').checked) continue;
            const mod = forge.mods[row.dataset.id];
            if (!mod) continue;
            const values = [...row.querySelectorAll('.fvalue')].map(i => parseInt(i.value, 10) || 0);
            mods.push(values.some(v => v !== 0) ? { id: mod.id, values: values } : { id: mod.id });
          }
          const body = {
            token: ftoken(), base: base.id,
            rarity: $('#frarity').value, itemLevel: parseInt($('#filvl').value, 10) || 84,
            quality: parseInt($('#fquality').value, 10) || 0,
            mods: mods,
          };
          if ($('#fsockets').value.trim()) body.sockets = $('#fsockets').value.trim();
          const states = $('#fstates').value.split(',').map(s => s.trim()).filter(s => s);
          if (states.length) body.states = states;
          const account = $('#faccount').value.trim() || who();
          if (needAccount) {
            if (!account) { $('#fres').textContent = '写一个要发放给的账号'; return null; }
            body.account = account;
          }
          return body;
        }
        async function forgeGive() {
          const body = forgeBody(true);
          if (!body) return;
          $('#fres').textContent = '发放中…';
          try {
            const d = await (await fetch('/forge', {
              method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
            })).json();
            if (d.ok) {
              $('#fres').textContent = '已放进「' + d.tabName + '」页：' + d.item;
              await loadStash(body.account);
            } else $('#fres').textContent = '失败：' + d.error;
          } catch (e) { $('#fres').textContent = '请求失败'; }
        }
        $('#fclass').addEventListener('change', fillBaseList);
        $('#fload').addEventListener('click', () => { (forge.loaded ? Promise.resolve() : loadForgeCatalog(false)).then(loadAffixes); });
        $('#fpreview').addEventListener('click', forgePreview);
        $('#fforge').addEventListener('click', forgeGive);
        // ── end of the forge ──
        for (const id of ['mq', 'mcur', 'mmin', 'mmax', 'monline', 'mstats', 'mclass', 'mrarity']) $('#' + id).addEventListener('change', loadMarket);
        $('#ggive').addEventListener('click', async () => {
          const body = {
            token: $('#gtoken').value, account: who(), item: $('#gitem').value,
            count: parseInt($('#gcount').value, 10) || 1, level: 1,
          };
          $('#gres').textContent = '发放中…';
          try {
            const d = await (await fetch('/grant', {
              method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
            })).json();
            if (d.ok) {
              const total = (d.placed || []).reduce((n, p) => n + (p.moved || p.stack || 1), 0);
              $('#gres').textContent = '已放进「' + d.tabName + '」页：' + d.item + ' ×' + total;
              await loadStash(who());
            } else $('#gres').textContent = '失败：' + d.error;
          } catch (e) { $('#gres').textContent = '请求失败'; }
        });
        async function tick() {
          try {
            const r = await fetch('/state', { cache: 'no-store' });
            render(await r.json());
            $('#status').textContent = '已连接';
          } catch (e) {
            $('#status').textContent = '连接断开，正在重试…';
          }
        }
        tick();
        setInterval(tick, 1000);
        loadMe().then(loadAccounts).then(loadMarket).then(loadExchange);
        setInterval(loadMarket, 5000);
        setInterval(loadExchange, 5000);
        </script>
        </body>
        </html>
        """;
}
