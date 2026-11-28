---
name: report-to-pptx
description: Turn this repo's analysis reports (docs/production-*.md, leak-test write-ups, strategy docs) into a Traditional Chinese PowerPoint deck in the house style of the 11/25 and 11/26 decks, built with pptxgenjs on the Windows side and checked by rendering every slide through PowerPoint. Use when asked for a 簡報 / PowerPoint / .pptx / slides of a report, a test result or a leak strategy.
---

# Report → PowerPoint deck

The decks are built **on the Windows side** — WSL has no `node`, no python-pptx and no
LibreOffice; Windows has `node`, a Python venv with the validator's packages, and PowerPoint
itself, which is the renderer the fab will use. Everything happens in one workspace,
`%TEMP%\oes-deck` (`/mnt/c/Users/infor/AppData/Local/Temp/oes-deck` from WSL), outside the repo.

What lives here:

| File | Role |
|---|---|
| `scripts/deck-base.js` | Theme (`OES Plasma`, Microsoft JhengHei), the three layouts, helpers (`content`, `card`, `badge`, `txt`, `img`, `pill`, `table`/`head`/`cell`, `CHART`), `save()` |
| `scripts/apply_theme.js` | Writes the theme colors into the deck after `writeFile` (same file as the `pptx` skill's) |
| `scripts/render.ps1` | PowerPoint COM: exports every slide to `slide-NN.png` at 1600×900 |
| `scripts/contact_sheet.py` | Stacks two slides per JPEG so a review costs half the reads |
| `examples/20261126-dv-test-strategy.js` | The full 14-slide 11/26 deck — the pattern to copy |

For the pptxgenjs API traps (hex colors without `#`, shared option objects, stacked-chart
label positions) load the `anthropic-skills:pptx` skill; `deck-base.js` already follows it.

## 1. Read the sources and plan the slides

Read every report the deck covers, end to end, and list the figures in `docs/images` that
belong to them. Plan one message per slide, the title stating the message
(`結論:只抓得到 DV 500 級的漏`, not `結論`). The shape that has worked:

- **總覽** — dark title slide; four numbered conclusion cards.
- **結果** — data, spectra / charts, sensitivity, the tool's state.
- **策略** — what changes and why, settings, the Replay or validation evidence.
- **下一步** — go-live steps; dark closing slide of questions for the customer.

Every number on a slide is copied from the source document, never re-derived; the detail a
slide leaves out goes into its speaker notes (`s.addNotes`), which every slide carries.
Done when each planned slide has a title, its numbers with their source section, and its
visual (figure, native chart, table, stat cards).

## 2. Prepare the workspace

```bash
W=/mnt/c/Users/infor/AppData/Local/Temp/oes-deck
ls $W/node_modules/pptxgenjs $W/venv/Scripts/python.exe $W/skillscripts/office/validate.py
```

Whatever is missing, create once:

```bash
mkdir -p $W && cd $W
cmd.exe /c "cd /d %TEMP%\oes-deck && npm init -y && npm install pptxgenjs jszip"
cmd.exe /c "cd /d %TEMP%\oes-deck && py -3.13 -m venv venv && venv\Scripts\pip install lxml defusedxml pillow python-pptx"
cp -r <anthropic-skills:pptx skill directory>/scripts $W/skillscripts   # validate.py + schemas
```

Then, every run, copy this skill's scripts in (so edits to the skill reach the build):

```bash
cp .claude/skills/report-to-pptx/scripts/* $W/
```

## 3. Write the deck script

Copy `examples/20261126-dv-test-strategy.js` to `$W/deck-<yyyymmdd>.js` and rewrite its
slides; keep the `createDeck({ title, footer, out, imgDir })` call at the top and
`d.save()` at the bottom. Layout facts that keep slides consistent:

- Canvas 13.333 × 7.5 in. Content between **y 1.35 and 6.8**, left edge **0.6**, right edge
  **12.7**; footer and slide number sit at y 7.0 on the layout.
- Body text 13–17 pt, card headings 15–20 pt, stat numbers 26–44 pt in `HEX.accent1` or
  the verdict color. Colors by meaning: `accent2` OK / the fix, `accent3` NO-GO / alarm,
  `accent4` warning / expected, `accent5` captions.
- `img(s, file, x, y, w, pixelW, pixelH, alt)` needs the PNG's pixel size:
  `python3 -c "import struct,sys;[print(f,*struct.unpack('>II',open(f,'rb').read(24)[16:24])) for f in sys.argv[1:]]" docs/images/<files>`.
- Charts are native (`pres.addChart`) with `...d.CHART` spread into the options; colors
  there are hex (`HEX.accent3`), never `C.*`.
- Verdict pills name the verdict itself (`ALARM`, `預期內`, `OK`) — a red pill reading `OK`
  on an alarm row reads as a contradiction.

## 4. Build, validate, render, look

```bash
cmd.exe /c "cd /d %TEMP%\oes-deck && node deck-<yyyymmdd>.js && venv\Scripts\python.exe skillscripts\office\validate.py <out>.pptx"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w $W/render.ps1)" -Pptx "$(wslpath -w $W/<out>.pptx)" -OutDir "$(wslpath -w $W/render)"
cmd.exe /c "cd /d %TEMP%\oes-deck && venv\Scripts\python.exe contact_sheet.py render"
cp $W/render/pair-*.jpg <scratchpad>/deck/      # then Read every pair-N.jpg
```

Put Python in a file rather than `cmd.exe /c "python -c \"…\""` — cmd's quoting breaks it.
A `writeFile` that fails with a locked file means the deck is open in PowerPoint.

Look at **every** slide, and fix in the script, rebuild, re-render: text overflowing or
wrapping inside a table header (widen that column in `colW`, keep the sum equal to `w`), a
caption or footnote touching the block above it, a card label wrapping to a second line,
uneven gaps, an image squashed (wrong pixel size). Done when validation prints
`All validations PASSED!` and the last render of every slide shows none of these.

## 5. Deliver

Copy the deck to `docs/<same stem as the main report>-zh-TW.pptx` (the 11/25 and 11/26 decks
sit beside their `.md`), add its line to `docs/dev/docs-index.md` (what it covers, slide
count, "not shipped"), run `python3 tools/check-docs.py`, and keep the deck script in the
workspace for later edits. Commit only when asked.
