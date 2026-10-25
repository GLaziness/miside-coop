using System;
using System.Collections.Generic;
using UnityEngine;

namespace MiSideCoop.Net;

// The co-op link between the two players on top of a transport: hello and welcome, keep-alive,
// time-outs, the host's scene announcements, story events and the player snapshots both ways.
internal sealed class Session : IDisposable
{
    public enum Phase { Connecting, Connected, Closed }

    private const float HelloEvery = 1f;
    private const float PingEvery = 1f;
    private const float TimeoutAfter = 15f;

    private readonly ITransport transport;
    private readonly string localName;
    private readonly Writer w = new();
    private float lastHeard, nextHello, nextPing;
    private string announcedScene;
    private uint announcedEpoch;

    public bool IsHost => transport.IsHost;
    public string Describe => transport.Describe;
    public Phase State { get; private set; } = Phase.Connecting;
    public string Status { get; private set; }
    public ulong Peer { get; private set; }
    public string PeerName { get; private set; } = "";
    public float RttMs { get; private set; } = -1;
    public string HostScene { get; private set; }
    public uint HostEpoch { get; private set; }
    public PlayerState LastRemote { get; private set; }
    public readonly Queue<PlayerState> RemoteStates = new();
    public int StatesSent { get; private set; }
    public int StatesReceived { get; private set; }

    public event Action<string, uint> HostSceneAnnounced;
    public event Action<uint, uint, string> StoryEventReceived; // epoch, seq, key
    public event Action<uint, uint> StoryFromRequested;         // epoch, first seq
    public event Action<string> Joined;
    public event Action<string> Left;

    public Session(ITransport transport, string localName)
    {
        this.transport = transport;
        this.localName = localName;
        Status = IsHost ? "ждём друга" : "подключение...";
        transport.PeerLost += p => { if (p == Peer) Drop("соединение потеряно"); };
    }

    public bool Connected => State == Phase.Connected;

    // localEpoch: the host's count of level loads, so a reload of the same level is a new epoch.
    public void Update(string localScene, uint localEpoch)
    {
        transport.Poll(OnData);
        float now = Time.unscaledTime;
        if (!IsHost && State == Phase.Connecting && transport.HostPeer != 0 && now >= nextHello)
        {
            nextHello = now + HelloEvery;
            w.Reset().U8((byte)Msg.Hello).U16(Protocol.Version).Str(localName).Str(Protocol.GameVersion);
            transport.Send(transport.HostPeer, w.Span, true);
        }
        if (State != Phase.Connected) return;
        if (now - lastHeard > TimeoutAfter)
        {
            Drop("друг не отвечает");
            return;
        }
        if (now >= nextPing)
        {
            nextPing = now + PingEvery;
            w.Reset().U8((byte)Msg.Ping).F32(now);
            transport.Send(Peer, w.Span, false);
        }
        if (IsHost && (localScene != announcedScene || localEpoch != announcedEpoch))
        {
            announcedScene = localScene;
            announcedEpoch = localEpoch;
            w.Reset().U8((byte)Msg.HostScene).Str(localScene).U32(localEpoch);
            transport.Send(Peer, w.Span, true);
        }
    }

    public void SendPlayerState(PlayerState s)
    {
        if (State != Phase.Connected) return;
        s.Write(w.Reset());
        transport.Send(Peer, w.Span, false);
        StatesSent++;
    }

    public void SendStoryEvent(uint epoch, uint seq, string key)
    {
        if (State != Phase.Connected) return;
        w.Reset().U8((byte)Msg.StoryEvent).U32(epoch).U32(seq).Str(key);
        transport.Send(Peer, w.Span, true);
    }

    public void SendStoryFrom(uint epoch, uint from)
    {
        if (State != Phase.Connected) return;
        w.Reset().U8((byte)Msg.StoryFrom).U32(epoch).U32(from);
        transport.Send(Peer, w.Span, true);
    }

