using System;
using System.Collections.Generic;
using HarmonyLib;
using MiSideCoop.Game;
using UnityEngine;
using UnityEngine.Events;

namespace MiSideCoop.Story;

// The story runs on the host only. MiSide's story is a chain of UnityEvents held by level scripts
// (triggers, timers, dialogue ends, interactions, choices). Every UnityEvent.Invoke is hooked:
//   host:  each top-level event of the level is logged and sent as (epoch, seq, key). Events
//          nested inside another mapped event are not sent: the guest gets them by replaying
//          the outer one.
//   guest: its own mapped events are suppressed; the host's are replayed in order, and nested
//          events inside a replay run normally.
// Events that fire while a level starts (before its map is built) run on both machines, as do
// events of the persistent objects (player, GameController, menus), which are never mapped.
internal static class StorySync
{
    public struct Traced
    {
        public float Time;
        public char Mark;   // ' ' ran, 'x' suppressed (guest), '<' replayed (guest), '?' unknown
        public int Depth;
        public string Key;
    }

    private const int MaxPerFrame = 16;

    private static readonly Queue<Traced> trace = new();
    private static readonly HashSet<IntPtr> unknown = new();
    private static readonly HashSet<IntPtr> firstSeen = new();
    private static float nextRescan;
    private static int mappedHandle;
    private static int depth;
    private static bool replaying;

    // host
    private static readonly List<string> log = new();
    private static readonly Dictionary<string, (int frame, int streak)> lastFired = new();
    private static uint epoch;

    // guest
    private static uint hostEpoch;
    private static uint syncedEpoch;
    private static uint nextSeq;
    private static readonly SortedDictionary<uint, string> pending = new();
    private static float gapSince = -1f;
    private static float nextFromRequest;

    // True while a host event is replayed here (synchronously, with everything nested in it).
    public static bool Replaying => replaying;
    public static int Invocations { get; private set; }
    public static int Sent { get; private set; }
    public static int Suppressed { get; private set; }
    public static int Replayed { get; private set; }
    public static int Missing { get; private set; }
    public static uint Epoch => epoch;
    public static int LogCount => log.Count;
    public static uint HostEpoch => hostEpoch;
    public static uint NextSeq => nextSeq;
    public static int Pending => pending.Count;
    public static IEnumerable<Traced> Trace => trace;

    // Story steps that the game takes straight from the keyboard in some Update, without a
    // UnityEvent: replayed like events, under "<script key>::<Method>(<int args>)".
    private static readonly (Type type, string method)[] ReplayedCalls =
    {
        (typeof(Tamagotchi_Dialogue), nameof(Tamagotchi_Dialogue.KeyNextDialogue)),
        (typeof(DialogueChanger), nameof(DialogueChanger.ClickButton)),
    };

    public static void Apply(Harmony h)
    {
        var target = AccessTools.Method(typeof(UnityEvent), nameof(UnityEvent.Invoke), Type.EmptyTypes);
        h.Patch(target,
            prefix: new HarmonyMethod(typeof(StorySync), nameof(BeforeInvoke)),
            postfix: new HarmonyMethod(typeof(StorySync), nameof(AfterInvoke)));
        foreach (var (type, name) in ReplayedCalls)
        {
            var m = AccessTools.Method(type, name);
            if (m == null) { CoopLog.Warn($"story: {type.Name}.{name} not found"); continue; }
            EventMap.CallTypes.Add(type.FullName);
            h.Patch(m,
                prefix: new HarmonyMethod(typeof(StorySync), nameof(BeforeCall)),
                postfix: new HarmonyMethod(typeof(StorySync), nameof(AfterInvoke)));
        }
    }

    // A new scene instance is active (also a reload of the same level).
    public static void OnLevelChanged()
    {
        EventMap.Clear();
        unknown.Clear();
        firstSeen.Clear();
        depth = 0;
        log.Clear();
        lastFired.Clear();
        mappedHandle = GameAccess.ActiveSceneHandle;
        if (!SceneFollower.IsLevel(GameAccess.ActiveScene)) return;
        epoch++;
        int n = EventMap.Scan();
        CoopLog.Info($"events: {n} in {GameAccess.ActiveScene}, epoch {epoch} ({EventMap.LastScanMs:0} ms)");
    }

    public static void ResetSession()
    {
        hostEpoch = syncedEpoch = nextSeq = 0;
        pending.Clear();
        gapSince = -1f;
    }

