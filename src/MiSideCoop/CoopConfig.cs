using BepInEx.Configuration;
using UnityEngine;

namespace MiSideCoop;

internal static class CoopConfig
{
    private static ConfigEntry<string> playerName;
    private static ConfigEntry<KeyCode> menuKey;
    private static ConfigEntry<int> directPort;
    private static ConfigEntry<string> directJoin;
    private static ConfigEntry<bool> debugLog;

    public static string PlayerName => LaunchArgs.Name ?? playerName.Value;
    public static KeyCode MenuKey => menuKey.Value;
    public static int DirectPort => directPort.Value;
    public static string DirectJoinAddress => directJoin.Value;
    public static bool DebugLog => debugLog?.Value ?? false;

    public static void Bind(ConfigFile cfg)
    {
        playerName = cfg.Bind("General", "PlayerName", "",
            "Name shown above your character for the other player. Empty: your Steam name.");
        menuKey = cfg.Bind("General", "MenuKey", KeyCode.F7, "Key that opens the co-op menu.");
        directPort = cfg.Bind("Direct", "Port", 27015,
            "UDP port for direct-IP games (host listens on it). Steam games do not use it.");
        directJoin = cfg.Bind("Direct", "JoinAddress", "",
            "Host address for direct-IP joining (LAN or VPN such as Radmin/ZeroTier). Menu key 9 joins it.");
        debugLog = cfg.Bind("Diagnostics", "DebugLog", false, "Extra log lines for bug reports.");
    }
}
