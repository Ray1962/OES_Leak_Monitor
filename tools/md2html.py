#!/usr/bin/env python3
"""Minimal Markdown -> standalone HTML, for the operator manual in docs/.

Regenerate the manual after editing the Markdown:

    python3 tools/md2html.py docs/user-manual-zh-TW.md \\
                             docs/user-manual-zh-TW.html "OES Leak Monitor 操作手冊"

Covers exactly the subset that file uses: ATX headings, GitHub tables, fenced
code, blockquotes, ordered/unordered lists (one nesting level), horizontal
rules, and inline bold / code / links. Anchor ids follow GitHub's slug rules so
the hand-written table-of-contents links keep working. There is no Markdown
library in this environment (no pandoc, no python-markdown) and the manual is
the only input, so this stays deliberately small rather than growing into a
general-purpose converter — if a new construct is needed, add it here.

Output is one self-contained file: inlined CSS, a sticky section list, a dark
-mode media query, and a print stylesheet. No external requests, so it can be
copied to a machine with no network and opened directly.
"""
import html
import re
import sys

# ---------------------------------------------------------------- inline

_INLINE_CODE = re.compile(r"`([^`]+)`")
_BOLD = re.compile(r"\*\*([^*]+)\*\*")
_LINK = re.compile(r"\[([^\]]+)\]\(([^)]+)\)")


def inline(text):
    """Escape, then re-introduce the inline constructs.

    Code spans are pulled out first and restored last so their contents are
    never treated as bold/link markup.
    """
    spans = []

    def stash(m):
        spans.append(m.group(1))
        return f"\x00{len(spans) - 1}\x00"

    text = _INLINE_CODE.sub(stash, text)
    text = html.escape(text, quote=False)
    text = _BOLD.sub(r"<strong>\1</strong>", text)
    text = _LINK.sub(r'<a href="\2">\1</a>', text)

    def restore(m):
        return "<code>" + html.escape(spans[int(m.group(1))], quote=False) + "</code>"

    return re.sub(r"\x00(\d+)\x00", restore, text)


def slug(text):
    """GitHub-compatible heading anchor."""
    s = _INLINE_CODE.sub(r"\1", text)
    s = _BOLD.sub(r"\1", s)
    s = s.strip().lower()
    # Python's \w is Unicode-aware, so CJK ideographs survive on their own.
    # Naming extra ranges here is not just redundant, it breaks the slug: the
    # fullwidth block (U+FF00-FFEF) carries punctuation like "：", which GitHub
    # strips — keeping it desynchronises the hand-written TOC links.
    s = re.sub(r"[^\w\s-]", "", s)
    return re.sub(r"\s+", "-", s).strip("-")


# ---------------------------------------------------------------- blocks

def is_table_sep(line):
    return bool(re.fullmatch(r"\s*\|?[\s:|-]+\|[\s:|-]*", line)) and "-" in line


def split_row(line):
    line = line.strip()
    if line.startswith("|"):
        line = line[1:]
    if line.endswith("|"):
        line = line[:-1]
    return [c.strip() for c in line.split("|")]


def render_table(header, rows, out):
    out.append("<div class=\"tablewrap\"><table>")
    out.append("<thead><tr>" + "".join(f"<th>{inline(c)}</th>" for c in header) + "</tr></thead>")
    out.append("<tbody>")
    for r in rows:
        # Pad/trim so a ragged row can't break the column grid.
        r = (r + [""] * len(header))[: len(header)]
        out.append("<tr>" + "".join(f"<td>{inline(c)}</td>" for c in r) + "</tr>")
    out.append("</tbody></table></div>")


