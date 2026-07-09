#!/usr/bin/env python3
"""Measures every [data-id] element in a design-twin HTML page with real
Chromium layout and writes the same JSON schema the WPF SnapshotExporter
produces, so compare.py can diff them directly.

No Node/Playwright dependency (neither is installed in this environment —
see LEMOINE_UI.md skill notes). The page itself computes getBoundingClientRect()
for every [data-id] element on load and serializes the result into a hidden
<pre id="__measurements"> element; this script drives headless Chromium with
--dump-dom, which runs the page's <script> before dumping, then pulls the
JSON straight out of the dumped markup.

Usage:  python3 devtools/design-twin/measure.py <page.html> [out.json] [--png out.png]
"""
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

CHROME_CANDIDATES = list(Path("/opt/pw-browsers").glob("chromium-*/chrome-linux/chrome"))


def find_chrome():
    if not CHROME_CANDIDATES:
        sys.exit("measure.py: no Chromium binary found under /opt/pw-browsers/chromium-*/chrome-linux/chrome")
    return str(sorted(CHROME_CANDIDATES)[-1])


def dump_dom(chrome, url, window_size):
    with tempfile.TemporaryDirectory() as profile:
        result = subprocess.run(
            [chrome, "--headless", "--no-sandbox", f"--user-data-dir={profile}",
             f"--window-size={window_size}", "--virtual-time-budget=2000", "--dump-dom", url],
            capture_output=True, text=True, timeout=30,
        )
        return result.stdout


def screenshot(chrome, url, window_size, out_png, scale=2):
    with tempfile.TemporaryDirectory() as profile:
        subprocess.run(
            [chrome, "--headless", "--no-sandbox", f"--user-data-dir={profile}",
             f"--force-device-scale-factor={scale}", "--hide-scrollbars",
             f"--window-size={window_size}", f"--screenshot={out_png}", url],
            capture_output=True, text=True, timeout=30,
        )


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    page = Path(sys.argv[1]).resolve()
    if not page.exists():
        sys.exit(f"measure.py: {page} does not exist")

    out_json = Path(sys.argv[2]) if len(sys.argv) > 2 and not sys.argv[2].startswith("--") else \
        page.with_suffix(".measured.json")

    png_out = None
    if "--png" in sys.argv:
        png_out = sys.argv[sys.argv.index("--png") + 1]

    chrome = find_chrome()
    url = f"file://{page}"
    dom = dump_dom(chrome, url, "1180,780")

    m = re.search(r'<pre id="__measurements"[^>]*>(.*?)</pre>', dom, re.DOTALL)
    if not m:
        sys.exit(f"measure.py: {page} produced no #__measurements element — "
                  "is the page's measurement <script> present and running?")

    raw = m.group(1)
    # --dump-dom HTML-escapes the JSON text content; undo the handful of
    # entities that can appear in our own JSON (quotes, amp, lt/gt).
    raw = (raw.replace("&quot;", '"').replace("&#34;", '"')
              .replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">"))
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as ex:
        sys.exit(f"measure.py: could not parse measurements JSON from {page}: {ex}")

    out_json.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    print(f"measure.py: wrote {out_json} ({len(data.get('elements', {}))} elements)")

    if png_out:
        screenshot(chrome, url, "1180,780", png_out)
        print(f"measure.py: wrote {png_out}")


if __name__ == "__main__":
    main()