    private void OnData(ulong from, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        float now = Time.unscaledTime;
        var r = new Reader(data);
        try
        {
            var type = (Msg)r.U8();
            if (from == Peer && State == Phase.Connected) lastHeard = now;
            switch (type)
            {
                case Msg.Hello when IsHost:
                    OnHello(from, ref r, now);
                    break;
                case Msg.Welcome when !IsHost:
                {
                    ushort version = r.U16();
                    string hostName = r.Str();
                    if (version != Protocol.Version) { Close($"другая версия мода у хоста ({version})"); break; }
                    Peer = from;
                    PeerName = hostName;
                    State = Phase.Connected;
                    lastHeard = now;
                    Status = $"в игре у {hostName}";
                    CoopLog.Info($"session: joined {hostName}");
                    Joined?.Invoke(hostName);
                    break;
                }
                case Msg.Reject when !IsHost:
                    Close("хост отказал: " + r.Str());
                    break;
                case Msg.Ping when from == Peer:
                    w.Reset().U8((byte)Msg.Pong).F32(r.F32());
                    transport.Send(from, w.Span, false);
                    break;
                case Msg.Pong when from == Peer:
                    RttMs = (now - r.F32()) * 1000f;
                    break;
                case Msg.Bye when from == Peer:
                    Drop("друг вышел из игры");
                    break;
                case Msg.PlayerState when from == Peer && State == Phase.Connected:
                {
                    var s = PlayerState.Read(ref r);
                    s.ReceivedAt = now;
                    LastRemote = s;
                    RemoteStates.Enqueue(s);
                    while (RemoteStates.Count > 64) RemoteStates.Dequeue();
                    StatesReceived++;
                    break;
                }
                case Msg.HostScene when !IsHost && from == Peer:
                    HostScene = r.Str();
                    HostEpoch = r.U32();
                    HostSceneAnnounced?.Invoke(HostScene, HostEpoch);
                    break;
                case Msg.StoryEvent when !IsHost && from == Peer && State == Phase.Connected:
                {
                    uint epoch = r.U32(), seq = r.U32();
                    StoryEventReceived?.Invoke(epoch, seq, r.Str());
                    break;
                }
                case Msg.StoryFrom when IsHost && from == Peer && State == Phase.Connected:
                {
                    uint epoch = r.U32();
                    StoryFromRequested?.Invoke(epoch, r.U32());
                    break;
                }
            }
        }
        catch (FormatException e)
        {
            CoopLog.Warn($"session: bad message from {from}: {e.Message}");
        }
    }

    private void OnHello(ulong from, ref Reader r, float now)
    {
        ushort version = r.U16();
        string name = r.Str();
        string gameVersion = r.Str();
        if (version != Protocol.Version)
        {
            Reject(from, $"версия мода: у тебя {version}, у хоста {Protocol.Version}");
            return;
        }
        if (State == Phase.Connected && Peer != from)
        {
            Reject(from, "в игре уже двое");
            return;
        }
        bool fresh = State != Phase.Connected;
        Peer = from;
        PeerName = name;
        State = Phase.Connected;
        lastHeard = now;
        announcedScene = null; // re-announce the scene to this guest
        announcedEpoch = 0;
        Status = $"{name} в игре";
        w.Reset().U8((byte)Msg.Welcome).U16(Protocol.Version).Str(localName);
        transport.Send(from, w.Span, true);
        if (fresh)
        {
            CoopLog.Info($"session: {name} joined (game {gameVersion})");
            Joined?.Invoke(name);
        }
    }

    private void Reject(ulong to, string reason)
    {
        w.Reset().U8((byte)Msg.Reject).Str(reason);
        transport.Send(to, w.Span, true);
    }

    private void Drop(string reason)
    {
        if (State != Phase.Connected) return;
        CoopLog.Info($"session: {PeerName} gone: {reason}");
        string who = PeerName;
        LastRemote = null;
        RemoteStates.Clear();
        if (IsHost)
        {
            Peer = 0;
            State = Phase.Connecting;
            Status = $"{reason}; ждём друга";
        }
        else Close(reason);
        Left?.Invoke(who);
    }

    private void Close(string reason)
    {
        State = Phase.Closed;
        Status = reason;
        CoopLog.Info("session: closed: " + reason);
    }

    public void Dispose()
    {
        if (State == Phase.Connected)
        {
            w.Reset().U8((byte)Msg.Bye);
            transport.Send(Peer, w.Span, true);
            transport.Poll((_, _) => { }); // flush
        }
        transport.Dispose();
    }
}
