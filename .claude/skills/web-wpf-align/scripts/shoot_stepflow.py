#!/usr/bin/env python3
"""Render a Lemoine step-flow web tool standalone and screenshot every step.

Feeds a spec JSON (the same shape a <Tool>WebTool.cs sends over the bridge as
'init') into the real shell (Source/Web/lib/stepflow.js + lemoine.css), then
drives headless Chromium once per step, clicking the active step's Confirm
button to advance. Pure stdlib.

Usage:
  python3 shoot_stepflow.py --spec spec.json --out outdir \
      [--web-root /path/to/repo/Source/Web] [--width 900] [--height 1200]

Outputs outdir/step-0.png .. step-N.png (N = number of steps - 1) and
outdir/driver.html (kept for debugging).

Notes:
- Renders in the DarkMono CSS-variable fallback theme (lemoine.css :root).
  Compare geometry/inputs against WPF screenshots, not raw colors, unless the
  WPF screenshot is also DarkMono.
- Validation is forced all-true so Confirm buttons are clickable and no
  "Required" hints pollute the shots. Steps with "hidden": true in the spec
  are skipped by the shell itself, matching stepHidden behavior.
"""
import argparse
import glob
import json
import os
import subprocess
import sys


def find_chromium():
    for pattern in ("/opt/pw-browsers/chromium-*/chrome-linux/chrome",
                    "/opt/pw-browsers/chromium/chrome-linux/chrome"):
        hits = sorted(glob.glob(pattern))
        if hits:
            return hits[-1]
    for name in ("chromium", "chromium-browser", "google-chrome"):
        from shutil import which
        p = which(name)
        if p:
            return p
    sys.exit("No Chromium found (looked in /opt/pw-browsers and PATH)")


DRIVER = """<!doctype html>
<html><head><meta charset="utf-8">
<link rel="stylesheet" href="file://{web_root}/lib/lemoine.css">
</head><body>
<div id="app" class="l-flow"></div>
<script src="file://{web_root}/lemoine-bridge.js"></script>
<script src="file://{web_root}/lib/lemoine.js"></script>
<script src="file://{web_root}/lib/stepflow.js"></script>
<script>
var SPEC = {spec_json};
var sf = Lemoine.stepflow(document.getElementById('app'), {{ send: function () {{}} }});
sf.init(SPEC);
var v = {{ steps: {{}}, canRun: true }};
(SPEC.steps || []).forEach(function (s) {{ v.steps[s.id] = true; }});
sf.applyValidation(v);
// ?step=k advances k times by clicking the active step's Confirm button.
var k = parseInt((location.search.match(/step=(\\d+)/) || [0, '0'])[1], 10);
for (var i = 0; i < k; i++) {{
  var btn = document.querySelector('.l-step.active .confirm-row .l-btn');
  if (btn) btn.click();
}}
</script></body></html>
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--spec", required=True, help="spec JSON file (init payload shape)")
    ap.add_argument("--out", required=True, help="output directory for PNGs")
    ap.add_argument("--web-root", default=None,
                    help="Source/Web directory (default: auto from git root)")
    ap.add_argument("--width", type=int, default=900)
    ap.add_argument("--height", type=int, default=820,
                    help="raise for tall steps so nothing is cut off")
    args = ap.parse_args()

    web_root = args.web_root
    if not web_root:
        try:
            top = subprocess.check_output(
                ["git", "rev-parse", "--show-toplevel"], text=True).strip()
            web_root = os.path.join(top, "Source", "Web")
        except subprocess.CalledProcessError:
            sys.exit("--web-root not given and not inside a git repo")
    web_root = os.path.abspath(web_root)
    if not os.path.isfile(os.path.join(web_root, "lib", "stepflow.js")):
        sys.exit(f"{web_root} does not look like Source/Web (no lib/stepflow.js)")

    with open(args.spec, encoding="utf-8") as f:
        spec = json.load(f)
    steps = [s for s in spec.get("steps", []) if not s.get("hidden")]
    if not steps:
        sys.exit("spec has no visible steps")

    os.makedirs(args.out, exist_ok=True)
    driver = os.path.join(os.path.abspath(args.out), "driver.html")
    with open(driver, "w", encoding="utf-8") as f:
        f.write(DRIVER.format(web_root=web_root,
                              spec_json=json.dumps(spec, ensure_ascii=True)))

    chrome = find_chromium()
    for k in range(len(steps)):
        png = os.path.join(args.out, f"step-{k}.png")
        cmd = [chrome, "--headless=new", "--no-sandbox", "--disable-gpu",
               "--hide-scrollbars", "--force-device-scale-factor=2",
               f"--window-size={args.width},{args.height}",
               f"--screenshot={png}", f"file://{driver}?step={k}"]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if not os.path.isfile(png):
            sys.exit(f"step {k} failed:\n{r.stderr[-2000:]}")
        print(f"wrote {png}  ({steps[k].get('title', steps[k].get('id'))})")


if __name__ == "__main__":
    main()
