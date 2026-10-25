using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideCoop.Bridge;

// A test player for the story: lists what can be used right now in the level (interactive
// objects, UI buttons of the level, trigger zones) and uses one the way the game does, so test
// scripts can play the host's story without a human. Names are the same object paths as in
// Story.EventMap: "<scene>:<path>|<type>#<n>".
internal static class StoryBot
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (bool, string) Clickables()
    {
        var lines = new List<string>();
        foreach (var (key, c) in Components())
        {
            string kind = Kind(c);
            if (kind == null) continue;
            var p = c.transform.position;
            lines.Add($"{kind}\t{key}\t{p.x.ToString("0.##", Inv)} {p.y.ToString("0.##", Inv)} {p.z.ToString("0.##", Inv)}");
        }
        return (true, string.Join("\n", lines));
    }

    public static (bool, string) Click(string key)
    {
        var c = Find(key.Trim());
        if (c == null) return (false, "not found: " + key);
        if (c.TryCast<ObjectInteractive>() is { } oi) { oi.Click(); return (true, "clicked"); }
        if (c.TryCast<ButtonMouseClick>() is { } b) { b.PointerDown(); b.PointerUp(); return (true, "pressed"); }
        if (c.TryCast<Trigger_Event>() is { } te) { te.TargetEnter(); return (true, "entered"); }
        if (c.TryCast<Trigger_MouseEvent>() is { } tm) { tm.Enter(); return (true, "entered"); }
        if (c.TryCast<Interface_KeyHint_Key>() is { } k) { k.eventKeyDown.Invoke(); return (true, "key " + k.nameKey); }
        if (c.TryCast<Tamagotchi_Dialogue>() is { } td) { td.KeyNextDialogue(); return (true, "next line"); }
        return (false, "cannot use " + c.GetIl2CppType().Name);
    }

    private static string Kind(Component c)
    {
        if (!c.gameObject.activeInHierarchy) return null;
        var mb = c.TryCast<Behaviour>();
        if (mb != null && !mb.enabled) return null;
        if (c.TryCast<ObjectInteractive>() is { } oi) return oi.active ? "interactive" : null;
        if (c.TryCast<ButtonMouseClick>() is { } b) return b.interactable ? "button" : null;
        if (c.TryCast<Trigger_Event>() != null) return "trigger";
        if (c.TryCast<Trigger_MouseEvent>() != null) return "look";
        if (c.TryCast<Interface_KeyHint_Key>() != null) return "key";
        if (c.TryCast<Tamagotchi_Dialogue>() != null) return "dialogue";
        return null;
    }

    // Every game script of the loaded level scenes, under its path.
    private static IEnumerable<(string key, Component c)> Components()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            var names = new Dictionary<string, int>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var x in Walk(root.transform, scene.name + ":" + Unique(names, root.name)))
                    yield return x;
        }
    }

    private static IEnumerable<(string, Component)> Walk(Transform t, string path)
    {
        var counts = new Dictionary<string, int>();
        foreach (var c in t.GetComponents(Il2CppType.Of<MonoBehaviour>()))
        {
            if (c == null) continue;
            string type = c.GetIl2CppType().FullName;
            counts.TryGetValue(type, out int n);
            counts[type] = n + 1;
            yield return ($"{path}|{type}#{n}", c);
        }
        var names = new Dictionary<string, int>();
        for (int i = 0; i < t.childCount; i++)
        {
            var child = t.GetChild(i);
            foreach (var x in Walk(child, path + "/" + Unique(names, child.name))) yield return x;
        }
    }

    private static string Unique(Dictionary<string, int> seen, string name)
    {
        seen.TryGetValue(name, out int n);
        seen[name] = n + 1;
        return n == 0 ? name : name + "#" + n;
    }

    private static Component Find(string key) => Components().FirstOrDefault(x => x.key == key).c;
}
