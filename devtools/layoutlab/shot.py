#!/usr/bin/env python3
"""Rasterise an SVG to PNG with the pre-installed headless Chromium.

Used only to preview a render in chat; the SVG itself is the deliverable.
"""
import os
import subprocess
import sys
import glob


def find_chrome():
    for pat in ("/opt/pw-browsers/chromium-*/chrome-linux/chrome",
                "/opt/pw-browsers/chromium_headless_shell-*/chrome-linux/headless_shell",
                "/opt/pw-browsers/chromium/chrome-linux/chrome"):
        hits = sorted(glob.glob(pat))
        if hits:
            return hits[-1]
    raise SystemExit("no chromium found under /opt/pw-browsers")


def shoot(svg_path, png_path=None, width=None, height=None):
    svg_path = os.path.abspath(svg_path)
    png_path = png_path or svg_path.rsplit(".", 1)[0] + ".png"
    if width is None or height is None:
        head = open(svg_path).read(400)
        import re
        w = re.search(r'width="(\d+)"', head)
        h = re.search(r'height="(\d+)"', head)
        width = int(w.group(1)) if w else 1600
        height = int(h.group(1)) if h else 1200
    subprocess.run([
        find_chrome(), "--headless", "--disable-gpu", "--no-sandbox",
        "--hide-scrollbars", "--default-background-color=ffffff",
        f"--screenshot={png_path}",
        f"--window-size={width},{height}",
        f"file://{svg_path}",
    ], check=True, capture_output=True)
    return png_path


if __name__ == "__main__":
    print(shoot(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None))
