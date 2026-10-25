using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace MiSideCoop;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "miside.coop";
    public const string Name = "MiSide Coop";
    public const string Version = "0.1.0";

    internal static Harmony Harmony { get; private set; }

    public override void Load()
    {
        CoopLog.Init(Log);
        LaunchArgs.Parse(Environment.GetCommandLineArgs());
        CoopConfig.Bind(Config);

        ClassInjector.RegisterTypeInIl2Cpp<CoopDriver>();
        Harmony = new Harmony(Guid);
        GamePatches.Apply(Harmony);
        try { Story.StorySync.Apply(Harmony); }
        catch (Exception e) { CoopLog.Error("story hooks: " + e); }

        AddComponent<CoopDriver>();
        CoopLog.Info($"{Name} {Version} loaded, slot={LaunchArgs.Slot ?? "-"}");
    }
}
