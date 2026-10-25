using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideCoop.Game;

// Read access to the single-player game: the local player and the scenes that are loaded.
internal static class GameAccess
{
    private static PlayerMove player;
    private static float nextPlayerSearch;

    public static string ActiveScene => SceneManager.GetActiveScene().name;

    // Changes on every load, also when the same level is loaded again.
    public static int ActiveSceneHandle => SceneManager.GetActiveScene().handle;

    public static string[] LoadedScenes()
    {
        var names = new string[SceneManager.sceneCount];
        for (int i = 0; i < names.Length; i++) names[i] = SceneManager.GetSceneAt(i).name;
        return names;
    }

    // The local player's controller, or null outside gameplay (menus, loading, some cutscenes).
    public static PlayerMove Player
    {
        get
        {
            if (player != null && player.isActiveAndEnabled) return player;
            player = null;
            if (Time.unscaledTime < nextPlayerSearch) return null;
            nextPlayerSearch = Time.unscaledTime + 0.5f;
            var found = Object.FindObjectOfType(Il2CppType.Of<PlayerMove>());
            player = found != null ? found.TryCast<PlayerMove>() : null;
            return player != null && player.isActiveAndEnabled ? player : null;
        }
    }

    // The persistent object that owns the player; it exists from the first level until the menu.
    public static bool HasGameController => Object.FindObjectOfType(Il2CppType.Of<GameController>()) != null;

    public static void ForgetPlayer()
    {
        player = null;
        nextPlayerSearch = 0;
    }
}
