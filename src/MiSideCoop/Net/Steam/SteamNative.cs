using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MiSideCoop.Net.Steam;

// Steamworks flat API (SDK of the game's steam_api64.dll: Friends v017, User v021,
// NetworkingMessages v002, NetworkingUtils v003). No callbacks: the game owns the callback
// dispatcher (Steamworks.NET in manual-dispatch mode), so everything here is a direct call.
internal static unsafe class SteamNative
{
    private const string Dll = "steam_api64";

    public const int SendUnreliableNoNagle = 1;
    public const int SendReliable = 8;
    public const int SendAutoRestartBrokenSession = 32;
    public const int FriendFlagImmediate = 4;
    public const int ResultOk = 1;

    // SteamNetworkingIdentity is 136 bytes (pack 1): type, size, then a 128-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    public struct Identity
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public int Size;
        [FieldOffset(8)] public ulong SteamId;
    }

    // FriendGameInfo_t, Windows packing (8).
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct FriendGameInfo
    {
        public ulong GameId;
        public uint GameIp;
        public ushort GamePort;
        public ushort QueryPort;
        public ulong Lobby;
    }

    // Offsets inside SteamNetworkingMessage_t.
    public const int MsgDataOffset = 0;
    public const int MsgSizeOffset = 8;
    public const int MsgIdentityOffset = 16;

    private static bool resolverSet;

    // steam_api64.dll is already loaded by the game from MiSideFull_Data\Plugins\x86_64; bind to that module.
    public static void EnsureResolver()
    {
        if (resolverSet) return;
        resolverSet = true;
        NativeLibrary.SetDllImportResolver(typeof(SteamNative).Assembly, (name, asm, path) =>
        {
            if (name != Dll) return IntPtr.Zero;
            var h = GetModuleHandleW("steam_api64.dll");
            return h;
        });
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    public static bool IsLoaded() => GetModuleHandleW("steam_api64.dll") != IntPtr.Zero;

    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_IsSteamRunning();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUser_v021();
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUser_BLoggedOn(IntPtr self);

    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamFriends_v017();
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    [DllImport(Dll)] public static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
    [DllImport(Dll)] public static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong friend);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong friend, out FriendGameInfo info);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, byte* key, byte* value);
    [DllImport(Dll)] public static extern void SteamAPI_ISteamFriends_ClearRichPresence(IntPtr self);
    [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong friend, byte* key);

    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Dll)]
    public static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(
        IntPtr self, ref Identity remote, byte* data, uint size, int flags, int channel);
    [DllImport(Dll)]
    public static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(
        IntPtr self, int channel, IntPtr* messages, int max);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, ref Identity remote);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, ref Identity remote);
    [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
    [DllImport(Dll)] public static extern ulong SteamAPI_SteamNetworkingIdentity_GetSteamID64(IntPtr identity);
    [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(ref Identity identity, ulong id);

    [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v003();
    [DllImport(Dll)] public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);

    public static byte[] Utf8Z(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    public static string FromUtf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
}
