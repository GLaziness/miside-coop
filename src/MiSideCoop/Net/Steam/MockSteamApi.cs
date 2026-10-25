using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiSideCoop.Net.Steam;

// Two game copies on one PC cannot be two Steam users, so tests swap in this mock
// (-coop-mocksteam <n>). Each copy is "friend" 7656119000000000n, has a loopback UDP port,
// and publishes its name and rich presence in a small JSON file the others read. It keeps the
// Steam rule the transport relies on: messages from a user nobody accepted wait until
// AcceptSession, and sending to a user accepts them.
internal sealed class MockSteamApi : ISteamApi
{
    private const ulong Base = 76561190000000000UL;
    private const int BasePort = 47100;

    private readonly string dir;
    private readonly int index;
    private readonly UdpClient udp;
    private readonly Dictionary<string, string> presence = new();
    private readonly HashSet<ulong> accepted = new();
    private readonly Dictionary<ulong, List<byte[]>> pending = new();
    private readonly List<(ulong, byte[])> ready = new();
    private DateTime nextPublish;

    public bool Available => true;
    public ulong MyId { get; }
    public string MyName { get; }

    public MockSteamApi(int index, string dir, string name)
    {
        this.index = index;
        this.dir = dir;
        MyId = Base + (ulong)index;
        MyName = string.IsNullOrEmpty(name) ? "Mock" + index : name;
        Directory.CreateDirectory(dir);
        udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, BasePort + index));
        Publish();
    }

    private static int PortOf(ulong id) => BasePort + (int)(id - Base);

    public bool Send(ulong to, ReadOnlySpan<byte> data, bool reliable)
    {
        accepted.Add(to);
        var packet = new byte[8 + data.Length];
        BitConverter.TryWriteBytes(packet.AsSpan(0, 8), MyId);
        data.CopyTo(packet.AsSpan(8));
        udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, PortOf(to)));
        return true;
    }

    public void Receive(List<(ulong from, byte[] data)> into)
    {
        into.AddRange(ready);
        ready.Clear();
        while (udp.Available > 0)
        {
            IPEndPoint ep = null;
            byte[] packet;
            try { packet = udp.Receive(ref ep); }
            catch (SocketException) { continue; } // ICMP port unreachable from a closed peer
            if (packet.Length < 8) continue;
            ulong from = BitConverter.ToUInt64(packet, 0);
            var payload = packet.AsSpan(8).ToArray();
            if (accepted.Contains(from)) into.Add((from, payload));
            else
            {
                if (!pending.TryGetValue(from, out var q)) pending[from] = q = new List<byte[]>();
                if (q.Count < 256) q.Add(payload);
            }
        }
    }

    public bool AcceptSession(ulong user)
    {
        if (pending.Remove(user, out var q))
        {
            accepted.Add(user);
            foreach (var p in q) ready.Add((user, p));
            return true;
        }
        return accepted.Contains(user);
    }

    public void CloseSession(ulong user)
    {
        accepted.Remove(user);
        pending.Remove(user);
    }

    public void SetRichPresence(string key, string value)
    {
        presence[key] = value;
        Publish();
    }

    public void ClearRichPresence()
    {
        presence.Clear();
        Publish();
    }

    public List<ulong> FriendsInGame()
    {
        var list = new List<ulong>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var info = ReadFriend(f);
            if (info == null) continue;
            ulong id = info["id"]!.GetValue<ulong>();
            if (id == MyId) continue;
            if (DateTime.UtcNow.Ticks - info["ticks"]!.GetValue<long>() > TimeSpan.FromSeconds(5).Ticks) continue;
            list.Add(id);
        }
        return list;
    }

    public string FriendName(ulong user) => ReadFriend(FileOf(user))?["name"]?.GetValue<string>() ?? "";

    public string FriendRichPresence(ulong user, string key) =>
        ReadFriend(FileOf(user))?["presence"]?[key]?.GetValue<string>() ?? "";

    public void Tick()
    {
        if (DateTime.UtcNow >= nextPublish) Publish();
    }

    private string FileOf(ulong id) => Path.Combine(dir, $"{id - Base}.json");

    private static JsonNode ReadFriend(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private void Publish()
    {
        nextPublish = DateTime.UtcNow.AddSeconds(1);
        var pres = new JsonObject();
        foreach (var kv in presence) pres[kv.Key] = kv.Value;
        var doc = new JsonObject
        {
            ["id"] = MyId, ["name"] = MyName, ["ticks"] = DateTime.UtcNow.Ticks, ["presence"] = pres,
        };
        var path = FileOf(MyId);
        try
        {
            File.WriteAllText(path + ".tmp", doc.ToJsonString());
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        try { File.Delete(FileOf(MyId)); } catch (IOException) { }
        udp.Dispose();
    }
}
