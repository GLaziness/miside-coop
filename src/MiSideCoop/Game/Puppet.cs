using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MiSideCoop.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MiSideCoop.Game;

// The other player as seen here: a copy of the player's body model with every script removed,
// posed from the received snapshots (interpolated about 100 ms behind).
internal sealed class Puppet
{
    private const float Delay = 0.1f;

    private readonly List<PlayerState> buffer = new();
    private const float HideCloserThan = 0.45f; // a camera inside the head would see the face from within

    private GameObject root;
    private Rig rig;
    private Renderer[] renderers = Array.Empty<Renderer>();
    private bool shown = true;

    public string Scene { get; private set; }
    public bool Alive => root != null;
    public Vector3 Position => root != null ? root.transform.position : Vector3.zero;

    public static Puppet Create(PlayerMove template, string name, string scene)
    {
        var person = template.scrpp;
        if (person == null) return null;

        // Cloned under an inactive parent, so none of the copied scripts ever wake up.
        var holder = new GameObject("CoopPuppetHolder");
        holder.SetActive(false);
        var clone = Object.Instantiate(person.gameObject, holder.transform);
        clone.name = "CoopPuppet " + name;
        try
        {
            Strip(clone);
            clone.SetActive(false);
            clone.transform.SetParent(null, false);

            // The local body is drawn on layers the player's own camera hides (the head is mirror-only).
            var renderers = new List<Renderer>();
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
            {
                r.gameObject.layer = 0;
                r.enabled = true;
                if (r.TryCast<SkinnedMeshRenderer>() is { } smr) smr.updateWhenOffscreen = true;
                renderers.Add(r);
            }

            var p = new Puppet
            {
                root = clone, Scene = scene, rig = Rig.Collect(clone.transform), Name = name,
                renderers = renderers.ToArray(),
            };
            clone.SetActive(true);
            CoopLog.Info($"puppet: created for {name} in {scene}, {p.rig.Bones.Length} bones");
            return p;
        }
        catch
        {
            Object.Destroy(clone);
            throw;
        }
        finally
        {
            Object.Destroy(holder);
        }
    }

    private static void Strip(GameObject clone)
    {
        // Inactive parts (minigame screens, the ring) are not needed on a puppet.
        var all = clone.GetComponentsInChildren<Transform>(true);
        foreach (var t in all)
            if (t != null && t != clone.transform && !t.gameObject.activeSelf)
                Object.DestroyImmediate(t.gameObject);

        // Scripts first, then the components they depended on.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null || Keep(c)) continue;
                if (pass == 0 && c.TryCast<MonoBehaviour>() == null) continue;
                Object.DestroyImmediate(c);
            }
        }
    }

    private static bool Keep(Component c) =>
        c.TryCast<Transform>() != null || c.TryCast<SkinnedMeshRenderer>() != null ||
        c.TryCast<MeshRenderer>() != null || c.TryCast<MeshFilter>() != null;

    public string Name { get; set; }

    // Where the name tag goes: a little above the head.
    public Vector3 TagPosition =>
        rig?.Head != null ? rig.Head.position + new Vector3(0, 0.3f, 0) : Position + new Vector3(0, 1.95f, 0);

    public void Push(PlayerState s)
    {
        if (!s.HasBody) return;
        buffer.Add(s);
        if (buffer.Count > 32) buffer.RemoveAt(0);
    }

    // Both players can stand on one spot (the level's spawn point, an animation spot): the
    // puppet is not drawn while the local camera is inside it.
    public void UpdateVisibility(Camera viewer)
    {
        if (root == null) return;
        bool show = viewer == null || Vector3.Distance(viewer.transform.position, TagPosition - new Vector3(0, 0.3f, 0)) > HideCloserThan;
        if (show == shown) return;
        shown = show;
        foreach (var r in renderers) if (r != null) r.enabled = show;
    }

    public bool Shown => shown;

    public void Update(float now)
    {
        if (root == null || buffer.Count == 0) return;
        float t = now - Delay;
        PlayerState a = buffer[0], b = buffer[buffer.Count - 1];
        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            if (buffer[i].ReceivedAt <= t)
            {
                a = buffer[i];
                b = i + 1 < buffer.Count ? buffer[i + 1] : buffer[i];
                break;
            }
        }
        float k = b.ReceivedAt > a.ReceivedAt ? Mathf.Clamp01((t - a.ReceivedAt) / (b.ReceivedAt - a.ReceivedAt)) : 1f;
        root.transform.SetPositionAndRotation(Vector3.Lerp(a.RootPos, b.RootPos, k), Quaternion.Slerp(a.RootRot, b.RootRot, k));
        if (a.BoneLayout == rig.Layout && b.BoneLayout == rig.Layout &&
            a.Bones.Length == rig.Bones.Length && b.Bones.Length == rig.Bones.Length)
        {
            for (int i = 0; i < rig.Bones.Length; i++)
                rig.Bones[i].localRotation = Quaternion.Slerp(a.Bones[i], b.Bones[i], k);
            if (rig.Hips != null) rig.Hips.localPosition = Vector3.Lerp(a.HipsLocalPos, b.HipsLocalPos, k);
        }
        while (buffer.Count > 2 && buffer[1].ReceivedAt < t) buffer.RemoveAt(0);
    }

    public void Destroy()
    {
        if (root != null) Object.Destroy(root);
        root = null;
    }
}
