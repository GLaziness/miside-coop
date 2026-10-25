using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MiSideCoop.Game;
using MiSideCoop.Net;
using MiSideCoop.Net.Steam;
using UnityEngine;

namespace MiSideCoop;

// Owns the co-op session and what it drives in the game: snapshots out, the puppet in, and
// the guest following the host's scene.
internal static class CoopNet
{
    private const float SendInterval = 0.05f; // 20 snapshots per second

    private static readonly PlayerSampler sampler = new();
    private static ISteamApi steam;
    private static float nextSteamTry;
    private static string lastSteamProblem;
    private static float nextSend;
    private static float nextPuppetTry;
    private static GUIStyle tagStyle;
    private static Puppet puppet;
    private static bool autoStarted;

    public static Session Session { get; private set; }
    public static Puppet Puppet => puppet;

    public static ISteamApi Steam
    {
        get
        {
            if (steam != null || Time.unscaledTime < nextSteamTry) return steam;
            nextSteamTry = Time.unscaledTime + 3f;
            try
            {
                if (LaunchArgs.MockSteam > 0)
                    steam = new MockSteamApi(LaunchArgs.MockSteam, LaunchArgs.MockSteamDir ?? DefaultMockDir(), CoopConfig.PlayerName);
                else
                {
                    var real = new RealSteamApi();
                    if (real.Available) steam = real;
                    else if (real.Problem != lastSteamProblem) CoopLog.Info("steam: unavailable: " + (lastSteamProblem = real.Problem));
                }
                if (steam != null) CoopLog.Info($"steam: ready as {steam.MyName} ({steam.MyId})");
            }
            catch (Exception e)
            {
                CoopLog.Warn("steam: unavailable: " + e.Message);
            }
            return steam;
        }
    }

