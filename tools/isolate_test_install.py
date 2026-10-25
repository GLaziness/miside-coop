"""Give a MiSide test install its own saves, PlayerPrefs and Player.log.

Unity derives LocalLow\\<company>\\<product> and HKCU\\Software\\<company>\\<product>
from the product name in globalgamemanagers. Renaming the product of a test install
("MiSideFull" -> "MiSideTstA", same length so the serialized file keeps its layout)
moves every save, setting and log of that copy away from the real ones.

globalgamemanagers is hardlinked to the real install by setup-test-installs.ps1, so the
link is broken first: the real file is never written.

Usage: python -I isolate_test_install.py <install dir> <one letter slot, e.g. A>
"""
import os
import shutil
import struct
import sys

ORIGINAL = b"MiSideFull"


def patch(install: str, slot: str) -> None:
    if len(slot) != 1 or not slot.isalnum():
        raise SystemExit("slot must be one letter or digit")
    new_name = ("MiSideTst" + slot).encode("ascii")
    assert len(new_name) == len(ORIGINAL)

    ggm = os.path.join(install, "MiSideFull_Data", "globalgamemanagers")
    data = bytearray(open(ggm, "rb").read())
    hits = [i for i in range(len(data)) if data.startswith(ORIGINAL, i)
            and i >= 4 and struct.unpack_from("<i", data, i - 4)[0] == len(ORIGINAL)]
    already = data.find(new_name) >= 0
    if not hits and already:
        print(f"{install}: already isolated as {new_name.decode()}")
    elif len(hits) != 1:
        raise SystemExit(f"expected one length-prefixed product name, found {len(hits)}")
    else:
        data[hits[0]:hits[0] + len(ORIGINAL)] = new_name
        tmp = ggm + ".tmp"
        with open(tmp, "wb") as f:
            f.write(data)
        os.remove(ggm)  # drops this copy's hardlink only; the real install keeps its file
        shutil.move(tmp, ggm)
        print(f"{install}: product name -> {new_name.decode()} at offset {hits[0]}")

    app_info = os.path.join(install, "MiSideFull_Data", "app.info")
    with open(app_info, "w", encoding="ascii", newline="\n") as f:
        f.write("AIHASTO\n" + new_name.decode())


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    patch(sys.argv[1], sys.argv[2])
