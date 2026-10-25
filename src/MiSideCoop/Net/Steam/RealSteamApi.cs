using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static MiSideCoop.Net.Steam.SteamNative;

namespace MiSideCoop.Net.Steam;

internal sealed unsafe class RealSteamApi : ISteamApi
{
    public const uint MiSideAppId = 2527500;
    private const int Channel = 0;

    private readonly IntPtr user, friends, messages;
    private readonly IntPtr[] inbox = new IntPtr[64];

    public bool Available { get; }
    public ulong MyId { get; }
    public string MyName { get; }
    public string Problem { get; }

    public RealSteamApi()
    {
        EnsureResolver();
        if (!IsLoaded()) { Problem = "steam_api64.dll is not loaded"; return; }
        if (!SteamManager.Initialized) { Problem = "the game did not initialise Steam"; return; }
        // SteamAPI_IsSteamRunning() is not used: it returns false in this game even while logged on.
        user = SteamAPI_SteamUser_v021();
        friends = SteamAPI_SteamFriends_v017();
        messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        if (user == IntPtr.Zero || friends == IntPtr.Zero || messages == IntPtr.Zero)
        {
            Problem = $"interfaces missing (user {user != IntPtr.Zero}, friends {friends != IntPtr.Zero}, messages {messages != IntPtr.Zero})";
            return;
        }
        if (!SteamAPI_ISteamUser_BLoggedOn(user)) { Problem = "not logged on to Steam"; return; }
        MyId = SteamAPI_ISteamUser_GetSteamID(user);
        MyName = FromUtf8(SteamAPI_ISteamFriends_GetPersonaName(friends));
        var utils = SteamAPI_SteamNetworkingUtils_SteamAPI_v003();
        if (utils != IntPtr.Zero) SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(utils);
        Available = MyId != 0;
    }

    private static Identity Id(ulong steamId)
    {
        var id = new Identity();
        SteamAPI_SteamNetworkingIdentity_SetSteamID64(ref id, steamId);
        return id;
    }

    public bool Send(ulong to, ReadOnlySpan<byte> data, bool reliable)
    {
        var id = Id(to);
        int flags = reliable ? SendReliable | SendAutoRestartBrokenSession : SendUnreliableNoNagle;
        fixed (byte* p = data)
            return SteamAPI_ISteamNetworkingMessages_SendMessageToUser(messages, ref id, p, (uint)data.Length, flags, Channel) == ResultOk;
    }

    public void Receive(List<(ulong from, byte[] data)> into)
    {
        while (true)
        {
            int n;
            fixed (IntPtr* box = inbox)
                n = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(messages, Channel, box, inbox.Length);
            for (int i = 0; i < n; i++)
            {
                var msg = inbox[i];
                var dataPtr = Marshal.ReadIntPtr(msg, MsgDataOffset);
                int size = Marshal.ReadInt32(msg, MsgSizeOffset);
                ulong from = SteamAPI_SteamNetworkingIdentity_GetSteamID64(msg + MsgIdentityOffset);
                var copy = new byte[size];
                Marshal.Copy(dataPtr, copy, 0, size);
                SteamAPI_SteamNetworkingMessage_t_Release(msg);
                into.Add((from, copy));
            }
            if (n < inbox.Length) return;
        }
    }

    public bool AcceptSession(ulong user)
    {
        var id = Id(user);
        return SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(messages, ref id);
    }

    public void CloseSession(ulong user)
    {
        var id = Id(user);
        SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(messages, ref id);
    }

    public void SetRichPresence(string key, string value)
    {
        fixed (byte* k = Utf8Z(key))
        fixed (byte* v = Utf8Z(value))
            SteamAPI_ISteamFriends_SetRichPresence(friends, k, v);
    }

    public void ClearRichPresence() => SteamAPI_ISteamFriends_ClearRichPresence(friends);

    public List<ulong> FriendsInGame()
    {
        var list = new List<ulong>();
        int count = SteamAPI_ISteamFriends_GetFriendCount(friends, FriendFlagImmediate);
        for (int i = 0; i < count; i++)
        {
            ulong f = SteamAPI_ISteamFriends_GetFriendByIndex(friends, i, FriendFlagImmediate);
            if (SteamAPI_ISteamFriends_GetFriendGamePlayed(friends, f, out var info) &&
                (uint)(info.GameId & 0xFFFFFF) == MiSideAppId)
                list.Add(f);
        }
        return list;
    }

    // Counts only, for a smoke test of the friend calls: friends, friends in any game, in MiSide.
    // The local user's own entry checks the game-id decoding: it must say MiSide.
    public (int friends, int inGame, int inMiSide, bool meInMiSide) FriendStats()
    {
        bool me = SteamAPI_ISteamFriends_GetFriendGamePlayed(friends, MyId, out var mine) &&
                  (uint)(mine.GameId & 0xFFFFFF) == MiSideAppId;
        int count = SteamAPI_ISteamFriends_GetFriendCount(friends, FriendFlagImmediate), inGame = 0, inMiSide = 0;
        for (int i = 0; i < count; i++)
        {
            ulong f = SteamAPI_ISteamFriends_GetFriendByIndex(friends, i, FriendFlagImmediate);
            if (!SteamAPI_ISteamFriends_GetFriendGamePlayed(friends, f, out var info)) continue;
            inGame++;
            if ((uint)(info.GameId & 0xFFFFFF) == MiSideAppId) inMiSide++;
        }
        return (count, inGame, inMiSide, me);
    }

    public string FriendName(ulong user) => FromUtf8(SteamAPI_ISteamFriends_GetFriendPersonaName(friends, user));

    public string FriendRichPresence(ulong user, string key)
    {
        fixed (byte* k = Utf8Z(key))
            return FromUtf8(SteamAPI_ISteamFriends_GetFriendRichPresence(friends, user, k));
    }

    public void Tick() { }

    public void Dispose() { }
}
