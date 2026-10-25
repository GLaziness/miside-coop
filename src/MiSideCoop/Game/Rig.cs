using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MiSideCoop.Game;

// The bones of the player's body model ("Person"): every transform a skinned mesh of the model
// deforms with, in a stable order. Both players have the same model, so the order and the
// layout hash match on both machines.
internal sealed class Rig
{
    public Transform Root;
    public Transform Hips;
    public Transform Head;
    public Transform[] Bones;
    public uint Layout;

    public static Rig Collect(Transform root)
    {
        var set = new HashSet<IntPtrKey>();
        var list = new List<(string path, Transform t)>();
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var bones = smr.bones;
            if (bones == null) continue;
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null || !IsUnder(b, root) || !set.Add(new IntPtrKey(b.Pointer))) continue;
                list.Add((PathFrom(root, b), b));
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
        if (list.Count > 255) list.RemoveRange(255, list.Count - 255);
        uint hash = 2166136261;
        foreach (var (path, _) in list)
            foreach (char c in path) hash = (hash ^ c) * 16777619;
        return new Rig
        {
            Root = root,
            Bones = list.Select(x => x.t).ToArray(),
            Hips = list.FirstOrDefault(x => x.t.name == "Hips").t,
            Head = list.FirstOrDefault(x => x.t.name == "Head").t,
            Layout = hash,
        };
    }

    private static bool IsUnder(Transform t, Transform root)
    {
        for (var p = t; p != null; p = p.parent) if (p == root) return true;
        return false;
    }

    private static string PathFrom(Transform root, Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null && p != root; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    // Il2Cpp wrappers are new objects per call; compare the native pointers.
    private readonly struct IntPtrKey : System.IEquatable<IntPtrKey>
    {
        private readonly System.IntPtr p;
        public IntPtrKey(System.IntPtr p) => this.p = p;
        public bool Equals(IntPtrKey o) => o.p == p;
        public override bool Equals(object o) => o is IntPtrKey k && Equals(k);
        public override int GetHashCode() => p.GetHashCode();
    }
}
