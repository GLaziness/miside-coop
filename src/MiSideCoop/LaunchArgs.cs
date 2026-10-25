using System;

namespace MiSideCoop;

// Command-line switches. Test runs use them to start hosting or joining without a menu and to
// open the in-game bridge; Steam's "Join game" adds +coop_join. A normal launch has none.
internal static class LaunchArgs
{
    public static string Slot { get; private set; }          // -coop-slot A
    public static int BridgePort { get; private set; } = -1; // -coop-bridge 27990 (0 = any free port)
    public static string BridgeFile { get; private set; }    // -coop-bridge-file <path> (endpoint JSON)
    public static string Host { get; private set; }          // -coop-host udp:27015 | steam
    public static string Join { get; private set; }          // -coop-join udp:127.0.0.1:27015 | steam:first
    public static string Name { get; private set; }          // -coop-name Alice
    public static int MockSteam { get; private set; }        // -coop-mocksteam 1 (tests: fake Steam user 1)
    public static string MockSteamDir { get; private set; }  // -coop-mocksteam-dir <path>
    public static ulong SteamJoin { get; private set; }      // +coop_join <steamid> (rich presence "connect")

    public static void Parse(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a.ToLowerInvariant())
            {
                case "-coop-slot": Slot = next; i++; break;
                case "-coop-bridge": BridgePort = int.TryParse(next, out var p) ? p : 0; i++; break;
                case "-coop-bridge-file": BridgeFile = next; i++; break;
                case "-coop-host": Host = next; i++; break;
                case "-coop-join": Join = next; i++; break;
                case "-coop-name": Name = next; i++; break;
                case "-coop-mocksteam": MockSteam = int.TryParse(next, out var m) ? m : 0; i++; break;
                case "-coop-mocksteam-dir": MockSteamDir = next; i++; break;
                case "+coop_join": SteamJoin = ulong.TryParse(next, out var j) ? j : 0; i++; break;
            }
        }
    }
}
