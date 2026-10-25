using System;
using System.Text.Json.Nodes;
using MiSideCoop.Bridge;
using MiSideCoop.Game;
using UnityEngine;

namespace MiSideCoop;

// Everything that runs each frame, outside the injected IL2CPP type.
internal static class CoopRuntime
{
    public static BridgeServer Bridge { get; private set; }

    private static bool started;
    private static string lastScene;
    private static int lastSceneHandle;
    private static bool hadPlayer;
    private static int errorCount;

    public static void Update(CoopDriver driver)
    {
        try
        {
            if (!started) Start();
            Story.StorySync.Update();
            Bridge?.Pump();
            TrackGame();
            CoopNet.Update(lastScene);
            CoopMenu.Update();
        }
        catch (Exception e)
        {
            if (errorCount++ < 20) CoopLog.Error("update: " + e);
        }
    }

    public static void LateUpdate()
    {
        try
        {
            CoopNet.LateUpdate(lastScene);
        }
        catch (Exception e)
        {
            if (errorCount++ < 20) CoopLog.Error("late update: " + e);
        }
    }

    public static void OnGUI()
    {
        try
        {
            CoopNet.OnGUI();
            CoopMenu.OnGUI();
        }
        catch (Exception e)
        {
            if (errorCount++ < 20) CoopLog.Error("gui: " + e);
        }
    }

    public static void Shutdown()
    {
        CoopNet.Stop();
        CoopNet.Steam?.Dispose();
        Bridge?.Stop();
    }

    private static void Start()
    {
        started = true;
        if (LaunchArgs.BridgePort >= 0)
        {
            Bridge = new BridgeServer("miside", Plugin.Version);
            GameCommands.Register(Bridge);
            if (Bridge.Start(LaunchArgs.BridgePort, LaunchArgs.BridgeFile))
                CoopLog.Info($"bridge listening on tcp://127.0.0.1:{Bridge.Port}");
        }
    }

    private static void TrackGame()
    {
        string scene = GameAccess.ActiveScene;
        int handle = GameAccess.ActiveSceneHandle;
        if (scene != lastScene || handle != lastSceneHandle)
        {
            CoopLog.Info($"scene: {lastScene ?? "-"} -> {scene} (loaded: {string.Join(", ", GameAccess.LoadedScenes())})");
            Bridge?.PushEvent("scene.changed", new JsonObject { ["from"] = lastScene, ["to"] = scene });
            lastScene = scene;
            lastSceneHandle = handle;
            GameAccess.ForgetPlayer();
            SceneFollower.OnSceneInstance(scene);
            Story.StorySync.OnLevelChanged();
            CoopNet.OnSceneChanged();
        }
        bool hasPlayer = GameAccess.Player != null;
        if (hasPlayer != hadPlayer)
        {
            CoopLog.Info(hasPlayer ? $"player found in {scene}" : "player gone");
            Bridge?.PushEvent(hasPlayer ? "player.found" : "player.lost", new JsonObject { ["scene"] = scene });
            hadPlayer = hasPlayer;
        }
    }

    public static bool TryExec(string cmd, string rest, out (bool, string) result) =>
        CoopNet.TryExec(cmd, rest, out result);

    public static bool TryEval(string expr, out JsonNode value) => CoopNet.TryEval(expr, out value);

    public static void AddState(JsonObject state) => CoopNet.AddState(state);
}
