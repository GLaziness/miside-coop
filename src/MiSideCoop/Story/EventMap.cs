using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MiSideCoop.Story;

// Every UnityEvent that a game script in the loaded level holds, under a name that is the same
// on both machines because both load the same level:
//   "<object path>|<script type>#<n>.<field>[<i>].<field>"
// Object names that repeat among siblings get "#k". The persistent DontDestroyOnLoad objects
// (player, GameController, menus) are not part of any level and are never mapped.
internal static class EventMap
{
    private delegate void Collect(object owner, string prefix, Action<string, UnityEvent> add);

    private static readonly Dictionary<IntPtr, string> keyOf = new();
    private static readonly Dictionary<string, UnityEvent> eventOf = new();
    private static readonly Dictionary<string, Collect[]> collectorsByType = new();
    private static readonly Dictionary<string, Type> gameTypes = new();
    private static readonly HashSet<IntPtr> scannedComponents = new();
    private static readonly Dictionary<IntPtr, string> componentKeyOf = new();
    private static readonly Dictionary<string, Component> componentOf = new();

    // Scripts some of whose methods are replayed (StorySync.ReplayedCalls).
    public static readonly HashSet<string> CallTypes = new();

    public static int Count => eventOf.Count;
    public static int Scans { get; private set; }
    public static float LastScanMs { get; private set; }

    public static string KeyOf(IntPtr ev) => keyOf.TryGetValue(ev, out var k) ? k : null;
    public static UnityEvent EventOf(string key) => eventOf.TryGetValue(key, out var e) ? e : null;
    public static bool IsMapped(IntPtr ev) => keyOf.ContainsKey(ev);
    public static IEnumerable<string> Keys => eventOf.Keys;

    public static string ComponentKeyOf(IntPtr component) =>
        componentKeyOf.TryGetValue(component, out var k) ? k : null;

    public static Component ComponentOf(string key) => componentOf.TryGetValue(key, out var c) ? c : null;

    public static void Clear()
    {
        keyOf.Clear();
        eventOf.Clear();
        scannedComponents.Clear();
        componentKeyOf.Clear();
        componentOf.Clear();
    }

