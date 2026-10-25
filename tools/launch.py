"""Start and stop MiSide test installs (local/installs/<slot>) for co-op tests.

    python -I tools/launch.py start A [--extra "-coop-host udp:27015"]
    python -I tools/launch.py stop A
    python -I tools/launch.py status

Each copy runs windowed with its own Unity log, opens the in-game bridge on a free loopback
port and writes the endpoint to local/run/<slot>-bridge.json. Only processes started by this
script (pid file + exe path inside local/installs) are ever stopped.
"""
from __future__ import annotations

import argparse
import json
import os
import shlex
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
INSTALLS = os.path.join(ROOT, "local", "installs")
RUN = os.path.join(ROOT, "local", "run")
LOGS = os.path.join(ROOT, "local", "logs")


def _pid_file(slot: str) -> str:
    return os.path.join(RUN, f"{slot}.pid")


def _endpoint_file(slot: str) -> str:
    return os.path.join(RUN, f"{slot}-bridge.json")


def _exe_of(pid: int) -> str | None:
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command", f"(Get-Process -Id {pid} -ErrorAction SilentlyContinue).Path"],
        capture_output=True, text=True).stdout.strip()
    return out or None


def _ours(pid: int) -> bool:
    exe = _exe_of(pid)
    return bool(exe) and os.path.normcase(exe).startswith(os.path.normcase(INSTALLS))


def start(slot: str, extra: str = "", width: int = 960, height: int = 540) -> int:
    install = os.path.join(INSTALLS, slot)
    exe = os.path.join(install, "MiSideFull.exe")
    if not os.path.exists(exe):
        raise SystemExit(f"no test install {install}")
    os.makedirs(RUN, exist_ok=True)
    os.makedirs(LOGS, exist_ok=True)
    pf = _pid_file(slot)
    if os.path.exists(pf):
        old = int(open(pf).read().strip() or 0)
        if old and _ours(old):
            raise SystemExit(f"{slot} already running as pid {old}")
    for f in (_endpoint_file(slot),):
        if os.path.exists(f):
            os.remove(f)
    args = [exe, "-logFile", os.path.join(LOGS, f"{slot}-player.log"),
            "-screen-fullscreen", "0", "-screen-width", str(width), "-screen-height", str(height),
            "-coop-slot", slot, "-coop-bridge", "0", "-coop-bridge-file", _endpoint_file(slot)]
    args += shlex.split(extra, posix=False)
    proc = subprocess.Popen(args, cwd=install, creationflags=getattr(subprocess, "DETACHED_PROCESS", 0))
    with open(pf, "w") as f:
        f.write(str(proc.pid))
    print(json.dumps({"slot": slot, "pid": proc.pid, "endpointFile": _endpoint_file(slot)}))
    return proc.pid


def stop(slot: str, timeout_s: float = 10.0) -> None:
    pf = _pid_file(slot)
    if not os.path.exists(pf):
        print(f"{slot}: not started by us")
        return
    pid = int(open(pf).read().strip() or 0)
    if pid and _ours(pid):
        subprocess.run(["taskkill", "/PID", str(pid)], capture_output=True)
        deadline = time.time() + timeout_s
        while time.time() < deadline and _exe_of(pid):
            time.sleep(0.25)
        if _exe_of(pid):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
        print(f"{slot}: stopped pid {pid}")
    else:
        print(f"{slot}: pid {pid} not running (or not ours)")
    os.remove(pf)


def status() -> None:
    if not os.path.isdir(RUN):
        return
    for name in sorted(os.listdir(RUN)):
        if name.endswith(".pid"):
            slot = name[:-4]
            pid = int(open(os.path.join(RUN, name)).read().strip() or 0)
            print(f"{slot}: pid {pid} {'running' if pid and _ours(pid) else 'not running'}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["start", "stop", "status"])
    ap.add_argument("slot", nargs="?")
    ap.add_argument("--extra", default="")
    ap.add_argument("--width", type=int, default=960)
    ap.add_argument("--height", type=int, default=540)
    a = ap.parse_args()
    if a.action == "start":
        start(a.slot, a.extra, a.width, a.height)
    elif a.action == "stop":
        stop(a.slot)
    else:
        status()


if __name__ == "__main__":
    main()
