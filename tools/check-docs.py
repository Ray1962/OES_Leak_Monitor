#!/usr/bin/env python3
"""Check that every generated doc is current, and that the publish lists agree.

    python3 tools/check-docs.py          # report; exit 1 if anything is stale
    python3 tools/check-docs.py --fix    # regenerate the stale ones in place

1. Every tracked docs/*.md with a tracked .html beside it must regenerate, through
   tools/md2html.py, to exactly the committed .html. The fab reads the HTML, never the
   Markdown, so an edit that skipped regeneration ships the old text. Each document keeps
   the title already in its <title>, which is why --fix is the way to regenerate and the
   hand-typed command is not: a different title is a different file.
2. The operator docs the csproj copies into the publish folder (_OperatorDoc) must be the
   ones publish.cmd checks for. A doc added to one list and not the other either never
   ships or fails every publish.

Only pages md2html wrote are checked, recognised by the sidebar it always emits: an HTML
page written by hand is left alone even when a .md sits beside it (docs/EAP ceid-events.html
is one), since regenerating it would replace its own layout.
"""
import html
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import md2html  # noqa: E402

GENERATED = '<nav class="toc"><p class="tt">目錄</p>'


def tracked(pattern):
    out = subprocess.run(["git", "ls-files", "-z", pattern], cwd=ROOT,
                         capture_output=True, check=True).stdout.decode("utf-8")
    return {p for p in out.split("\0") if p}


def stale_docs(fix):
    htmls = tracked("docs/*.html")
    problems = []
    for md in sorted(tracked("docs/*.md")):
        page = md[:-3] + ".html"
        if page not in htmls:
            continue
        with open(os.path.join(ROOT, page), encoding="utf-8") as f:
            current = f.read()
        if GENERATED not in current:
            continue
        m = re.search(r"<title>(.*?)</title>", current)
        title = html.unescape(m.group(1)) if m else ""
        src = os.path.join(ROOT, md)
        with open(src, encoding="utf-8") as f:
            wanted = md2html.build(f.read(), title, os.path.dirname(src))
        if wanted == current:
            continue
        if fix:
            with open(os.path.join(ROOT, page), "w", encoding="utf-8", newline="\n") as f:
                f.write(wanted)
            problems.append(f"regenerated {page}")
        else:
            problems.append(f"{page} is stale: regenerate it from {md}")
    return problems


def publish_lists_disagree():
    with open(os.path.join(ROOT, "src/OES_Leak_Monitor/OES_Leak_Monitor.csproj"), encoding="utf-8") as f:
        shipped = set(re.findall(r'_OperatorDoc Include="[^"]*\\docs\\([^"\\]+\.html)"', f.read()))
    with open(os.path.join(ROOT, "publish.cmd"), encoding="utf-8", errors="replace") as f:
        checked = {name for line in f if not line.strip().lower().startswith("echo")
                   for name in re.findall(r"[\w.-]+\.html", line)}
    problems = []
    for name in sorted(shipped - checked):
        problems.append(f"{name} ships (_OperatorDoc) but publish.cmd does not check for it")
    for name in sorted(checked - shipped):
        problems.append(f"publish.cmd checks for {name} but the csproj does not ship it")
    return problems


if __name__ == "__main__":
    fix = "--fix" in sys.argv[1:]
    problems = stale_docs(fix) + publish_lists_disagree()
    for p in problems:
        print(p)
    failed = [p for p in problems if not p.startswith("regenerated ")]
    if not problems:
        print("docs: all generated HTML is current; publish lists agree")
    sys.exit(1 if failed else 0)
