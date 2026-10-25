using MiSideCoop.Net;
using UnityEngine;

namespace MiSideCoop.Game;

// Reads the local player's body pose for the other machine.
internal sealed class PlayerSampler
{
    private PlayerMove bound;
    private Rig rig;
    private uint seq;

    public Rig Rig => rig;

    public PlayerState Sample(string scene)
    {
        var s = new PlayerState { Seq = ++seq, Scene = scene };
        var pm = GameAccess.Player;
        if (pm == null) return s;
        if (bound == null || bound.Pointer != pm.Pointer || rig?.Root == null)
        {
            bound = pm;
            var person = pm.scrpp;
            rig = person != null ? Rig.Collect(person.transform) : null;
            if (rig != null) CoopLog.Info($"sampler: {rig.Bones.Length} bones, layout {rig.Layout:x8}");
        }
        if (rig == null) return s;
        s.HasBody = true;
        s.RootPos = rig.Root.position;
        s.RootRot = rig.Root.rotation;
        s.BoneLayout = rig.Layout;
        s.HipsLocalPos = rig.Hips != null ? rig.Hips.localPosition : Vector3.zero;
        s.Bones = new Quaternion[rig.Bones.Length];
        for (int i = 0; i < s.Bones.Length; i++) s.Bones[i] = rig.Bones[i].localRotation;
        return s;
    }

    public void Forget()
    {
        bound = null;
        rig = null;
    }
}