    private static string DefaultMockDir() =>
        Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "..", "run", "mocksteam"));

    public static string LocalName
    {
        get
        {
            var n = CoopConfig.PlayerName;
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            var s = steam?.MyName;
            return string.IsNullOrEmpty(s) ? "Игрок" : s;
        }
    }

    public static string HostUdp(int port) => Start(() => UdpTransport.Host(port));
    public static string JoinUdp(string address, int port) => Start(() => UdpTransport.Join(address, port));

    public static string HostSteam()
    {
        var api = Steam;
        if (api == null) return "Steam недоступен";
        return Start(() => SteamTransport.Host(api));
    }

    public static string JoinSteam(ulong host)
    {
        var api = Steam;
        if (api == null) return "Steam недоступен";
        return Start(() => SteamTransport.Join(api, host));
    }

    private static string Start(Func<ITransport> make)
    {
        Stop();
        try
        {
            var t = make();
            Session = new Session(t, LocalName);
            Session.HostSceneAnnounced += (scene, epoch) =>
            {
                SceneFollower.OnHostScene(scene, epoch);
                Story.StorySync.OnHostEpoch(epoch);
            };
            Session.StoryEventReceived += Story.StorySync.OnStoryEvent;
            Session.StoryFromRequested += Story.StorySync.OnStoryFrom;
            Session.Left += _ => DestroyPuppet();
            CoopLog.Info($"session: started ({t.Describe}) as {LocalName}");
            CoopRuntime.Bridge?.PushEvent("net.started", new JsonObject { ["transport"] = t.Describe });
            return "ok: " + t.Describe;
        }
        catch (Exception e)
        {
            CoopLog.Error("session: start failed: " + e.Message);
            return "ошибка: " + e.Message;
        }
    }

    public static void Stop()
    {
        if (Session == null) return;
        Session.Dispose();
        Session = null;
        SceneFollower.Reset();
        Regroup.Reset();
        Story.StorySync.ResetSession();
        DestroyPuppet();
        CoopLog.Info("session: stopped");
    }

    public static List<(ulong id, string name)> FindSteamHosts()
    {
        var api = Steam;
        return api == null ? new List<(ulong, string)>() : SteamTransport.FindHosts(api);
    }

    public static void Update(string scene)
    {
        AutoStart(scene);
        if (Session == null) return;
        bool wasConnected = Session.Connected;
        Session.Update(scene, Story.StorySync.Epoch);
        if (!Session.IsHost && Session.Connected)
        {
            SceneFollower.Update();
            Regroup.Update(Session, scene);
        }
        if (wasConnected && !Session.Connected) DestroyPuppet();
    }

    // After the animation and IK of this frame: send our pose, pose the other player.
    public static void LateUpdate(string scene)
    {
        if (Session == null || !Session.Connected) return;
        float now = Time.unscaledTime;
        if (now >= nextSend)
        {
            nextSend = now + SendInterval;
            Session.SendPlayerState(sampler.Sample(scene));
        }
        UpdatePuppet(scene, now);
    }

    private static void UpdatePuppet(string scene, float now)
    {
        var remote = Session.LastRemote;
        bool sameScene = remote != null && remote.HasBody && remote.Scene == scene;
        if (!sameScene)
        {
            Session.RemoteStates.Clear();
            if (puppet != null && remote != null && remote.Scene != scene) DestroyPuppet();
            return;
        }
        if (puppet != null && (!puppet.Alive || puppet.Scene != scene)) DestroyPuppet();
        if (puppet == null)
        {
            if (now < nextPuppetTry) return;
            var template = GameAccess.Player;
            if (template == null) return; // no body here to copy yet
            nextPuppetTry = now + 2f; // a failed attempt is not repeated every frame
            puppet = Puppet.Create(template, Session.PeerName, scene);
            if (puppet == null) return;
            CoopRuntime.Bridge?.PushEvent("puppet.created", new JsonObject { ["scene"] = scene });
        }
        while (Session.RemoteStates.Count > 0) puppet.Push(Session.RemoteStates.Dequeue());
        puppet.Name = Session.PeerName;
        puppet.Update(now);
        puppet.UpdateVisibility(Camera.main);
    }

    // The other player's name over their head. Drawn by IMGUI: the game build has no TextMesh.
    public static void OnGUI()
    {
        if (puppet == null || !puppet.Alive || !puppet.Shown) return;
        var cam = Camera.main;
        if (cam == null) return;
        var p = cam.WorldToScreenPoint(puppet.TagPosition);
        if (p.z < 0.2f || p.z > 40f) return;
        tagStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
        var r = new Rect(p.x - 150, Screen.height - p.y - 14, 300, 28);
        tagStyle.normal.textColor = new Color(0, 0, 0, 0.8f);
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), puppet.Name, tagStyle);
        tagStyle.normal.textColor = new Color(1f, 0.85f, 0.95f);
        GUI.Label(r, puppet.Name, tagStyle);
    }

    private static void DestroyPuppet()
    {
        puppet?.Destroy();
        puppet = null;
        nextPuppetTry = 0;
    }

    public static void OnSceneChanged()
    {
        sampler.Forget();
        DestroyPuppet();
    }

    // Test runs and Steam's "Join game" start a session from the command line.
    private static void AutoStart(string scene)
    {
        if (autoStarted) return;
        if (LaunchArgs.Host == null && LaunchArgs.Join == null && LaunchArgs.SteamJoin == 0) { autoStarted = true; return; }
        if (scene == "SceneAihasto") return; // let the game boot first
        if ((LaunchArgs.Host?.StartsWith("steam") == true || LaunchArgs.Join?.StartsWith("steam") == true ||
             LaunchArgs.SteamJoin != 0) && Steam == null) return;
        autoStarted = true;
        string result;
        if (LaunchArgs.SteamJoin != 0) result = JoinSteam(LaunchArgs.SteamJoin);
        else if (LaunchArgs.Host != null) result = StartFromSpec(LaunchArgs.Host, host: true);
        else result = StartFromSpec(LaunchArgs.Join, host: false);
        CoopLog.Info("autostart: " + result);
    }

    // "udp:27015", "udp:127.0.0.1:27015", "steam", "steam:<id>", "steam:first"
    public static string StartFromSpec(string spec, bool host)
    {
        var parts = spec.Split(':');
        switch (parts[0])
        {
            case "udp" when host:
                return HostUdp(parts.Length > 1 ? int.Parse(parts[1]) : CoopConfig.DirectPort);
            case "udp":
                return JoinUdp(parts[1], parts.Length > 2 ? int.Parse(parts[2]) : CoopConfig.DirectPort);
            case "steam" when host:
                return HostSteam();
            case "steam":
            {
                ulong id = 0;
                if (parts.Length > 1 && parts[1] != "first") ulong.TryParse(parts[1], out id);
                else
                {
                    var hosts = FindSteamHosts();
                    if (hosts.Count == 0) { autoStarted = false; return "нет хоста среди друзей, повторю"; }
                    id = hosts[0].id;
                }
                return JoinSteam(id);
            }
            default:
                return "unknown spec " + spec;
        }
    }

    // ---- bridge ----

    public static bool TryExec(string cmd, string rest, out (bool, string) result)
    {
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (cmd)
        {
            case "net.host":
                result = (true, StartFromSpec(string.Join(":", a.DefaultIfEmpty("steam")), host: true));
                return true;
            case "net.join":
                result = (true, StartFromSpec(string.Join(":", a), host: false));
                return true;
            case "net.stop":
                Stop();
                result = (true, "stopped");
                return true;
            case "steam.hosts":
                result = (true, string.Join("\n", FindSteamHosts().Select(h => $"{h.id} {h.name}")));
                return true;
        }
        result = default;
        return false;
    }

    public static bool TryEval(string expr, out JsonNode value)
    {
        var s = Session;
        switch (expr)
        {
            case "net.state": value = s == null ? "idle" : s.State.ToString().ToLowerInvariant(); return true;
            case "net.connected": value = s?.Connected ?? false; return true;
            case "net.peer": value = s?.PeerName; return true;
            case "net.rtt": value = (double)(s?.RttMs ?? -1); return true;
            case "net.sent": value = s?.StatesSent ?? 0; return true;
            case "net.received": value = s?.StatesReceived ?? 0; return true;
            case "remote.scene": value = s?.LastRemote?.Scene; return true;
            case "remote.pos": value = s?.LastRemote is { HasBody: true } r ? Bridge.GameCommands.Fmt(r.RootPos) : null; return true;
            case "puppet.exists": value = puppet?.Alive ?? false; return true;
            case "puppet.pos": value = puppet?.Alive == true ? Bridge.GameCommands.Fmt(puppet.Position) : null; return true;
            case "puppet.error":
            {
                // How far the puppet stands from where the other player says they are.
                var r2 = s?.LastRemote;
                value = puppet?.Alive == true && r2 is { HasBody: true } ? (double)Vector3.Distance(puppet.Position, r2.RootPos) : -1.0;
                return true;
            }
            case "follow.target": value = SceneFollower.Target; return true;
            case "regroup.count": value = Regroup.Teleports; return true;
            case "steam.id": value = Steam?.MyId.ToString(); return true;
            case "steam.problem": value = Steam == null ? lastSteamProblem : null; return true;
            case "steam.friends":
                value = Steam is RealSteamApi real && real.FriendStats() is var (n, g, m, me)
                    ? $"friends {n}, in a game {g}, in MiSide {m}; me in MiSide {me}" : null;
                return true;
        }
        value = null;
        return false;
    }

    public static void AddState(JsonObject state)
    {
        var s = Session;
        state["net"] = s == null ? new JsonObject { ["state"] = "idle" } : new JsonObject
        {
            ["state"] = s.State.ToString().ToLowerInvariant(),
            ["host"] = s.IsHost,
            ["transport"] = s.Describe,
            ["peer"] = s.PeerName,
            ["rttMs"] = (double)s.RttMs,
            ["sent"] = s.StatesSent,
            ["received"] = s.StatesReceived,
            ["remoteScene"] = s.LastRemote?.Scene,
            ["remotePos"] = s.LastRemote is { HasBody: true } r ? Bridge.GameCommands.Fmt(r.RootPos) : null,
            ["puppet"] = puppet?.Alive == true ? Bridge.GameCommands.Fmt(puppet.Position) : null,
        };
    }
}
