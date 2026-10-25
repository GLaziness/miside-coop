using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideCoop.Game;

// Guest side: whenever the host is in a level, load the same level here, the way the game does.
// From a level, the persistent GameController (with the player) carries over, so the level loads
// directly. From the menu there is none yet: the level has to go through the loading scene,
// which reads its target from GlobalGame.LoadingLevel, as Menu.ButtonNewGame does.
//
// Each load of a level on the host is a new epoch. The guest's copy of the level is in step only
// if it was loaded for the host's current epoch: a level the guest was already in, or one the
// host has since reloaded (death, checkpoint), is loaded again so both start from the same state.
internal static class SceneFollower
{
    private static readonly HashSet<string> NotLevels = new() { "SceneAihasto", "SceneLoading", "SceneMenu" };
    private static string target;
    private static uint targetEpoch;
    private static string requested;
    private static uint requestedEpoch;
    private static uint loadedEpoch;
    private static float retryAt;

    public static string Target => target;
    public static uint TargetEpoch => targetEpoch;

    // The guest stands in the host's level instance, so the host's story events apply here.
    public static bool InStep => IsLevel(target) && SceneManager.GetActiveScene().name == target && loadedEpoch == targetEpoch;

    public static bool IsLevel(string scene) => !string.IsNullOrEmpty(scene) && !NotLevels.Contains(scene);

    public static void OnHostScene(string scene, uint epoch)
    {
        target = scene;
        targetEpoch = epoch;
        CoopLog.Info($"follow: host is in {scene} (epoch {epoch})");
    }

    // A scene instance became active here (also a reload of the same scene). Only the load this
    // class asked for counts as the host's epoch; any other load leaves the guest out of step.
    public static void OnSceneInstance(string scene)
    {
        if (requested != null && scene == requested)
        {
            loadedEpoch = requestedEpoch;
            requested = null;
        }
        else if (scene != "SceneLoading")
        {
            loadedEpoch = 0;
        }
    }

    public static void Reset()
    {
        target = null;
        requested = null;
        targetEpoch = requestedEpoch = loadedEpoch = 0;
    }

    public static void Update()
    {
        if (!IsLevel(target)) return;
        string current = SceneManager.GetActiveScene().name;
        if (current == target && loadedEpoch == targetEpoch) return;
        // Not while the game is still booting (logo, first-start language screen) or loading.
        if (current == "SceneAihasto" || current == "SceneLoading") return;
        if (requested == target && requestedEpoch == targetEpoch && Time.unscaledTime < retryAt) return;
        requested = target;
        requestedEpoch = targetEpoch;
        retryAt = Time.unscaledTime + 30f;
        Time.timeScale = 1f;
        if (GameAccess.HasGameController)
        {
            CoopLog.Info($"follow: loading {target} for epoch {targetEpoch} (was {current})");
            SceneManager.LoadScene(target);
        }
        else
        {
            CoopLog.Info($"follow: loading {target} for epoch {targetEpoch} through the loading scene (was {current})");
            GlobalGame.LoadingLevel = target;
            SceneManager.LoadScene("SceneLoading");
        }
    }
}
