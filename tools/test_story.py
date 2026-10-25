"""Story sync on one PC: host in test install A, guest in B, over direct UDP on loopback.

    python -I tools/test_story.py [--keep]

The host starts a new game and a test bot plays the start of level 1 on it through the bridge
(choices, tutorial, phone, the phone game with Mita). After every step the guest must have
replayed the host's story events in order, found every one of them, and show the same objects
active in the level and the same dialogue line. Prints RESULT: PASS or RESULT: FAIL with the failing step.
Traces, screenshots and logs go to local/logs/story-<time>/.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(HERE, "bridge"))
sys.path.insert(0, HERE)

from bridge_client import Bridge, endpoint_from_file  # noqa: E402
import launch  # noqa: E402
from test_pair import Fail, boot_to_menu, step, wait  # noqa: E402

LEVEL = "Scene 1 - RealRoom"
TUTORIAL_READY = "Tutorial|Time_Events#0.EventsOnTime[0]._event"

# Level 1 as the host plays it: (label, kind, target, seconds to let it play out).
#   click:  use a clickable of the host whose key ends with target (bot.click)
#           (the phone in the hand belongs to the persistent player, so its key hints are fired
#           as events: their keys keep the level path they had when the level was mapped)
#   invoke: fire the host's mapped event whose key ends with target (what the game's own
#           input check would fire: the tutorial's "mouse moved" and "walked")
#   next:   the phone game's next line (SPACE)
LEVEL1 = [
    ("style choice", "click", "Frame RussianText/Button Hello)|ButtonMouseClick#0", 3),
    ("tutorial: yes", "click", "Frame EnterTutorial/Button Yes|ButtonMouseClick#0", 3),
    ("looked around", "invoke", "Tutorial|Location1Tutorial#0.eventReadyMouse", 3),
    ("walked", "invoke", "Tutorial|Location1Tutorial#0.eventReadyMove", 3),
    ("t-shirt", "click", "Interactive TShip Tutorial|ObjectInteractive#0", 6),
    ("phone", "click", "Interactive Mobile|ObjectInteractive#0", 8),
    ("open message", "invoke", "3D HintKey OpenMessage|Interface_KeyHint_Key#0.eventKeyDown", 8),
    ("download", "invoke", "3D HintKey Download|Interface_KeyHint_Key#0.eventKeyDown", 10),
    ("play", "invoke", "3D HintKey Play|Interface_KeyHint_Key#0.eventKeyDown", 10),
    ("Mita line 2", "next", "", 3),
    ("Mita line 3", "next", "", 4),
    ("name confirmed", "click", "InputName/ButtonEnter|ButtonMouseClick#0", 6),
] + [(f"Mita line {i + 4}", "next", "", 3) for i in range(4)]


def caught_up(host: Bridge, guest: Bridge, timeout_s: float = 30) -> int:
    """Wait until the guest has replayed everything the host logged; returns the count."""
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        logged = host.eval("story.log")
        if guest.eval("story.next") >= logged and guest.eval("story.pending") == 0:
            return logged
        time.sleep(0.25)
    raise Fail(f"guest behind: host logged {host.eval('story.log')}, guest at {guest.eval('story.next')}, "
               f"pending {guest.eval('story.pending')}")


def world_diff(host: Bridge, guest: Bridge, prefix: str = "World"):
    a = set(filter(None, host.exec("world.active " + prefix)["output"].split("\n")))
    b = set(filter(None, guest.exec("world.active " + prefix)["output"].split("\n")))
    return sorted(a - b), sorted(b - a), len(a)


def check_world(host: Bridge, guest: Bridge, label: str, run_dir: str, settle_s: float = 15):
    # The guest entered the level a little later, so its local animations (a cutscene that ends
    # by itself) finish a little later too: allow a few seconds to settle.
    deadline = time.time() + settle_s
    while True:
        only_host, only_guest, total = world_diff(host, guest)
        if not (only_host or only_guest) or time.time() >= deadline:
            break
        time.sleep(1)
    print(f"    {label}: {total} active objects on the host, {len(only_host)} only on the host, "
          f"{len(only_guest)} only on the guest")
    if only_host or only_guest:
        with open(os.path.join(run_dir, f"diff-{label}.txt"), "w", encoding="utf-8") as f:
            f.write("only host:\n" + "\n".join(only_host) + "\n\nonly guest:\n" + "\n".join(only_guest) + "\n")
        for p in (only_host[:8] + only_guest[:8]):
            print("      ", "H" if p in only_host else "G", p)
        raise Fail(f"{label}: worlds differ ({len(only_host)} + {len(only_guest)} objects)")


def wait_trace(g: Bridge, needle: str, label: str, timeout_s: float):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        if needle in (g.eval("story.trace") or ""):
            return
        time.sleep(0.5)
    raise Fail(f"{label}: no {needle!r} in the event trace after {timeout_s}s")


def clickables(g: Bridge):
    out = g.exec("bot.clickables")["output"]
    return [tuple(line.split("\t")) for line in out.split("\n") if line]


def do_step(host: Bridge, kind: str, target: str):
    if kind == "invoke":
        r = host.exec("story.invoke " + target)
    else:
        if kind == "next":
            keys = [k for c, k, _ in clickables(host) if c == "dialogue"]
        else:
            keys = [k for _, k, _ in clickables(host) if k.endswith(target)]
        if len(keys) != 1:
            raise Fail(f"host: {len(keys)} clickables match {target or kind!r}")
        r = host.exec("bot.click " + keys[0])
    if not r.get("ok"):
        raise Fail(f"host: {r}")


def line_index(g: Bridge):
    r = g.exec("get Tamagotchi_Dialogue dialogueIndex")
    return r["output"] if r.get("ok") else None


def save_traces(host: Bridge, guest: Bridge, run_dir: str):
    for g, name in ((host, "host"), (guest, "guest")):
        with open(os.path.join(run_dir, f"{name}-trace.txt"), "w", encoding="utf-8") as f:
            f.write(g.eval("story.trace") or "")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", action="store_true", help="leave both games running")
    a = ap.parse_args()

    run_dir = os.path.join(ROOT, "local", "logs", "story-" + time.strftime("%Y%m%d-%H%M%S"))
    os.makedirs(run_dir, exist_ok=True)
    for slot in ("A", "B"):
        launch.stop(slot)
    result, detail = "FAIL", ""
    host = guest = None
    try:
        step("start both copies")
        launch.start("A", "-coop-host udp:27015 -coop-name Host")
        launch.start("B", "-coop-join udp:127.0.0.1:27015 -coop-name Guest")
        host = Bridge.connect(endpoint_from_file(os.path.join(ROOT, "local", "run", "A-bridge.json"), wait_s=120), wait_s=60)
        guest = Bridge.connect(endpoint_from_file(os.path.join(ROOT, "local", "run", "B-bridge.json"), wait_s=120), wait_s=60)
        boot_to_menu(host, "host")
        boot_to_menu(guest, "guest")
        wait(host, "net.connected", "host", 60)
        wait(guest, "net.connected", "guest", 30)

        step("host starts a new game, guest follows into the same epoch")
        host.exec("menu.newgame")
        wait(host, "scene", "host", 90, contains=LEVEL)
        wait(guest, "follow.instep", "guest", 120)
        print(f"    host epoch {host.eval('story.epoch')}, guest follows epoch {guest.eval('story.hostepoch')}")

        step("intro cutscene: guest replays the host's events")
        wait_trace(host, TUTORIAL_READY, "host", 90)
        n = caught_up(host, guest)
        print(f"    {n} events replayed, {guest.eval('story.suppressed')} of the guest's own suppressed, "
              f"{guest.eval('story.missing')} missing")
        check_world(host, guest, "after-intro", run_dir)

        for label, kind, target, settle in LEVEL1:
            step(label)
            do_step(host, kind, target)
            time.sleep(settle)
            n = caught_up(host, guest)
            extra = ""
            if kind == "next":
                hl, gl = line_index(host), line_index(guest)
                extra = f", line {hl} on the host, {gl} on the guest"
                if hl != gl:
                    raise Fail(f"{label}: the host shows line {hl}, the guest line {gl}")
            print(f"    {n} events replayed{extra}")
            check_world(host, guest, label.replace(" ", "-").replace(":", ""), run_dir)

        missing = guest.eval("story.missing")
        if missing:
            raise Fail(f"guest could not find {missing} of the host's events")
        step("evidence")
        for g, name in ((host, "host"), (guest, "guest")):
            g.screenshot(os.path.join(run_dir, f"{name}.png"))
        time.sleep(1.5)
        result = "PASS"
        detail = (f"{guest.eval('story.replayed')} events replayed, {guest.eval('story.suppressed')} suppressed, "
                  f"0 missing, worlds equal")
    except Fail as e:
        detail = str(e)
    except Exception as e:  # bridge errors, crashes
        detail = f"{type(e).__name__}: {e}"
    finally:
        if host and guest:
            try:
                save_traces(host, guest, run_dir)
            except Exception:
                pass
        for slot in ("A", "B"):
            src = os.path.join(ROOT, "local", "logs", f"{slot}-player.log")
            if os.path.exists(src):
                shutil.copy(src, run_dir)
            bep = os.path.join(ROOT, "local", "installs", slot, "BepInEx", "LogOutput.log")
            if os.path.exists(bep):
                shutil.copy(bep, os.path.join(run_dir, f"{slot}-bepinex.log"))
        if not a.keep:
            for slot in ("A", "B"):
                launch.stop(slot)
    print(f"RESULT: {result} (story) {detail}")
    print(f"logs: {run_dir}")
    sys.exit(0 if result == "PASS" else 1)


if __name__ == "__main__":
    main()
