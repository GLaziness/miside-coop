using MiSideCoop.Net;
using UnityEngine;

namespace MiSideCoop.Game;

// Guest side: the story places the host's character (level start, cutscenes, a new room), and
// the guest's character is kept out of the host's interactions. So the guest is brought next to
// the host when it arrives in the host's level, and whenever the host jumps far in one step.
internal static class Regroup
{
    private const float JumpDistance = 4f;

    private static uint placedEpoch;
    private static Vector3 lastHostPos;
    private static bool haveLast;

    public static int Teleports { get; private set; }

    public static void Reset()
    {
        placedEpoch = 0;
        haveLast = false;
    }

    public static void Update(Session s, string scene)
    {
        if (s is not { IsHost: false, Connected: true }) return;
        var me = GameAccess.Player;
        var host = s.LastRemote;
        if (me == null || host == null || !host.HasBody || host.Scene != scene)
        {
            haveLast = false;
            return;
        }
        bool jumped = haveLast && (host.RootPos - lastHostPos).sqrMagnitude > JumpDistance * JumpDistance;
        lastHostPos = host.RootPos;
        haveLast = true;
        bool arrived = SceneFollower.InStep && placedEpoch != SceneFollower.TargetEpoch;
        if (arrived) placedEpoch = SceneFollower.TargetEpoch;
        else if (!jumped || Vector3.Distance(me.transform.position, host.RootPos) < JumpDistance) return;
        CoopLog.Info(arrived ? "regroup: arrived in the host's level" : "regroup: the host moved far");
        TeleportNear(me, host.RootPos, host.RootRot);
    }

    // Menu: "to the host".
    public static string ToHost()
    {
        var s = CoopNet.Session;
        var me = GameAccess.Player;
        var host = s?.LastRemote;
        if (me == null || host == null || !host.HasBody || host.Scene != GameAccess.ActiveScene)
            return "хоста сейчас нет рядом";
        TeleportNear(me, host.RootPos, host.RootRot);
        return "ты рядом с хостом";
    }

    // A free spot a metre beside or behind the host, on the host's floor.
    private static void TeleportNear(PlayerMove me, Vector3 hostPos, Quaternion hostRot)
    {
        var right = hostRot * Vector3.right;
        var back = hostRot * Vector3.back;
        var target = hostPos;
        foreach (var dir in new[] { right, -right, back, -back })
        {
            if (Physics.Raycast(hostPos + Vector3.up, dir, 1.3f)) continue;
            target = hostPos + dir;
            break;
        }
        me.TeleportPlayer(target);
        Teleports++;
    }
}
