using System;
using System.Text.Json.Nodes;
using HarmonyLib;

namespace MiSideCoop;

// Harmony hooks into the game's own methods: they record how the game changes scenes, and on a
// connected guest they hold back level changes, which follow the host instead (SceneFollower).
internal static class GamePatches
{
    public static void Apply(Harmony h)
    {
        Patch(h, typeof(Menu), nameof(Menu.ButtonLoadScene), nameof(LogMenuLoadScene));
        Patch(h, typeof(Menu), nameof(Menu.ButtonNewGame), nameof(LogCall));
        Patch(h, typeof(Menu), nameof(Menu.ButtonContinue), nameof(LogCall));
        Patch(h, typeof(Scene_Load), nameof(Scene_Load.StartLoad), nameof(GuestHoldsSceneLoad));
        Patch(h, typeof(Scene_Load), nameof(Scene_Load.GoScene), nameof(GuestHoldsSceneLoad));
        Patch(h, typeof(SceneLoading), nameof(SceneLoading.LoadGo), nameof(LogSceneLoading));
        Patch(h, typeof(GameController), nameof(GameController.StartSceneLoadAsync), nameof(LogCall));

        // The guest's own single-player save is not overwritten by the host's playthrough.
        Patch(h, typeof(Scene_Load), nameof(Scene_Load.SaveGame), nameof(GuestDoesNotSave));
        Patch(h, typeof(Scene_Load), nameof(Scene_Load.SilentSave), nameof(GuestDoesNotSave));

        // A host's interaction, replayed on the guest, must not grab the guest's own character:
        // the animation, the item in hand and the camera turn belong to the host's character,
        // which the guest sees as the puppet.
        foreach (var m in AccessTools.GetDeclaredMethods(typeof(PlayerMove)))
            if (m.Name is nameof(PlayerMove.AnimationPlay) or nameof(PlayerMove.AnimationHandPlay)
                or nameof(PlayerMove.AnimationHandPlayFast) or nameof(PlayerMove.TakeItem)
                or nameof(PlayerMove.Look) or nameof(PlayerMove.CameraTargetOther))
                PatchMethod(h, m, nameof(KeepGuestCharacter));
    }

    private static bool GuestDoesNotSave(System.Reflection.MethodBase __originalMethod)
    {
        if (CoopNet.Session is not { IsHost: false, Connected: true }) return true;
        CoopLog.Info($"guest: skipped Scene_Load.{__originalMethod.Name}, the host keeps the save");
        return false;
    }

    private static bool KeepGuestCharacter(System.Reflection.MethodBase __originalMethod)
    {
        if (!Story.StorySync.Replaying) return true;
        CoopLog.Debug($"guest: kept own character out of PlayerMove.{__originalMethod.Name}");
        return false;
    }

    private static void PatchMethod(Harmony h, System.Reflection.MethodInfo target, string prefix)
    {
        try
        {
            h.Patch(target, prefix: new HarmonyMethod(typeof(GamePatches), prefix));
        }
        catch (Exception e)
        {
            CoopLog.Error($"patch {target.DeclaringType?.Name}.{target.Name}: {e.Message}");
        }
    }

    private static void Patch(Harmony h, Type type, string method, string prefix)
    {
        try
        {
            var target = AccessTools.Method(type, method);
            if (target == null) { CoopLog.Warn($"patch: {type.Name}.{method} not found"); return; }
            h.Patch(target, prefix: new HarmonyMethod(typeof(GamePatches), prefix));
        }
        catch (Exception e)
        {
            CoopLog.Error($"patch {type.Name}.{method}: {e.Message}");
        }
    }

    private static void LogCall(object __instance, System.Reflection.MethodBase __originalMethod)
    {
        Emit($"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}", null);
    }

    private static void LogMenuLoadScene(string _nameScene)
    {
        Emit("Menu.ButtonLoadScene", new JsonObject { ["scene"] = _nameScene });
    }

    // A whole-level load on a connected guest is skipped: the guest loads what the host loads,
    // when the host announces it. Sub-scenes (additive loads) load as usual.
    private static bool GuestHoldsSceneLoad(Scene_Load __instance, System.Reflection.MethodBase __originalMethod)
    {
        bool hold = CoopNet.Session is { IsHost: false, Connected: true } &&
                    __instance.modeLoad == UnityEngine.SceneManagement.LoadSceneMode.Single;
        Emit($"Scene_Load.{__originalMethod.Name}", new JsonObject
        {
            ["object"] = __instance.name,
            ["load"] = __instance.nameSceneLoad,
            ["unload"] = __instance.nameSceneUnload,
            ["mode"] = __instance.modeLoad.ToString(),
            ["continue"] = __instance.nameSceneContinue,
            ["held"] = hold,
        });
        return !hold;
    }

    private static void LogSceneLoading(SceneLoading __instance)
    {
        Emit("SceneLoading.LoadGo", new JsonObject { ["level"] = __instance.levelLoadName });
    }

    private static void Emit(string what, JsonObject data)
    {
        CoopLog.Info($"game: {what} {data?.ToJsonString()}");
        CoopRuntime.Bridge?.PushEvent("game.call", new JsonObject { ["what"] = what, ["data"] = data });
    }
}