def convert(md):
    lines = md.split("\n")
    out = []
    toc = []           # (level, text, id) for the sidebar
    i = 0
    n = len(lines)

    while i < n:
        line = lines[i]

        # fenced code
        if line.startswith("```"):
            lang = line[3:].strip()
            i += 1
            buf = []
            while i < n and not lines[i].startswith("```"):
                buf.append(lines[i])
                i += 1
            i += 1
            cls = f' class="lang-{html.escape(lang)}"' if lang else ""
            out.append(f"<pre{cls}><code>" + html.escape("\n".join(buf), quote=False) + "</code></pre>")
            continue

        # heading
        m = re.match(r"^(#{1,6})\s+(.*)$", line)
        if m:
            lvl = len(m.group(1))
            text = m.group(2).strip()
            hid = slug(text)
            out.append(f'<h{lvl} id="{hid}">{inline(text)}</h{lvl}>')
            if 2 <= lvl <= 3:
                toc.append((lvl, re.sub(r"[*`]", "", text), hid))
            i += 1
            continue

        # horizontal rule
        if re.fullmatch(r"\s*---+\s*", line):
            out.append("<hr>")
            i += 1
            continue

        # table
        if "|" in line and i + 1 < n and is_table_sep(lines[i + 1]):
            header = split_row(line)
            i += 2
            rows = []
            while i < n and "|" in lines[i] and lines[i].strip():
                rows.append(split_row(lines[i]))
                i += 1
            render_table(header, rows, out)
            continue

        # blockquote (consecutive '>' lines form one block)
        if line.startswith(">"):
            buf = []
            while i < n and lines[i].startswith(">"):
                buf.append(re.sub(r"^>\s?", "", lines[i]))
                i += 1
            inner = convert_fragment("\n".join(buf))
            out.append(f"<blockquote>{inner}</blockquote>")
            continue

        # list (unordered or ordered), one level of nesting
        if re.match(r"^\s*([-*]|\d+\.)\s+", line):
            block = []
            while i < n and (re.match(r"^\s*([-*]|\d+\.)\s+", lines[i]) or
                             (lines[i].strip() and lines[i].startswith("  "))):
                block.append(lines[i])
                i += 1
            out.append(render_list(block))
            continue

        # blank
        if not line.strip():
            i += 1
            continue

        # paragraph
        buf = []
        while i < n and lines[i].strip() and not re.match(
                r"^(#{1,6}\s|```|>|\s*([-*]|\d+\.)\s|\s*---+\s*$)", lines[i]):
            if "|" in lines[i] and i + 1 < n and is_table_sep(lines[i + 1]):
                break
            buf.append(lines[i])
            i += 1
        if buf:
            out.append("<p>" + inline(" ".join(x.strip() for x in buf)) + "</p>")
        else:
            i += 1

    return "\n".join(out), toc


def convert_fragment(md):
    """Convert nested content (blockquote bodies) without collecting TOC entries."""
    body, _ = convert(md)
    return body


def render_list(block):
    """Render one list block, supporting a single level of nesting."""
    items = []          # (indent, marker_is_ordered, text_lines)
    for raw in block:
        m = re.match(r"^(\s*)([-*]|\d+\.)\s+(.*)$", raw)
        if m:
            items.append([len(m.group(1)), not m.group(2) in ("-", "*"), [m.group(3)]])
        elif items:
            items[-1][2].append(raw.strip())

    if not items:
        return ""

    base = min(it[0] for it in items)
    ordered = items[0][1]
    out = ["<ol>" if ordered else "<ul>"]
    idx = 0
    while idx < len(items):
        indent, _, text = items[idx]
        out.append("<li>" + inline(" ".join(text)))
        # collect any deeper-indented items as a nested list
        nested = []
        j = idx + 1
        while j < len(items) and items[j][0] > indent:
            nested.append(items[j])
            j += 1
        if nested:
            sub_ordered = nested[0][1]
            out.append("<ol>" if sub_ordered else "<ul>")
            for _, _, t in nested:
                out.append("<li>" + inline(" ".join(t)) + "</li>")
            out.append("</ol>" if sub_ordered else "</ul>")
        out.append("</li>")
        idx = j
    out.append("</ol>" if ordered else "</ul>")
    return "\n".join(out)


# ---------------------------------------------------------------- page

