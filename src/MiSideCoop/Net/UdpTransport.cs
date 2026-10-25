using System;
using System.Collections.Generic;
using LiteNetLib;

namespace MiSideCoop.Net;

// Direct IP over UDP (LiteNetLib). Used for LAN or VPN play and for tests on one PC.
internal sealed class UdpTransport : ITransport
{
    private const string Key = "miside-coop";

    private readonly EventBasedNetListener listener = new();
    private readonly NetManager manager;
    private readonly Dictionary<ulong, NetPeer> peers = new();
    private ReceiveHandler handler;

    public bool IsHost { get; }
    public string Describe { get; }
    public ulong HostPeer { get; private set; }
    public event Action<ulong> PeerLost;

    private UdpTransport(bool host, string describe)
    {
        IsHost = host;
        Describe = describe;
        manager = new NetManager(listener) { AutoRecycle = true, DisconnectTimeout = 10000 };
        listener.ConnectionRequestEvent += req =>
        {
            if (IsHost && peers.Count < 1) req.AcceptIfKey(Key);
            else req.Reject();
        };
        listener.PeerConnectedEvent += p =>
        {
            peers[Id(p)] = p;
            if (!IsHost) HostPeer = Id(p);
        };
        listener.PeerDisconnectedEvent += (p, info) =>
        {
            peers.Remove(Id(p));
            if (!IsHost && HostPeer == Id(p)) HostPeer = 0;
            CoopLog.Info($"udp: peer {p} left ({info.Reason})");
            PeerLost?.Invoke(Id(p));
        };
        listener.NetworkReceiveEvent += (p, reader, channel, method) =>
            handler?.Invoke(Id(p), reader.GetRemainingBytesSpan());
    }

    public static UdpTransport Host(int port)
    {
        var t = new UdpTransport(true, $"udp host :{port}");
        if (!t.manager.Start(port)) throw new InvalidOperationException($"UDP port {port} is busy");
        return t;
    }

    public static UdpTransport Join(string address, int port)
    {
        var t = new UdpTransport(false, $"udp {address}:{port}");
        t.manager.Start();
        t.manager.Connect(address, port, Key);
        return t;
    }

    // NetPeer ids start at 0; keep 0 meaning "nobody".
    private static ulong Id(NetPeer p) => (ulong)p.Id + 1;

    public void Send(ulong peer, ReadOnlySpan<byte> data, bool reliable)
    {
        if (peers.TryGetValue(peer, out var p))
            p.Send(data, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
    }

    public void Poll(ReceiveHandler onData)
    {
        handler = onData;
        manager.PollEvents();
        handler = null;
    }

    public void Dispose() => manager.Stop();
}
