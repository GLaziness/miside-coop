"""Every co-op test on this PC, one after another (each takes a few minutes).

    python -I tools/test_all.py

Runs the pair test over direct UDP and over the mock Steam, then the story test, and prints one
line per test and RESULT: PASS only if all of them passed.
"""
from __future__ import annotations

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TESTS = [
    ("pair over UDP", ["test_pair.py", "--transport", "udp"]),
    ("pair over mock Steam", ["test_pair.py", "--transport", "mocksteam"]),
    ("story, level 1", ["test_story.py"]),
]


def main():
    results = []
    for label, args in TESTS:
        print(f"=== {label}", flush=True)
        proc = subprocess.run([sys.executable, "-I", os.path.join(HERE, args[0])] + args[1:],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")
        lines = [l for l in proc.stdout.splitlines() if l.startswith(("RESULT:", "logs:"))]
        print("\n".join(lines) or proc.stdout[-2000:] + proc.stderr[-2000:], flush=True)
        results.append((label, proc.returncode == 0))
    print()
    for label, ok in results:
        print(f"{'PASS' if ok else 'FAIL'}  {label}")
    ok = all(ok for _, ok in results)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(ok for _, ok in results)}/{len(results)})")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