CSS = """
:root{
  --ink:#0F1E22; --ink-soft:#35494F; --muted:#5F747A; --faint:#8A9BA0;
  --line:#D6E0E1; --line-soft:#E7EDED; --paper:#F2F5F5; --card:#FFFFFF;
  --signal:#0E9AA8; --signal-deep:#0A6C76;
  --warn-bg:#FFF6E0; --warn-line:#E8C870; --warn-ink:#7A5A10;
  --mono:"Cascadia Code",ui-monospace,"SF Mono",Consolas,"Liberation Mono",monospace;
  --sans:"Segoe UI",system-ui,-apple-system,"Noto Sans TC","Microsoft JhengHei",
         "Helvetica Neue",Arial,sans-serif;
}
@media (prefers-color-scheme: dark){
  :root{
    --ink:#E7EFF0; --ink-soft:#BCCBCE; --muted:#8FA3A8; --faint:#6F8388;
    --line:#2C3B3F; --line-soft:#222E31; --paper:#101A1C; --card:#162225;
    --signal:#38BECB; --signal-deep:#7FD8E1;
    --warn-bg:#2C2413; --warn-line:#6B5620; --warn-ink:#E0C98A;
  }
}
*{box-sizing:border-box}
html{scroll-behavior:smooth}
body{margin:0;background:var(--paper);color:var(--ink);font-family:var(--sans);
  line-height:1.75;-webkit-font-smoothing:antialiased;}
.shell{max-width:1180px;margin:0 auto;padding:0 24px;
  display:grid;grid-template-columns:246px minmax(0,1fr);gap:44px;align-items:start;}

/* ---- sidebar ---- */
.toc{position:sticky;top:0;max-height:100vh;overflow-y:auto;
  padding:40px 6px 40px 0;font-size:13px;}
.toc .tt{font-size:11px;letter-spacing:.2em;text-transform:uppercase;
  color:var(--signal-deep);font-weight:700;margin:0 0 12px;}
.toc a{display:block;color:var(--muted);text-decoration:none;padding:3px 0;
  border-left:2px solid transparent;padding-left:10px;line-height:1.45;}
.toc a:hover{color:var(--signal-deep);border-left-color:var(--signal);}
.toc a.l3{padding-left:22px;font-size:12px;color:var(--faint);}

/* ---- content ---- */
main{min-width:0;padding:40px 0 96px;}
h1{font-size:clamp(26px,4.2vw,36px);line-height:1.2;letter-spacing:-.01em;
  font-weight:700;margin:0 0 6px;}
h2{font-size:22px;font-weight:700;margin:52px 0 14px;padding-bottom:8px;
  border-bottom:2px solid var(--line);scroll-margin-top:20px;}
h3{font-size:17px;font-weight:650;margin:34px 0 10px;color:var(--ink);
  scroll-margin-top:20px;}
h4{font-size:15px;font-weight:650;margin:24px 0 8px;color:var(--ink-soft);}
p{margin:12px 0;}
a{color:var(--signal-deep);}
hr{border:0;border-top:1px solid var(--line);margin:40px 0;}
ul,ol{margin:12px 0;padding-left:24px;}
li{margin:5px 0;}
li>ul,li>ol{margin:5px 0;}
strong{font-weight:650;}
code{font-family:var(--mono);font-size:.88em;background:var(--line-soft);
  padding:.13em .4em;border-radius:4px;border:1px solid var(--line);
  word-break:break-word;}
pre{background:var(--card);border:1px solid var(--line);border-radius:10px;
  padding:14px 16px;overflow-x:auto;margin:16px 0;}
pre code{background:none;border:0;padding:0;font-size:12.5px;line-height:1.6;}
blockquote{margin:18px 0;padding:12px 16px;background:var(--warn-bg);
  border:1px solid var(--warn-line);border-left-width:4px;border-radius:0 8px 8px 0;
  color:var(--warn-ink);}
blockquote p{margin:6px 0;}
blockquote code{background:rgba(0,0,0,.06);border-color:rgba(0,0,0,.12);}

/* ---- tables ---- */
.tablewrap{overflow-x:auto;margin:16px 0;border:1px solid var(--line);
  border-radius:10px;background:var(--card);}
table{border-collapse:collapse;width:100%;font-size:14px;}
th,td{padding:9px 13px;text-align:left;vertical-align:top;
  border-bottom:1px solid var(--line-soft);}
th{background:var(--line-soft);font-weight:650;color:var(--ink);
  white-space:nowrap;}
tr:last-child td{border-bottom:0;}
td code{white-space:nowrap;}

/* ---- narrow ---- */
@media (max-width:900px){
  .shell{grid-template-columns:minmax(0,1fr);gap:0;}
  .toc{position:static;max-height:none;padding:28px 0 0;
    border-bottom:1px solid var(--line);}
  .toc a.l3{display:none;}
  main{padding-top:24px;}
}

/* ---- print ---- */
@media print{
  :root{--paper:#fff;--card:#fff;}
  body{background:#fff;color:#000;font-size:10.5pt;line-height:1.5;}
  .shell{display:block;max-width:none;padding:0;}
  .toc{display:none;}
  main{padding:0;}
  h2{page-break-after:avoid;margin-top:22pt;}
  h3,h4{page-break-after:avoid;}
  table,pre,blockquote{page-break-inside:avoid;}
  .tablewrap{overflow:visible;}
  a{color:#000;text-decoration:none;}
}
"""


def build(md, title):
    body, toc = convert(md)
    nav = "\n".join(
        f'<a class="l{lvl}" href="#{hid}">{html.escape(txt)}</a>'
        for lvl, txt, hid in toc
    )
    return f"""<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{html.escape(title)}</title>
<style>{CSS}</style>
</head>
<body>
<div class="shell">
<nav class="toc"><p class="tt">目錄</p>
{nav}
</nav>
<main>
{body}
</main>
</div>
</body>
</html>
"""


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit("usage: md2html.py <source.md> <output.html> <title>")
    src, dst, title = sys.argv[1], sys.argv[2], sys.argv[3]
    with open(src, encoding="utf-8") as f:
        md = f.read()
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(build(md, title))
    print(f"wrote {dst}")
