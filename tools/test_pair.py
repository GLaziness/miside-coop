"""Two-client co-op test on one PC: host in test install A, guest in B.

    python -I tools/test_pair.py                 # direct UDP on loopback
    python -I tools/test_pair.py --transport mocksteam

Checks, in order: both copies boot to the menu, the session connects, the host starts a new game,
the guest follows into the same level, each side shows the other's puppet, and a puppet follows
its player when the player moves. Prints RESULT: PASS or RESULT: FAIL with the failing step.
Screenshots and logs go to local/logs/pair-<time>/.
"""
from __future__ import annotations

import argparse
import json
import math
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

LEVEL = "Scene 1 - RealRoom"


class Fail(Exception):
    pass


def step(name):
    print(f"--- {name}", flush=True)


def vec(text):
    return [float(x) for x in text.split()] if text else None


def dist(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def boot_to_menu(g: Bridge, label: str, timeout_s: float = 120):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        scene = g.eval("scene")
        if scene == "SceneMenu":
            return
        if scene == "SceneLoading":
            # first start of a fresh copy: the language screen waits for "Continue"
            g.exec("call ChangeLanguageStart Apply")
        time.sleep(1)
    raise Fail(f"{label}: did not reach the menu")


def wait(g: Bridge, expr: str, label: str, timeout_s: float, **kw):
    r = g.wait_for(expr=expr, timeout_ms=int(timeout_s * 1000), **kw)
    if not r.get("satisfied"):
        raise Fail(f"{label}: {expr} {kw} not true after {timeout_s}s (last {r.get('value')!r})")
    return r.get("value")


def check_puppet_follows(mover: Bridge, viewer: Bridge, label: str, points):
    worst = 0.0
    for p in points:
        mover.exec("tp " + " ".join(f"{c:.3f}" for c in p))
        time.sleep(0.6)
        actual = vec(mover.eval("player.pos"))
        seen = vec(viewer.eval("puppet.pos"))
        if seen is None:
            raise Fail(f"{label}: no puppet on the viewer")
        d = dist(actual, seen)
        worst = max(worst, d)
        print(f"    {label}: player {actual} puppet {seen} off by {d:.3f} m")
    if worst > 0.25:
        raise Fail(f"{label}: puppet off by {worst:.2f} m (limit 0.25)")
    return worst


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--transport", choices=["udp", "mocksteam"], default="udp")
    ap.add_argument("--keep", action="store_true", help="leave both games running")
    a = ap.parse_args()

    run_dir = os.path.join(ROOT, "local", "logs", "pair-" + time.strftime("%Y%m%d-%H%M%S"))
    os.makedirs(run_dir, exist_ok=True)
    mock_dir = os.path.join(ROOT, "local", "run", "mocksteam")
    if a.transport == "udp":
        host_args = "-coop-host udp:27015 -coop-name Host"
        guest_args = "-coop-join udp:127.0.0.1:27015 -coop-name Guest"
    else:
        shutil.rmtree(mock_dir, ignore_errors=True)
        host_args = f"-coop-mocksteam 1 -coop-mocksteam-dir {mock_dir} -coop-host steam -coop-name Host"
        guest_args = f"-coop-mocksteam 2 -coop-mocksteam-dir {mock_dir} -coop-join steam:first -coop-name Guest"

    for slot in ("A", "B"):
        launch.stop(slot)
    result = "FAIL"
    detail = ""
    try:
        step("start both copies")
        launch.start("A", host_args)
        launch.start("B", guest_args)
        host = Bridge.connect(endpoint_from_file(os.path.join(ROOT, "local", "run", "A-bridge.json"), wait_s=120), wait_s=60)
        guest = Bridge.connect(endpoint_from_file(os.path.join(ROOT, "local", "run", "B-bridge.json"), wait_s=120), wait_s=60)

        step("boot to the menu")
        boot_to_menu(host, "host")
        boot_to_menu(guest, "guest")

        step("session connects")
        wait(host, "net.connected", "host", 60)
        wait(guest, "net.connected", "guest", 30)
        print("    host sees", host.eval("net.peer"), "| guest sees", guest.eval("net.peer"))

        step("host starts a new game")
        host.exec("menu.newgame")
        wait(host, "scene", "host", 90, contains=LEVEL)
        wait(host, "player.found", "host", 120)

        step("guest follows into the level")
        wait(guest, "scene", "guest", 90, contains=LEVEL)
        wait(guest, "player.found", "guest", 120)

        step("puppets appear")
        wait(host, "puppet.exists", "host", 30)
        wait(guest, "puppet.exists", "guest", 30)

        step("host moves, guest sees it")
        hp = vec(host.eval("player.pos"))
        ring = [[hp[0] + 0.8 * math.cos(t), hp[1], hp[2] + 0.8 * math.sin(t)] for t in (0, 1.6, 3.2, 4.8)]
        w1 = check_puppet_follows(host, guest, "host->guest", ring)

        step("guest moves, host sees it")
        gp = vec(guest.eval("player.pos"))
        ring = [[gp[0] + 0.6 * math.cos(t), gp[1], gp[2] + 0.6 * math.sin(t)] for t in (0.5, 2.5, 4.5)]
        w2 = check_puppet_follows(guest, host, "guest->host", ring)

        step("evidence")
        # stand the two players near each other, facing each other, so each screenshot shows the other
        host.exec("tp " + " ".join(f"{c:.3f}" for c in hp))
        guest.exec("tp " + " ".join(f"{c:.3f}" for c in (hp[0] + 1.2, hp[1], hp[2])))
        time.sleep(1)
        hpos, gpos = vec(host.eval("player.pos")), vec(guest.eval("player.pos"))
        host.exec(f"face {gpos[0]:.3f} {gpos[1] + 1.4:.3f} {gpos[2]:.3f}")
        guest.exec(f"face {hpos[0]:.3f} {hpos[1] + 1.4:.3f} {hpos[2]:.3f}")
        time.sleep(1)
        for g, name in ((host, "host"), (guest, "guest")):
            g.screenshot(os.path.join(run_dir, f"{name}.png"))
            with open(os.path.join(run_dir, f"{name}-state.json"), "w", encoding="utf-8") as f:
                json.dump(g.state(), f, ensure_ascii=False, indent=1)
        time.sleep(1.5)
        # the co-op menu (F7) as each player sees it
        for g, name in ((host, "host"), (guest, "guest")):
            g.exec("menu.toggle")
        time.sleep(0.5)
        for g, name in ((host, "host"), (guest, "guest")):
            g.screenshot(os.path.join(run_dir, f"{name}-menu.png"))
        time.sleep(1.5)
        for g, name in ((host, "host"), (guest, "guest")):
            g.exec("menu.toggle")
        result = "PASS"
        detail = f"puppet error max {max(w1, w2):.3f} m, rtt host {host.eval('net.rtt'):.0f} ms"
    except Fail as e:
        detail = str(e)
    except Exception as e:  # bridge errors, crashes
        detail = f"{type(e).__name__}: {e}"
    finally:
        for slot in ("A", "B"):
            for name in (f"{slot}-player.log",):
                src = os.path.join(ROOT, "local", "logs", name)
                if os.path.exists(src):
                    shutil.copy(src, run_dir)
            bep = os.path.join(ROOT, "local", "installs", slot, "BepInEx", "LogOutput.log")
            if os.path.exists(bep):
                shutil.copy(bep, os.path.join(run_dir, f"{slot}-bepinex.log"))
        if not a.keep:
            for slot in ("A", "B"):
                launch.stop(slot)
    print(f"RESULT: {result} ({a.transport}) {detail}")
    print(f"logs: {run_dir}")
    sys.exit(0 if result == "PASS" else 1)


if __name__ == "__main__":
    main()
