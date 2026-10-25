using System.Collections.Generic;
using MiSideCoop.Net;
using UnityEngine;

namespace MiSideCoop;

// The co-op menu: F7 opens it, number keys choose. Keyboard only, so the game's locked mouse
// and first-person controls are never touched.
internal static class CoopMenu
{
    private static readonly KeyCode[] Digits =
    {
        KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
        KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
    };

    private static bool open;
    private static List<(ulong id, string name)> hosts = new();
    private static float nextRefresh;
    private static string flash;
    private static float flashUntil;
    private static GUIStyle box;

    // The menu key, or a test through the bridge ("menu.toggle").
    public static void Toggle()
    {
        open = !open;
        nextRefresh = 0;
    }

    public static void Update()
    {
        if (Input.GetKeyDown(CoopConfig.MenuKey)) Toggle();
        if (!open) return;

        var s = CoopNet.Session;
        if (s == null && Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 2f;
            hosts = CoopNet.FindSteamHosts();
        }
        int digit = PressedDigit();
        if (digit < 0) return;

        if (s != null)
        {
            if (digit == 0) { CoopNet.Stop(); Flash("Отключено"); }
            else if (digit == 7 && !s.IsHost) Flash(Game.Regroup.ToHost());
            return;
        }
        if (digit == 1) Flash(CoopNet.HostSteam());
        else if (digit >= 2 && digit - 2 < hosts.Count && digit <= 6) Flash(CoopNet.JoinSteam(hosts[digit - 2].id));
        else if (digit == 8) Flash(CoopNet.HostUdp(CoopConfig.DirectPort));
        else if (digit == 9 && !string.IsNullOrWhiteSpace(CoopConfig.DirectJoinAddress))
            Flash(CoopNet.JoinUdp(CoopConfig.DirectJoinAddress.Trim(), CoopConfig.DirectPort));
    }

    private static int PressedDigit()
    {
        for (int i = 0; i < Digits.Length; i++)
            if (Input.GetKeyDown(Digits[i])) return i;
        return -1;
    }

    private static void Flash(string text)
    {
        flash = text;
        flashUntil = Time.unscaledTime + 4f;
    }

    public static void OnGUI()
    {
        box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 16, wordWrap = true };
        var s = CoopNet.Session;
        if (!open)
        {
            if (s != null) GUI.Label(new Rect(10, 10, 600, 24), Short(s));
            return;
        }
        var lines = new List<string> { $"MiSide Co-op {Plugin.Version}   ({CoopConfig.MenuKey} — закрыть)", "" };
        if (s != null)
        {
            lines.Add(Short(s));
            lines.Add(s.Connected ? $"Пинг: {s.RttMs:0} мс" : s.Status);
            lines.Add("");
            if (!s.IsHost && s.Connected) lines.Add("7 — переместиться к хосту");
            lines.Add("0 — отключиться");
        }
        else
        {
            lines.Add(CoopNet.Steam != null ? $"Steam: {CoopNet.Steam.MyName}" : "Steam: недоступен");
            lines.Add("1 — создать игру (друзья увидят её в этом меню)");
            if (hosts.Count == 0) lines.Add("   друзей с открытой игрой пока нет");
            for (int i = 0; i < hosts.Count && i < 5; i++) lines.Add($"{i + 2} — войти к {hosts[i].name}");
            lines.Add("");
            lines.Add($"8 — создать игру по IP (порт {CoopConfig.DirectPort})");
            if (!string.IsNullOrWhiteSpace(CoopConfig.DirectJoinAddress))
                lines.Add($"9 — войти по IP {CoopConfig.DirectJoinAddress}");
        }
        if (flash != null && Time.unscaledTime < flashUntil) { lines.Add(""); lines.Add(flash); }
        float h = 26 + lines.Count * 22;
        GUI.Box(new Rect(10, 10, 520, h), string.Join("\n", lines), box);
    }

    private static string Short(Session s) =>
        s.Connected ? $"Co-op: {(s.IsHost ? "хост" : "гость")}, с тобой {s.PeerName}" : $"Co-op: {s.Status}";
}
