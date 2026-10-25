using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace MiSideCoop.Bridge;

// In-game RPC server, protocol v1 of coop-anything's tools/bridge (PROTOCOL.md there), so the
// hub's bridge_client.py drives this game. Loopback only, and only when a test run passes
// -coop-bridge: it can run commands inside the game.
internal sealed class BridgeServer
{
    public const int Protocol = 1;
    private const int DefaultTimeoutMs = 30000;
    private const int SlackMs = 2000;
    private const int MaxCallsPerPump = 16;
    private const int MaxPending = 256;
    private const int EventRing = 1024;

    public delegate JsonNode Handler(JsonObject prms);

    private sealed class Call
    {
        public string Method;
        public JsonObject Params;
        public volatile bool Abandoned;
        public JsonNode Result;
        public (int code, string message)? Error;
        public readonly ManualResetEventSlim Done = new(false);
        // waitFor state
        public long NextPollMs;
        public long StartedMs;
        public JsonNode LastValue;
        public string LastError;
    }

    private readonly string game;
    private readonly string build;
    private readonly Dictionary<string, Handler> mainHandlers = new();
    private readonly Dictionary<string, Handler> directHandlers = new();
    private readonly ConcurrentQueue<Call> queue = new();
    private readonly List<Call> waits = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object eventLock = new();
    private readonly LinkedList<JsonObject> events = new();
    private long eventSeq;
    private long frames;
    private long lastPumpMs = -1;
    private int pending;
    private TcpListener listener;
    private volatile bool running;

    // Adapter: what "waitFor" evaluates. Set by the owner before Start.
    public Func<string, JsonNode> Eval;
    public Func<string, (bool ok, string output)> Exec;
    public Func<int> LoadedCount = () => 0;

    public int Port { get; private set; }

    public BridgeServer(string game, string build)
    {
        this.game = game;
        this.build = build;
    }

    public void Register(string method, Handler handler, bool direct = false)
    {
        (direct ? directHandlers : mainHandlers)[method] = handler;
    }

