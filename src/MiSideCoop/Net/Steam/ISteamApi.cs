using System;
using System.Collections.Generic;

namespace MiSideCoop.Net.Steam;

// The few Steam features the mod uses. The real one talks to steam_api64.dll, which the game
// has already initialised; the mock lets two game copies on one PC pretend to be two friends.
internal interface ISteamApi : IDisposable
{
    bool Available { get; }
    ulong MyId { get; }
    string MyName { get; }

    bool Send(ulong to, ReadOnlySpan<byte> data, bool reliable);
    void Receive(List<(ulong from, byte[] data)> into);

    // True when a session with this user is now accepted (it was pending or already open).
    bool AcceptSession(ulong user);
    void CloseSession(ulong user);

    void SetRichPresence(string key, string value);
    void ClearRichPresence();

    // Immediate friends whose current game is MiSide.
    List<ulong> FriendsInGame();
    string FriendName(ulong user);
    string FriendRichPresence(ulong user, string key);

    void Tick();
}
