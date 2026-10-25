using System;
using System.Collections.Generic;
using MiSideCoop.Net.Steam;
using UnityEngine;

namespace MiSideCoop.Net;

// Steam P2P through Valve's relays (ISteamNetworkingMessages): no IP, no ports.
// The host advertises itself in rich presence; the guest sees it in its friends list and
// sends a hello. Without the game's callback dispatcher there is no session-request
// callback, so the host accepts sessions by polling its friends who play MiSide.
internal sealed class SteamTransport : ITransport
{
    public const string PresenceKey = "misidecoop";

    private readonly ISteamApi api;
    private readonly List<(ulong, byte[])> inbox = new();
    private readonly HashSet<ulong> acceptedFriends = new();
    private float nextAcceptPoll;

    public bool IsHost { get; }
    public string Describe { get; }
    public ulong HostPeer { get; }
    public event Action<ulong> PeerLost { add { } remove { } }

    private SteamTransport(ISteamApi api, bool host, ulong hostPeer, string describe)
    {
        this.api = api;
        IsHost = host;
        HostPeer = hostPeer;
        Describe = describe;
    }

    public static SteamTransport Host(ISteamApi api)
    {
        api.SetRichPresence(PresenceKey, "host:" + Protocol.Version);
        api.SetRichPresence("status", "MiSide Co-op: ждёт друга");
        api.SetRichPresence("connect", "+coop_join " + api.MyId);
        return new SteamTransport(api, true, 0, "steam host");
    }

    public static SteamTransport Join(ISteamApi api, ulong host)
    {
        api.AcceptSession(host);
        api.SetRichPresence("status", "MiSide Co-op");
        return new SteamTransport(api, false, host, $"steam {api.FriendName(host)}");
    }

    // Friends whose game advertises a co-op host, for the join menu.
    public static List<(ulong id, string name)> FindHosts(ISteamApi api)
    {
        var hosts = new List<(ulong, string)>();
        foreach (var f in api.FriendsInGame())
            if (api.FriendRichPresence(f, PresenceKey).StartsWith("host:", StringComparison.Ordinal))
                hosts.Add((f, api.FriendName(f)));
        return hosts;
    }

    public void Send(ulong peer, ReadOnlySpan<byte> data, bool reliable)
    {
        if (!api.Send(peer, data, reliable) && reliable)
            CoopLog.Warn($"steam: send to {peer} failed");
    }

    public void Poll(ReceiveHandler onData)
    {
        api.Tick();
        if (IsHost && Time.unscaledTime >= nextAcceptPoll)
        {
            nextAcceptPoll = Time.unscaledTime + 1f;
            foreach (var f in api.FriendsInGame())
                if (api.AcceptSession(f) && acceptedFriends.Add(f))
                    CoopLog.Info($"steam: session with {api.FriendName(f)} ({f})");
        }
        inbox.Clear();
        api.Receive(inbox);
        foreach (var (from, data) in inbox) onData(from, data);
    }

    public void Dispose()
    {
        foreach (var f in acceptedFriends) api.CloseSession(f);
        if (HostPeer != 0) api.CloseSession(HostPeer);
        api.ClearRichPresence();
    }
}
