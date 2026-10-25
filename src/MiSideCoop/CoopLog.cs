using BepInEx.Logging;

namespace MiSideCoop;

internal static class CoopLog
{
    private static ManualLogSource source;

    public static void Init(ManualLogSource log) => source = log;
    public static void Info(string msg) => source?.LogInfo(msg);
    public static void Warn(string msg) => source?.LogWarning(msg);
    public static void Error(string msg) => source?.LogError(msg);
    public static void Debug(string msg)
    {
        if (CoopConfig.DebugLog) source?.LogInfo("[dbg] " + msg);
    }
}