    public bool Start(int port, string endpointFile)
    {
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "coop-bridge" }.Start();
            if (!string.IsNullOrEmpty(endpointFile))
            {
                var info = new JsonObject
                {
                    ["protocol"] = Protocol,
                    ["game"] = game,
                    ["pid"] = Environment.ProcessId,
                    ["endpoints"] = new JsonArray($"tcp://127.0.0.1:{Port}"),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(endpointFile)));
                File.WriteAllText(endpointFile + ".tmp", info.ToJsonString());
                File.Move(endpointFile + ".tmp", endpointFile, true);
            }
            return true;
        }
        catch (Exception e)
        {
            CoopLog.Error("bridge: " + e.Message);
            return false;
        }
    }

    public void Stop()
    {
        running = false;
        try { listener?.Stop(); } catch { }
    }

    public void PushEvent(string type, JsonObject data = null)
    {
        lock (eventLock)
        {
            eventSeq++;
            events.AddLast(new JsonObject
            {
                ["seq"] = eventSeq,
                ["tMs"] = clock.ElapsedMilliseconds,
                ["type"] = type,
                ["data"] = data ?? new JsonObject(),
            });
            while (events.Count > EventRing) events.RemoveFirst();
        }
    }

    // Main thread, once per frame.
    public void Pump()
    {
        frames++;
        long now = clock.ElapsedMilliseconds;
        lastPumpMs = now;
        for (int n = 0; n < MaxCallsPerPump && queue.TryDequeue(out var call); n++)
        {
            Interlocked.Decrement(ref pending);
            if (call.Abandoned) continue;
            if (call.Method == "waitFor")
            {
                call.StartedMs = now;
                waits.Add(call);
                continue;
            }
            Run(call);
        }
        for (int i = waits.Count - 1; i >= 0; i--)
        {
            var w = waits[i];
            if (w.Abandoned) { waits.RemoveAt(i); continue; }
            if (now < w.NextPollMs) continue;
            if (PollWait(w, now)) { waits.RemoveAt(i); w.Done.Set(); }
        }
    }

    private void Run(Call call)
    {
        try
        {
            call.Result = mainHandlers[call.Method](call.Params);
        }
        catch (BridgeError e) { call.Error = (e.Code, e.Message); }
        catch (Exception e) { call.Error = (-32000, e.GetType().Name + ": " + e.Message); }
        call.Done.Set();
    }

    private bool PollWait(Call w, long now)
    {
        var p = w.Params;
        int timeoutMs = Int(p, "timeoutMs", 10000);
        int pollMs = Math.Max(10, Int(p, "pollMs", 100));
        w.NextPollMs = now + pollMs;
        string op = Str(p, "op") ?? "truthy";
        double target = p["value"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        string contains = Str(p, "contains");
        bool satisfied = false;
        try
        {
            JsonNode value;
            if (Str(p, "expr") is string expr) value = Eval(expr);
            else if (Str(p, "cmd") is string cmd) value = Exec(cmd).output;
            else throw new BridgeError(-32602, "waitFor needs 'expr' or 'cmd'");
            w.LastValue = Clone(value);
            satisfied = Satisfies(value, op, target, contains, p.ContainsKey("cmd"));
        }
        catch (BridgeError e) when (e.Code == -32602)
        {
            w.Error = (e.Code, e.Message);
            return true;
        }
        catch (Exception e) { w.LastError = e.Message; }

        bool timedOut = !satisfied && now - w.StartedMs >= timeoutMs;
        if (!satisfied && !timedOut) return false;
        var result = new JsonObject
        {
            ["satisfied"] = satisfied,
            ["timedOut"] = timedOut,
            ["value"] = Clone(w.LastValue),
            ["elapsedMs"] = now - w.StartedMs,
        };
        if (timedOut && w.LastError != null) result["lastError"] = w.LastError;
        w.Result = result;
        return true;
    }

    private static bool Satisfies(JsonNode value, string op, double target, string contains, bool fromCmd)
    {
        if (contains != null) return value?.ToString().Contains(contains) ?? false;
        // JsonValues built from CLR ints do not convert to double, so go through the JSON text.
        string json = value?.ToJsonString();
        double x;
        if (json == "true") x = 1;
        else if (json == "false") x = 0;
        else if (double.TryParse(json, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var dn)) x = dn;
        else if (value is JsonValue js && js.TryGetValue<string>(out var s))
        {
            if (fromCmd && TryFirstNumber(s, out var fn)) x = fn;
            else return op == "truthy" && s.Length > 0;
        }
        else return false;
        return op switch
        {
            "truthy" => x != 0,
            "==" => x == target,
            "!=" => x != target,
            "<" => x < target,
            "<=" => x <= target,
            ">" => x > target,
            ">=" => x >= target,
            _ => false,
        };
    }

    private static bool TryFirstNumber(string s, out double d)
    {
        d = 0;
        int start = -1;
        for (int i = 0; i < s.Length; i++)
        {
            bool numChar = char.IsDigit(s[i]) || (s[i] == '-' && start < 0) || (s[i] == '.' && start >= 0);
            if (numChar && start < 0) start = i;
            else if (!numChar && start >= 0)
                return double.TryParse(s.AsSpan(start, i - start), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d);
        }
        return start >= 0 && double.TryParse(s.AsSpan(start), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out d);
    }

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { if (!running) return; continue; }
            new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "coop-bridge-client" }.Start();
        }
    }

    private void ClientLoop(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                string line;
                while (running && (line = reader.ReadLine()) != null)
                {
                    line = line.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (line.Length > 4 * 1024 * 1024)
                    {
                        writer.WriteLine(ErrorReply(null, -32600, "line too long").ToJsonString());
                        return;
                    }
                    writer.WriteLine(Handle(line).ToJsonString());
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private JsonObject Handle(string line)
    {
        JsonNode req;
        try { req = JsonNode.Parse(line); }
        catch { return ErrorReply(null, -32700, "parse error"); }
        if (req is not JsonObject obj) return ErrorReply(null, -32600, "request must be an object");
        JsonNode id = Clone(obj["id"]);
        if (id != null && !(id is JsonValue idv && (idv.TryGetValue<double>(out _) || idv.TryGetValue<string>(out _))))
            return ErrorReply(null, -32600, "bad id");
        if (obj["method"] is not JsonValue mv || !mv.TryGetValue<string>(out var method))
            return ErrorReply(id, -32600, "method missing");
        var prms = obj["params"] as JsonObject ?? new JsonObject();

        try
        {
            switch (method)
            {
                case "ping": return Reply(id, PingResult());
                case "version":
                    return Reply(id, new JsonObject
                    {
                        ["protocol"] = Protocol, ["game"] = game, ["build"] = build,
                        ["server"] = "miside-coop-cs 1", ["pid"] = Environment.ProcessId,
                    });
                case "methods": return Reply(id, new JsonObject { ["methods"] = MethodList() });
                case "events": return Reply(id, EventsResult(prms));
            }
            if (directHandlers.TryGetValue(method, out var direct)) return Reply(id, direct(prms));
            if (!mainHandlers.ContainsKey(method) && method != "waitFor")
                return ErrorReply(id, -32601, "method not found: " + method);
        }
        catch (BridgeError e) { return ErrorReply(id, e.Code, e.Message); }
        catch (Exception e) { return ErrorReply(id, -32000, e.Message); }

        if (Interlocked.Increment(ref pending) > MaxPending)
        {
            Interlocked.Decrement(ref pending);
            return ErrorReply(id, -32003, "busy");
        }
        int timeoutMs = Int(prms, "timeoutMs", DefaultTimeoutMs);
        var call = new Call { Method = method, Params = prms };
        queue.Enqueue(call);
        if (!call.Done.Wait(timeoutMs + SlackMs))
        {
            call.Abandoned = true;
            return ErrorReply(id, -32001, "main thread timeout");
        }
        if (call.Error is { } err) return ErrorReply(id, err.code, err.message);
        return Reply(id, call.Result);
    }

    private JsonObject PingResult()
    {
        long last = lastPumpMs;
        return new JsonObject
        {
            ["pong"] = true,
            ["protocol"] = Protocol,
            ["game"] = game,
            ["pid"] = Environment.ProcessId,
            ["frames"] = Interlocked.Read(ref frames),
            ["mainLoopAgeMs"] = last < 0 ? -1 : clock.ElapsedMilliseconds - last,
            ["ready"] = last >= 0,
            ["loadedCount"] = LoadedCount(),
        };
    }

    private JsonArray MethodList()
    {
        var list = new JsonArray("ping", "version", "methods", "events", "waitFor");
        foreach (var k in mainHandlers.Keys) list.Add(k);
        foreach (var k in directHandlers.Keys) list.Add(k);
        return list;
    }

    private JsonObject EventsResult(JsonObject prms)
    {
        long since = (long)(prms["since"] is JsonValue s && s.TryGetValue<double>(out var sv) ? sv : 0);
        int limit = Int(prms, "limit", 0);
        if (limit <= 0) limit = 1000;
        var list = new JsonArray();
        long next = since, dropped = 0, last;
        lock (eventLock)
        {
            last = eventSeq;
            long oldest = events.First?.Value["seq"]!.GetValue<long>() ?? last + 1;
            if (since + 1 < oldest) dropped = oldest - since - 1;
            foreach (var e in events)
            {
                long seq = e["seq"]!.GetValue<long>();
                if (seq <= since) continue;
                if (list.Count >= limit) break;
                list.Add(Clone(e));
                next = seq;
            }
        }
        return new JsonObject { ["events"] = list, ["next"] = next, ["dropped"] = dropped, ["last"] = last };
    }

    // System.Text.Json in .NET 6 has no DeepClone, and a node can only have one parent.
    private static JsonNode Clone(JsonNode n) => n == null ? null : JsonNode.Parse(n.ToJsonString());

    private static JsonObject Reply(JsonNode id, JsonNode result) =>
        new() { ["id"] = id, ["result"] = result };

    private static JsonObject ErrorReply(JsonNode id, int code, string message) =>
        new() { ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    public static string Str(JsonObject p, string key) =>
        p[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int Int(JsonObject p, string key, int fallback) =>
        p[key] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : fallback;
}

internal sealed class BridgeError : Exception
{
    public int Code { get; }
    public BridgeError(int code, string message) : base(message) => Code = code;
}
