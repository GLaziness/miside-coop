using System;

namespace MiSideCoop.Net;

// A connection to the other player. Peers are numbers: a SteamID for Steam, a local id for UDP.
internal interface ITransport : IDisposable
{
    bool IsHost { get; }
    string Describe { get; }

    // Guest: the host's peer id once it is known (0 before). Host: 0.
    ulong HostPeer { get; }

    void Send(ulong peer, ReadOnlySpan<byte> data, bool reliable);

    // Reads everything that arrived since the last call. Main thread, once per frame.
    void Poll(ReceiveHandler onData);

    // Transport-level hint that a peer went away (UDP disconnect). Steam has none: the session times out.
    event Action<ulong> PeerLost;
}

internal delegate void ReceiveHandler(ulong peer, ReadOnlySpan<byte> data);