    // Adds what is new since the last scan. An event already known under one name keeps it, so a
    // script that stores another script's event (MitaPerson.eventFinish) does not rename it.
    public static int Scan()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int before = eventOf.Count;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded) WalkRoots(scene.GetRootGameObjects(), scene.name, scannedComponents, Add);
        }
        Scans++;
        LastScanMs = (float)watch.Elapsed.TotalMilliseconds;
        return eventOf.Count - before;
    }

    // Diagnostics: which script holds this event, looking at the persistent objects too.
    public static string Whois(IntPtr ev)
    {
        var known = KeyOf(ev);
        if (known != null) return known;
        string found = null;
        void Look(string key, UnityEvent e) { if (found == null && e != null && e.Pointer == ev) found = key; }
        var probe = new GameObject("coop-ddol-probe");
        Object.DontDestroyOnLoad(probe);
        try
        {
            WalkRoots(probe.scene.GetRootGameObjects(), "DontDestroyOnLoad", new HashSet<IntPtr>(), Look);
            for (int i = 0; found == null && i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) WalkRoots(scene.GetRootGameObjects(), scene.name, new HashSet<IntPtr>(), Look);
            }
        }
        finally
        {
            Object.Destroy(probe);
        }
        return found;
    }

    private static void WalkRoots(Il2CppReferenceArray<GameObject> roots, string sceneName, HashSet<IntPtr> scanned,
        Action<string, UnityEvent> add)
    {
        var names = new Dictionary<string, int>();
        foreach (var root in roots)
        {
            if (root.name == "coop-ddol-probe") continue;
            Walk(root.transform, sceneName + ":" + Unique(names, root.name), scanned, add);
        }
    }

    private static string Unique(Dictionary<string, int> seen, string name)
    {
        seen.TryGetValue(name, out int n);
        seen[name] = n + 1;
        return n == 0 ? name : name + "#" + n;
    }

    private static void Walk(Transform t, string path, HashSet<IntPtr> scanned, Action<string, UnityEvent> add)
    {
        var comps = t.GetComponents(Il2CppType.Of<MonoBehaviour>());
        Dictionary<string, int> typeCounts = null;
        foreach (var c in comps)
        {
            if (c == null) continue; // missing script
            string typeName = c.GetIl2CppType().FullName;
            typeCounts ??= new Dictionary<string, int>();
            typeCounts.TryGetValue(typeName, out int n);
            typeCounts[typeName] = n + 1;
            if (!scanned.Add(c.Pointer)) continue;
            string prefix = $"{path}|{typeName}#{n}";
            if (CallTypes.Contains(typeName) && add == Add && !componentKeyOf.ContainsKey(c.Pointer) && !componentOf.ContainsKey(prefix))
            {
                componentKeyOf[c.Pointer] = prefix;
                componentOf[prefix] = c;
            }
            var collectors = CollectorsFor(typeName);
            if (collectors.Length == 0) continue;
            var proxy = Activator.CreateInstance(gameTypes[typeName], c.Pointer);
            foreach (var collect in collectors)
            {
                try { collect(proxy, prefix, add); }
                catch (Exception e) { CoopLog.Debug($"events: {prefix}: {e.Message}"); }
            }
        }
        int count = t.childCount;
        if (count == 0) return;
        var names = new Dictionary<string, int>();
        for (int i = 0; i < count; i++)
        {
            var child = t.GetChild(i);
            Walk(child, path + "/" + Unique(names, child.name), scanned, add);
        }
    }

    private static void Add(string key, UnityEvent ev)
    {
        if (ev == null || keyOf.ContainsKey(ev.Pointer) || eventOf.ContainsKey(key)) return;
        keyOf[ev.Pointer] = key;
        eventOf[key] = ev;
    }

    // --- which fields of a game type hold events -------------------------------------------

    private static Collect[] CollectorsFor(string typeName)
    {
        if (collectorsByType.TryGetValue(typeName, out var c)) return c;
        if (gameTypes.Count == 0) IndexGameTypes();
        c = gameTypes.TryGetValue(typeName, out var type) ? Build(type, 0) : Array.Empty<Collect>();
        collectorsByType[typeName] = c;
        return c;
    }

    private static void IndexGameTypes()
    {
        foreach (var asm in new[] { typeof(PlayerMove).Assembly, typeof(GlobalGame).Assembly })
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsGenericTypeDefinition) continue;
                // interop names nested types "Outer+Inner" like IL2CPP's FullName does
                gameTypes[t.FullName ?? t.Name] = t;
            }
        }
    }

    private static bool IsGameType(Type t) =>
        t.Assembly == typeof(PlayerMove).Assembly || t.Assembly == typeof(GlobalGame).Assembly;

    // Interop proxies expose IL2CPP fields as properties; real C# properties (which run game
    // code) have no NativeFieldInfoPtr_ field and are never touched.
    private static bool IsField(Type declaring, PropertyInfo p) =>
        declaring.GetField("NativeFieldInfoPtr_" + p.Name, BindingFlags.NonPublic | BindingFlags.Static) != null;

    private static Collect[] Build(Type type, int depth)
    {
        var list = new List<Collect>();
        for (var cur = type; cur != null && IsGameType(cur); cur = cur.BaseType)
        {
            foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetMethod == null || p.GetIndexParameters().Length != 0 || !IsField(cur, p)) continue;
                var collect = ForField(p, depth);
                if (collect != null) list.Add(collect);
            }
        }
        return list.ToArray();
    }

    private static Collect ForField(PropertyInfo p, int depth)
    {
        var pt = p.PropertyType;
        string name = p.Name;
        if (typeof(UnityEvent).IsAssignableFrom(pt))
            return (o, prefix, add) => add(prefix + "." + name, p.GetValue(o) as UnityEvent);

        if (pt.IsGenericType)
        {
            var def = pt.GetGenericTypeDefinition();
            var et = pt.GetGenericArguments()[0];
            bool array = def == typeof(Il2CppReferenceArray<>);
            bool list = def == typeof(Il2CppSystem.Collections.Generic.List<>);
            if (!array && !list) return null;
            var countProp = pt.GetProperty(array ? "Length" : "Count");
            var item = pt.GetProperty("Item");
            if (countProp == null || item == null) return null;
            if (typeof(UnityEvent).IsAssignableFrom(et))
                return (o, prefix, add) => ForEach(p.GetValue(o), countProp, item, (i, e) => add($"{prefix}.{name}[{i}]", e as UnityEvent));
            if (depth == 0 && IsGameType(et) && !typeof(UnityEngine.Object).IsAssignableFrom(et))
            {
                var inner = Build(et, 1);
                if (inner.Length == 0) return null;
                return (o, prefix, add) => ForEach(p.GetValue(o), countProp, item, (i, e) =>
                {
                    if (e == null) return;
                    foreach (var c in inner) c(e, $"{prefix}.{name}[{i}]", add);
                });
            }
            return null;
        }

        if (depth == 0 && IsGameType(pt) && !typeof(UnityEngine.Object).IsAssignableFrom(pt) && !pt.IsValueType)
        {
            var inner = Build(pt, 1);
            if (inner.Length == 0) return null;
            return (o, prefix, add) =>
            {
                var v = p.GetValue(o);
                if (v == null) return;
                foreach (var c in inner) c(v, prefix + "." + name, add);
            };
        }
        return null;
    }

    private static void ForEach(object container, PropertyInfo countProp, PropertyInfo item, Action<int, object> each)
    {
        if (container == null) return;
        int n = (int)countProp.GetValue(container);
        for (int i = 0; i < n; i++) each(i, item.GetValue(container, new object[] { i }));
    }
}
