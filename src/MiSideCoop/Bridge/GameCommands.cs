using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime;
using MiSideCoop.Game;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideCoop.Bridge;

// What the bridge can do in MiSide: console-like commands (exec), values (eval) and a state
// snapshot. Test scripts and the reverse-engineering notes use these names.
internal static class GameCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Register(BridgeServer b)
    {
        b.Eval = Eval;
        b.Exec = Exec;
        b.LoadedCount = () => GameAccess.Player != null ? 1 : 0;
        b.Register("exec", p =>
        {
            var cmd = BridgeServer.Str(p, "cmd") ?? throw new BridgeError(-32602, "exec requires string param 'cmd'");
            var (ok, output) = Exec(cmd);
            return new JsonObject { ["ok"] = ok, ["output"] = output };
        });
        b.Register("eval", p =>
        {
            var expr = BridgeServer.Str(p, "expr") ?? throw new BridgeError(-32602, "eval requires string param 'expr'");
            return new JsonObject { ["value"] = Eval(expr) };
        });
        b.Register("state", p => State());
        b.Register("screenshot", p =>
        {
            var path = BridgeServer.Str(p, "path");
            if (string.IsNullOrEmpty(path)) path = System.IO.Path.GetFullPath($"coop-shot-{DateTime.Now:HHmmss}.png");
            ScreenCapture.CaptureScreenshot(path);
            return new JsonObject { ["ok"] = true, ["path"] = path };
        });
    }

    public static (bool ok, string output) Exec(string line)
    {
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (false, "empty");
        string rest = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "scene.load":
                SceneManager.LoadScene(rest);
                return (true, "loading " + rest);
            case "menu.load":
            {
                var menu = Find<Menu>();
                if (menu == null) return (false, "no menu");
                menu.ButtonLoadScene(rest);
                return (true, "menu loading " + rest);
            }
            case "menu.newgame":
            {
                var menu = Find<Menu>();
                if (menu == null) return (false, "no menu");
                menu.ButtonNewGame();
                return (true, "new game");
            }
            case "dump":
            {
                int n = HierarchyDump.Write(rest);
                return (true, $"{n} objects -> {rest}");
            }
            case "find":
                return FindObjects(rest);
            case "world.active":
                return ActiveObjects(rest.Trim());
            case "menu.toggle":
                CoopMenu.Toggle();
                return (true, "");
            case "bot.clickables":
                return StoryBot.Clickables();
            case "bot.click":
                return StoryBot.Click(rest);
            case "story.keys":
            {
                var keys = Story.EventMap.Keys.Where(k => k.Contains(rest.Trim(), StringComparison.Ordinal)).ToList();
                return (true, $"{keys.Count}" + string.Concat(keys.Select(k => "\n" + k)));
            }
            case "story.invoke":
            {
                // Fires a mapped event here as the game would (tests drive the host's story with it).
                var keys = Story.EventMap.Keys.Where(k => k == rest || k.EndsWith(rest, StringComparison.Ordinal)).ToList();
                if (keys.Count != 1) return (false, $"{keys.Count} events match");
                Story.EventMap.EventOf(keys[0]).Invoke();
                return (true, keys[0]);
            }
            case "call":
                return GameReflection.Call(rest);
            case "get":
                return GameReflection.Get(rest);
            case "set":
                return GameReflection.Set(rest);
            case "tp":
            {
                var pm = GameAccess.Player;
                if (pm == null) return (false, "no player");
                var v = ParseVector(rest);
                pm.transform.position = v;
                return (true, Fmt(v));
            }
            case "face":
            {
                // Turn the player toward a point: "face x y z".
                var pm = GameAccess.Player;
                if (pm == null) return (false, "no player");
                var d = ParseVector(rest) - pm.transform.position;
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                pm.TeleportRotate(yaw);
                return (true, yaw.ToString("0.#", Inv));
            }
            case "timescale":
                Time.timeScale = float.Parse(rest, Inv);
                return (true, Time.timeScale.ToString(Inv));
            case "log":
                CoopLog.Info("marker: " + rest);
                return (true, "");
            case "quit":
                Application.Quit();
                return (true, "quitting");
            default:
                if (CoopRuntime.TryExec(parts[0], rest, out var result)) return result;
                return (false, "unknown command: " + parts[0]);
        }
    }

    public static JsonNode Eval(string expr)
    {
        switch (expr)
        {
            case "frames": return Time.frameCount;
            case "time": return (double)Time.realtimeSinceStartup;
            case "scene": return GameAccess.ActiveScene;
            case "scenes": return string.Join(",", GameAccess.LoadedScenes());
            case "scenes.build":
                return string.Join("\n", Enumerable.Range(0, SceneManager.sceneCountInBuildSettings)
                    .Select(i => System.IO.Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i))));
            case "player.found": return GameAccess.Player != null;
            case "player.pos":
            {
                var pm = GameAccess.Player;
                return pm == null ? null : Fmt(pm.transform.position);
            }
            case "player.x": return GameAccess.Player is { } a ? (double)a.transform.position.x : null;
            case "player.y": return GameAccess.Player is { } b ? (double)b.transform.position.y : null;
            case "player.z": return GameAccess.Player is { } c ? (double)c.transform.position.z : null;
            case "player.rot": return GameAccess.Player is { } d ? $"{d.rotX.ToString("0.#", Inv)} {d.rotY.ToString("0.#", Inv)}" : null;
            case "camera.fwd": return Camera.main is { } cam ? Fmt(cam.transform.forward) : null;
            case "story.mapped": return Story.EventMap.Count;
            case "story.scans": return Story.EventMap.Scans;
            case "story.invocations": return Story.StorySync.Invocations;
            case "story.epoch": return Story.StorySync.Epoch;
            case "story.log": return Story.StorySync.LogCount;
            case "story.sent": return Story.StorySync.Sent;
            case "story.hostepoch": return Story.StorySync.HostEpoch;
            case "story.next": return Story.StorySync.NextSeq;
            case "story.pending": return Story.StorySync.Pending;
            case "story.replayed": return Story.StorySync.Replayed;
            case "story.suppressed": return Story.StorySync.Suppressed;
            case "story.missing": return Story.StorySync.Missing;
            case "follow.instep": return SceneFollower.InStep;
            case "story.trace":
                return string.Join("\n", Story.StorySync.Trace.Select(t =>
                    $"{t.Time.ToString("0.00", Inv)} {t.Mark}{new string('>', t.Depth)}{t.Key}"));
        }
        if (CoopRuntime.TryEval(expr, out var value)) return value;
        throw new BridgeError(-32010, "unknown expression: " + expr);
    }

    private static JsonObject State()
    {
        var pm = GameAccess.Player;
        var game = new JsonObject
        {
            ["scene"] = GameAccess.ActiveScene,
            ["scenes"] = new JsonArray(GameAccess.LoadedScenes().Select(s => (JsonNode)s).ToArray()),
            ["frames"] = Time.frameCount,
            ["player"] = pm == null ? null : new JsonObject
            {
                ["pos"] = Fmt(pm.transform.position),
                ["rotY"] = (double)pm.transform.eulerAngles.y,
                ["dontMove"] = pm.dontMove,
            },
        };
        var state = new JsonObject { ["game"] = game };
        CoopRuntime.AddState(state);
        return state;
    }

    private static (bool, string) FindObjects(string typeName)
    {
        var type = Il2CppSystem.Type.GetType(typeName + ", Assembly-CSharp") ?? Il2CppSystem.Type.GetType(typeName);
        if (type == null) return (false, "type not found: " + typeName);
        var found = UnityEngine.Object.FindObjectsOfType(type);
        var lines = found.Select(o =>
        {
            var c = o.TryCast<Component>();
            return c == null ? o.name : Path(c.transform) + " @" + Fmt(c.transform.position);
        });
        return (true, $"{found.Length}\n" + string.Join("\n", lines));
    }

    // Paths of the active objects of the loaded level scenes, optionally only under a prefix.
    // Two machines in step show the same list.
    private static (bool, string) ActiveObjects(string prefix)
    {
        var lines = new System.Collections.Generic.List<string>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects()) CollectActive(root.transform, root.name, prefix, lines);
        }
        return (true, string.Join("\n", lines));
    }

    private static void CollectActive(Transform t, string path, string prefix, System.Collections.Generic.List<string> lines)
    {
        if (!t.gameObject.activeSelf) return;
        bool under = prefix.Length == 0 || path.StartsWith(prefix, StringComparison.Ordinal);
        bool onWay = !under && prefix.StartsWith(path, StringComparison.Ordinal);
        if (!under && !onWay) return;
        if (under) lines.Add(path);
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            CollectActive(c, path + "/" + c.name, prefix, lines);
        }
    }

    private static T Find<T>() where T : UnityEngine.Object
    {
        var o = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<T>());
        return o == null ? null : o.TryCast<T>();
    }

    public static string Path(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    public static string Fmt(Vector3 v) =>
        v.x.ToString("0.###", Inv) + " " + v.y.ToString("0.###", Inv) + " " + v.z.ToString("0.###", Inv);

    public static Vector3 ParseVector(string s)
    {
        var f = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, Inv)).ToArray();
        return new Vector3(f[0], f[1], f[2]);
    }
}