    private static bool IsGuest => CoopNet.Session is { IsHost: false, Connected: true };

    // --- the hook ------------------------------------------------------------------------------

    private static bool BeforeInvoke(UnityEvent __instance, out bool __state)
    {
        __state = false;
        Invocations++;
        var ptr = __instance.Pointer;
        string key = KeyOf(ptr);
        if (key == null)
        {
            if ((CoopRuntime.Bridge != null || CoopConfig.DebugLog) && firstSeen.Add(ptr))
                Record('?', EventMap.Whois(ptr) ?? ptr.ToString("x"));
            return true;
        }
        return Gate(key, out __state);
    }

    // Host: send it if it is not nested in another story step. Guest: run it only as a replay.
    private static bool Gate(string key, out bool entered)
    {
        entered = false;
        bool streaming = Streak(key) > 3;
        if (IsGuest)
        {
            if (!replaying)
            {
                Suppressed++;
                if (!streaming) Record('x', key);
                return false;
            }
            Record('<', key);
        }
        else
        {
            if (depth == 0 && SceneFollower.IsLevel(GameAccess.ActiveScene))
            {
                if (streaming) Streamed++;
                else HostEmit(key);
            }
            if (!streaming) Record(' ', key);
        }
        depth++;
        entered = true;
        return true;
    }

    // Some scripts fire an event every frame while a condition holds (Mesh_VisibleEvents while
    // an object is on screen, distance checks). Such a stream is sent at its start only (its
    // first three frames): the guest's own copy fires the same stream, suppressed, so the replay
    // has the same effect, and the log of the level stays short.
    private static int Streak(string key)
    {
        int frame = Time.frameCount;
        lastFired.TryGetValue(key, out var last);
        int streak = last.frame == frame - 1 ? last.streak + 1 : last.frame == frame ? last.streak : 1;
        lastFired[key] = (frame, streak);
        return streak;
    }

    private static bool BeforeCall(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __instance,
        System.Reflection.MethodBase __originalMethod, object[] __args, out bool __state)
    {
        __state = false;
        if (GameAccess.ActiveSceneHandle != mappedHandle) return true;
        string owner = EventMap.ComponentKeyOf(__instance.Pointer);
        if (owner == null) return true;
        string key = $"{owner}::{__originalMethod.Name}({string.Join(",", __args ?? Array.Empty<object>())})";
        return Gate(key, out __state);
    }

    private static void AfterInvoke(bool __state)
    {
        if (__state && depth > 0) depth--;
    }

    private static string KeyOf(IntPtr ev)
    {
        // Between a level load and the first frame that sees it, the map is the old level's.
        if (GameAccess.ActiveSceneHandle != mappedHandle) return null;
        var key = EventMap.KeyOf(ev);
        if (key != null || unknown.Contains(ev) || EventMap.Count == 0) return key;
        // a level object made after the last scan?
        if (Time.unscaledTime >= nextRescan)
        {
            nextRescan = Time.unscaledTime + 1f;
            int added = EventMap.Scan();
            if (added > 0) CoopLog.Info($"events: +{added} after rescan ({EventMap.LastScanMs:0} ms)");
            key = EventMap.KeyOf(ev);
        }
        if (key == null) unknown.Add(ev);
        return key;
    }

    // --- host ----------------------------------------------------------------------------------

    public static int Streamed { get; private set; }

    private static void HostEmit(string key)
    {
        if (log.Count >= 100000) return;
        uint seq = (uint)log.Count;
        log.Add(key);
        var s = CoopNet.Session;
        if (s is { IsHost: true, Connected: true })
        {
            s.SendStoryEvent(epoch, seq, key);
            Sent++;
        }
    }

    // The guest asks for the events of this level from some point (on arrival: from the start).
    public static void OnStoryFrom(uint wantedEpoch, uint from)
    {
        var s = CoopNet.Session;
        if (s == null || wantedEpoch != epoch) return;
        for (uint i = from; i < log.Count; i++) s.SendStoryEvent(epoch, i, log[(int)i]);
        CoopLog.Info($"story: sent events {from}..{log.Count - 1} of epoch {epoch}");
    }

    // --- guest ---------------------------------------------------------------------------------

    public static void OnHostEpoch(uint newEpoch)
    {
        if (newEpoch == hostEpoch) return;
        hostEpoch = newEpoch;
        pending.Clear();
        gapSince = -1f;
    }

