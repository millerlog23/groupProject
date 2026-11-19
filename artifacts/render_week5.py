"""Render the current Week 5 README section as a self-contained HTML and PNG."""

import base64
import re
import subprocess
import tempfile
from pathlib import Path
from urllib.parse import unquote, urlparse

from markdown_it import MarkdownIt
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "artifacts"
BROWSER = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
WIDTH = 1280

readme = (ROOT / "README.md").read_text(encoding="utf-8-sig")
match = re.search(r"^## Week 5\s*$", readme, re.MULTILINE)
if not match:
    raise RuntimeError("Week 5 was not found in README.md")
section = readme[match.start():]
next_section = re.search(r"^## (?!Week 5\s*$).+", section[1:], re.MULTILINE)
if next_section:
    section = section[:next_section.start() + 1]


def local_image(match):
    alt, source = match.groups()
    filename = Path(unquote(urlparse(source).path)).name
    path = ROOT / "Markdown Images" / "Week 5" / filename
    if not path.is_file():
        raise FileNotFoundError(f"Missing README image: {filename}")
    data = base64.b64encode(path.read_bytes()).decode("ascii")
    return f"![{alt}](data:image/png;base64,{data})"


section = re.sub(r"!\[([^\]]*)\]\(([^)]+)\)", local_image, section)
content = MarkdownIt("commonmark", {"html": False}).render(section)
html = """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>ToTrak — Week 5</title>
<style>
* { box-sizing: border-box; }
html, body { margin: 0; background: #ffffff; color: #1f2328; }
body { width: 1280px; font: 18px/1.6 -apple-system, BlinkMacSystemFont,
       "Segoe UI", Arial, sans-serif; }
main { width: 1152px; margin: 0 auto; padding: 42px 0 48px; }
h2, h3, h4 { font-weight: 600; line-height: 1.25; }
h2 { font-size: 32px; border-bottom: 1px solid #d1d9e0;
     padding-bottom: 12px; margin: 0 0 26px; }
h3 { font-size: 25px; margin: 30px 0 17px; }
h4 { font-size: 21px; margin: 30px 0 16px; }
p { margin: 0 0 18px; }
img { display: block; max-width: 100%; height: auto; margin: 10px 0 25px; }
main > :last-child { margin-bottom: 0; }
</style></head><body><main>""" + content + """</main>
<script>
async function measure() {
  await Promise.all(Array.from(document.images).map(image => image.decode()));
  await document.fonts.ready;
  document.body.dataset.renderHeight = Math.ceil(document.querySelector('main').getBoundingClientRect().height);
  document.body.dataset.imageCount = document.images.length;
}
measure();
</script></body></html>
"""

OUTPUT.mkdir(exist_ok=True)
html_path = OUTPUT / "week5-readme.html"
png_path = OUTPUT / "week5-readme.png"
html_path.write_text(html, encoding="utf-8")
profile = Path(tempfile.mkdtemp(prefix="totrak-week5-render-"))
flags = [
    str(BROWSER), "--headless", "--disable-gpu", "--no-first-run",
    "--no-default-browser-check", "--disable-background-networking",
    "--disable-background-mode", "--hide-scrollbars",
    "--force-device-scale-factor=1", f"--user-data-dir={profile}",
    "--virtual-time-budget=2500",
]


def run_browser(*arguments):
    result = subprocess.run(
        flags + list(arguments), capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=55,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    if result.returncode:
        raise RuntimeError(f"Browser render failed: {result.stderr[-2000:]}")
    return result.stdout


dom = run_browser(f"--window-size={WIDTH},900", "--dump-dom", html_path.as_uri())
height_match = re.search(r'data-render-height="(\d+)"', dom)
if not height_match or 'data-image-count="7"' not in dom:
    raise RuntimeError("Document did not finish loading all seven images")
height = int(height_match.group(1))
run_browser(
    f"--window-size={WIDTH},{height}", f"--screenshot={png_path}", html_path.as_uri()
)
with Image.open(png_path) as picture:
    if picture.size != (WIDTH, height):
        raise RuntimeError(f"Unexpected image dimensions: {picture.size}")
print(f"Rendered all seven screenshots: {png_path} ({WIDTH} x {height})")
print(f"Self-contained HTML: {html_path}")
