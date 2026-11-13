#!/usr/bin/env python3
"""Packs branding/png/kna-*.png into the icon files used by every edition.

  branding/kna.ico   Windows: exe icon (desktop + server) and window icon
  branding/kna.icns  macOS: .app bundle icon
  copies into src/KnA.App/Assets and src/KnA.Server/wwwroot
"""
import shutil
import struct
from pathlib import Path

HERE = Path(__file__).resolve().parent
PNG = HERE / "png"
ROOT = HERE.parent


def png(size: int) -> bytes:
    return (PNG / f"kna-{size}.png").read_bytes()


def write_ico(path: Path, sizes: list[int]) -> None:
    # ICO with PNG-compressed entries (supported since Windows Vista).
    entries = [(s, png(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    directory, blobs = b"", b""
    for s, data in entries:
        dim = 0 if s >= 256 else s
        directory += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data
    path.write_bytes(header + directory + blobs)


def write_icns(path: Path) -> None:
    # PNG payloads: ic07 128, ic08 256, ic09 512, ic10 1024 (512@2x), ic11 32 (16@2x),
    # ic12 64 (32@2x), ic13 256 (128@2x), ic14 512 (256@2x), icp4 16, icp5 32.
    chunks = [("icp4", 16), ("icp5", 32), ("ic11", 32), ("ic12", 64), ("ic07", 128),
              ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024)]
    body = b"".join(t.encode() + struct.pack(">I", 8 + len(png(s))) + png(s) for t, s in chunks)
    path.write_bytes(b"icns" + struct.pack(">I", 8 + len(body)) + body)


def main() -> None:
    write_ico(HERE / "kna.ico", [16, 20, 24, 32, 40, 48, 64, 128, 256])
    write_icns(HERE / "kna.icns")
    assets = ROOT / "src" / "KnA.App" / "Assets"
    www = ROOT / "src" / "KnA.Server" / "wwwroot"
    assets.mkdir(parents=True, exist_ok=True)
    shutil.copy(HERE / "kna.ico", assets / "kna.ico")
    shutil.copy(PNG / "kna-256.png", assets / "kna-256.png")
    shutil.copy(HERE / "kna.ico", ROOT / "src" / "KnA.Server" / "kna.ico")
    shutil.copy(HERE / "kna.svg", www / "favicon.svg")
    shutil.copy(PNG / "kna-180.png", www / "apple-touch-icon.png")
    shutil.copy(PNG / "kna-192.png", www / "icon-192.png")
    shutil.copy(PNG / "kna-512.png", www / "icon-512.png")
    print("ico, icns and copies written")


if __name__ == "__main__":
    main()