    public static void OnStoryEvent(uint evEpoch, uint seq, string key)
    {
        if (evEpoch != hostEpoch) return;                      // from a level instance that is gone
        if (evEpoch == syncedEpoch && seq < nextSeq) return;   // already applied
        if (pending.Count < 20000) pending[seq] = key;
    }

    public static void Update()
    {
        depth = 0; // nothing is mid-invoke between frames
        if (!IsGuest || hostEpoch == 0 || !SceneFollower.InStep || SceneFollower.TargetEpoch != hostEpoch) return;
        if (EventMap.Count == 0) return;
        var s = CoopNet.Session;
        float now = Time.unscaledTime;
        if (syncedEpoch != hostEpoch)
        {
            syncedEpoch = hostEpoch;
            nextSeq = 0;
            s.SendStoryFrom(hostEpoch, 0);
            CoopLog.Info($"story: in step with the host, epoch {hostEpoch}");
        }
        for (int n = 0; n < MaxPerFrame && pending.TryGetValue(nextSeq, out var key); n++)
        {
            pending.Remove(nextSeq);
            nextSeq++;
            Replay(key);
        }
        while (pending.Count > 0)
        {
            using var e = pending.Keys.GetEnumerator();
            e.MoveNext();
            if (e.Current >= nextSeq) break;
            pending.Remove(e.Current);
        }
        // A hole in the sequence (a message lost across a reconnect): ask again from there.
        if (pending.Count > 0 && !pending.ContainsKey(nextSeq))
        {
            if (gapSince < 0) gapSince = now;
            if (now - gapSince > 2f && now >= nextFromRequest)
            {
                nextFromRequest = now + 2f;
                s.SendStoryFrom(hostEpoch, nextSeq);
                CoopLog.Info($"story: missing event {nextSeq}, asked again");
            }
        }
        else gapSince = -1f;
    }

    private static void Replay(string key)
    {
        int call = key.IndexOf("::", StringComparison.Ordinal);
        if (call >= 0)
        {
            ReplayCall(key, key.Substring(0, call), key.Substring(call + 2));
            return;
        }
        var ev = EventMap.EventOf(key);
        if (ev == null && Time.unscaledTime >= nextRescan)
        {
            nextRescan = Time.unscaledTime + 1f;
            EventMap.Scan();
            ev = EventMap.EventOf(key);
        }
        if (ev == null)
        {
            // Usually an object only the host has (its autosave icon): nothing to do here.
            Missing++;
            Record('!', key);
            CoopLog.Info("story: no event here for " + key);
            return;
        }
        replaying = true;
        try
        {
            ev.Invoke();
            Replayed++;
        }
        catch (Exception e)
        {
            CoopLog.Warn($"story: replaying {key}: {e.Message}");
        }
        finally
        {
            replaying = false;
        }
    }

    // "<script key>::Method(1,2)": the same script on this side, the method with int arguments.
    private static void ReplayCall(string key, string owner, string call)
    {
        var c = EventMap.ComponentOf(owner);
        int open = call.IndexOf('(');
        string name = open < 0 ? call : call.Substring(0, open);
        string argText = open < 0 ? "" : call.Substring(open + 1).TrimEnd(')');
        var (type, _) = Array.Find(ReplayedCalls, x => c != null && x.type.FullName == c.GetIl2CppType().FullName);
        if (c == null || type == null)
        {
            Missing++;
            Record('!', key);
            CoopLog.Warn("story: no script here for " + key);
            return;
        }
        var args = argText.Length == 0 ? Array.Empty<object>()
            : Array.ConvertAll(argText.Split(','), a => (object)int.Parse(a, System.Globalization.CultureInfo.InvariantCulture));
        var method = AccessTools.Method(type, name, Array.ConvertAll(args, a => a.GetType()));
        replaying = true;
        try
        {
            method.Invoke(Activator.CreateInstance(type, c.Pointer), args);
            Replayed++;
        }
        catch (Exception e)
        {
            CoopLog.Warn($"story: replaying {key}: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            replaying = false;
        }
    }

    private static void Record(char mark, string key)
    {
        trace.Enqueue(new Traced { Time = Time.unscaledTime, Mark = mark, Depth = depth, Key = key });
        while (trace.Count > 400) trace.Dequeue();
        CoopLog.Debug($"event {mark}{new string('>', depth)}{key}");
    }
}
