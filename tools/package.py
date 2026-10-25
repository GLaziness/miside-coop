"""Build the player release: dist/MiSideCoop-<version>.zip.

    python -I tools/package.py [--no-build]

The zip holds one folder with install.bat, uninstall.bat, uninstall-all.bat, setup.ps1, the
README, the third-party notices and game/, which mirrors the game folder: BepInEx from the
pinned zip in local/downloads (downloaded from builds.bepinex.dev if missing, checked by
SHA-256) and the plugin from a Release build.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PROJECT = os.path.join(ROOT, "src", "MiSideCoop", "MiSideCoop.csproj")
BUILD_OUT = os.path.join(ROOT, "src", "MiSideCoop", "bin", "Release", "net6.0")
PACKAGING = os.path.join(ROOT, "packaging")
DIST = os.path.join(ROOT, "dist")

BEPINEX_ZIP = os.path.join(ROOT, "local", "downloads", "bepinex-be755",
                           "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+3fab71a.zip")
BEPINEX_URL = ("https://builds.bepinex.dev/projects/bepinex_be/755/"
               "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip")
BEPINEX_SHA256 = "3616d6a67f5f595973ec4aa7bd7edaf7f799d5bb9926f7146a6dcc7b4abf478f"
BEPINEX_SKIP = {"changelog.txt"}
PLUGIN_FILES = ["MiSideCoop.dll", "MiSideCoop.pdb", "LiteNetLib.dll"]
BATS = {
    "install.bat": "Install",
    "uninstall.bat": "Uninstall",
    "uninstall-all.bat": "UninstallAll",
}


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def version() -> str:
    text = open(os.path.join(ROOT, "src", "MiSideCoop", "Plugin.cs"), encoding="utf-8").read()
    m = re.search(r'const string Version = "([^"]+)"', text)
    if not m:
        raise SystemExit("no Version in Plugin.cs")
    return m.group(1)


def build() -> None:
    subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "-nologo", "-v", "q"], check=True)


def fetch_bepinex() -> None:
    if os.path.exists(BEPINEX_ZIP):
        return
    os.makedirs(os.path.dirname(BEPINEX_ZIP), exist_ok=True)
    tmp = BEPINEX_ZIP + ".part"
    print(f"downloading {BEPINEX_URL}")
    # builds.bepinex.dev answers 403 to the default Python-urllib user agent.
    req = urllib.request.Request(BEPINEX_URL, headers={"User-Agent": "miside-coop-package"})
    with urllib.request.urlopen(req, timeout=60) as r, open(tmp, "wb") as f:
        shutil.copyfileobj(r, f)
    if sha256(tmp) != BEPINEX_SHA256:
        os.remove(tmp)
        raise SystemExit("the downloaded BepInEx zip has a different SHA-256 than the pinned one")
    os.replace(tmp, BEPINEX_ZIP)


def extract_bepinex(game: str) -> int:
    fetch_bepinex()
    if sha256(BEPINEX_ZIP) != BEPINEX_SHA256:
        raise SystemExit(f"{BEPINEX_ZIP}: SHA-256 differs from the pinned one")
    n = 0
    with zipfile.ZipFile(BEPINEX_ZIP) as z:
        for info in z.infolist():
            name = info.filename
            if name in BEPINEX_SKIP:
                continue
            parts = name.replace("\\", "/").split("/")
            if name.startswith(("/", "\\")) or ":" in name or ".." in parts:
                raise SystemExit(f"unsafe path in the BepInEx zip: {name}")
            out = os.path.join(game, *parts)
            if info.is_dir():
                os.makedirs(out, exist_ok=True)
                continue
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with z.open(info) as src, open(out, "wb") as dst:
                shutil.copyfileobj(src, dst)
            n += 1
    return n


def write_text(path: str, text: str, *, bom: bool = False, crlf: bool = False) -> None:
    text = text.replace("\r\n", "\n")
    if crlf:
        text = text.replace("\n", "\r\n")
    with open(path, "w", encoding="utf-8-sig" if bom else "utf-8", newline="") as f:
        f.write(text)


def zip_dir(folder: str, zip_path: str) -> None:
    base = os.path.dirname(folder)
    entries = []
    for dirpath, dirnames, filenames in os.walk(folder):
        dirnames.sort()
        rel_dir = os.path.relpath(dirpath, base).replace(os.sep, "/")
        if not filenames and not dirnames:
            entries.append((rel_dir + "/", None))
        for name in sorted(filenames):
            entries.append((f"{rel_dir}/{name}", os.path.join(dirpath, name)))
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for arc, path in entries:
            if path is None:
                z.writestr(zipfile.ZipInfo(arc, date_time=(2026, 1, 1, 0, 0, 0)), b"")
            else:
                z.write(path, arc)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-build", action="store_true", help="use the existing Release build")
    a = ap.parse_args()

    ver = version()
    if not a.no_build:
        build()
    name = f"MiSideCoop-{ver}"
    out = os.path.join(DIST, name)
    if os.path.exists(out):
        shutil.rmtree(out)
    game = os.path.join(out, "game")
    os.makedirs(game)

    n = extract_bepinex(game)
    plugin = os.path.join(game, "BepInEx", "plugins", "MiSideCoop")
    os.makedirs(plugin, exist_ok=True)
    for f in PLUGIN_FILES:
        shutil.copy2(os.path.join(BUILD_OUT, f), plugin)

    write_text(os.path.join(out, "setup.ps1"),
               open(os.path.join(PACKAGING, "setup.ps1"), encoding="utf-8-sig").read(), bom=True, crlf=True)
    for bat, action in BATS.items():
        write_text(os.path.join(out, bat),
                   "@echo off\n"
                   f'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" -Action {action} %*\n'
                   "pause\n", crlf=True)
    write_text(os.path.join(out, "README.txt"),
               open(os.path.join(ROOT, "README.md"), encoding="utf-8").read(), bom=True, crlf=True)
    write_text(os.path.join(out, "THIRD-PARTY-NOTICES.txt"),
               open(os.path.join(PACKAGING, "THIRD-PARTY-NOTICES.txt"), encoding="utf-8").read(), crlf=True)

    zip_path = os.path.join(DIST, name + ".zip")
    if os.path.exists(zip_path):
        os.remove(zip_path)
    zip_dir(out, zip_path)
    print(f"{zip_path}: {os.path.getsize(zip_path)} bytes, sha256 {sha256(zip_path)}")
    print(f"BepInEx files: {n}, plugin: {', '.join(PLUGIN_FILES)} "
          f"(MiSideCoop.dll sha256 {sha256(os.path.join(plugin, 'MiSideCoop.dll'))})")


if __name__ == "__main__":
    sys.exit(main())
