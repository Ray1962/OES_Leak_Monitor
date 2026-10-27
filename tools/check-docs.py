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
3. The package table in docs/dev/nuget-packages.md must name the version the app's csproj
   references, and list every in-house package (Aqst.*, Aqusen.*) it references. The
   table is what someone reads to learn which version to pack and copy into the local
   feed; it said Aqusen.Secs 0.8.0 for as long as anyone remembered to check.

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


PACKAGE_TABLE = "docs/dev/nuget-packages.md"
APP_CSPROJ = "src/OES_Leak_Monitor/OES_Leak_Monitor.csproj"
IN_HOUSE = ("Aqst.", "Aqusen.")


def package_table_disagrees():
    with open(os.path.join(ROOT, APP_CSPROJ), encoding="utf-8") as f:
        referenced = dict(re.findall(r'<PackageReference Include="([^"]+)"\s+Version="([^"]+)"', f.read()))
    with open(os.path.join(ROOT, PACKAGE_TABLE), encoding="utf-8") as f:
        listed = dict(re.findall(r"^\| `([\w.]+)` \| ([^ |]+) \|", f.read(), re.MULTILINE))
    problems = []
    for name, version in sorted(listed.items()):
        if name not in referenced:
            problems.append(f"{PACKAGE_TABLE} lists {name} {version}, which {APP_CSPROJ} does not reference")
        elif referenced[name] != version:
            problems.append(f"{PACKAGE_TABLE} says {name} {version}; {APP_CSPROJ} references {referenced[name]}")
    for name in sorted(n for n in referenced if n.startswith(IN_HOUSE) and n not in listed):
        problems.append(f"{APP_CSPROJ} references in-house package {name} {referenced[name]}, "
                        f"which the table in {PACKAGE_TABLE} does not list")
    return problems


if __name__ == "__main__":
    fix = "--fix" in sys.argv[1:]
    problems = stale_docs(fix) + publish_lists_disagree() + package_table_disagrees()
    for p in problems:
        print(p)
    failed = [p for p in problems if not p.startswith("regenerated ")]
    if not problems:
        print("docs: all generated HTML is current; publish lists agree; package table matches the csproj")
    sys.exit(1 if failed else 0)
