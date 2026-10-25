using System;
using UnityEngine;

namespace MiSideCoop.Net;

internal enum Msg : byte
{
    Hello = 1,        // guest -> host: protocol, name, game version
    Welcome = 2,      // host -> guest: protocol, host name
    Reject = 3,       // host -> guest: reason
    Ping = 4,         // either way, keep-alive with a timestamp
    Pong = 5,
    Bye = 6,
    PlayerState = 10, // either way, unreliable, ~20 per second
    HostScene = 20,   // host -> guest, reliable: the scene the host plays now and its epoch
    StoryEvent = 30,  // host -> guest, reliable: epoch, sequence number, event key
    StoryFrom = 31,   // guest -> host, reliable: epoch, first sequence number wanted (re)sent
}

internal static class Protocol
{
    // Bump when a message layout changes; both sides must match.
    public const ushort Version = 2;
    public const string GameVersion = "0.93L";
}

// One snapshot of a player: where the body is and how every bone of the model is posed.
internal sealed class PlayerState
{
    public uint Seq;
    public string Scene = "";
    public bool HasBody;
    public Vector3 RootPos;
    public Quaternion RootRot = Quaternion.identity;
    public uint BoneLayout;          // hash of the bone names, so a different model is ignored
    public Vector3 HipsLocalPos;
    public Quaternion[] Bones = Array.Empty<Quaternion>();
    public float ReceivedAt;         // local receive time, set on arrival

    public void Write(Writer w)
    {
        w.U8((byte)Msg.PlayerState).U32(Seq).Str(Scene).Bool(HasBody);
        if (!HasBody) return;
        w.Vec3(RootPos).Quat(RootRot).U32(BoneLayout).Vec3(HipsLocalPos).U8((byte)Bones.Length);
        foreach (var q in Bones) w.QuatPacked(q);
    }

    // The message id byte has been read already.
    public static PlayerState Read(ref Reader r)
    {
        var s = new PlayerState { Seq = r.U32(), Scene = r.Str(), HasBody = r.Bool() };
        if (!s.HasBody) return s;
        s.RootPos = r.Vec3();
        s.RootRot = r.Quat();
        s.BoneLayout = r.U32();
        s.HipsLocalPos = r.Vec3();
        int n = r.U8();
        s.Bones = new Quaternion[n];
        for (int i = 0; i < n; i++) s.Bones[i] = r.QuatPacked();
        return s;
    }
}
