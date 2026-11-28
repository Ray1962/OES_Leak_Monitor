// Theme, slide layouts and drawing helpers shared by every OES Leak Monitor report deck.
// A deck script only writes its slides:
//
//   const { createDeck } = require("./deck-base.js");
//   const d = createDeck({ title: "…", footer: "OES Leak Monitor · 新機台 2026-11-26",
//                          out: "OES-xxx.pptx", imgDir: "C:\\…\\OES_Leak_Monitor\\docs\\images" });
//   const { pres, C, HEX } = d;
//   pres.addSection({ title: "總覽" });
//   const s = d.content("結論:…", "總覽");      // title-only content slide
//   d.card(s, …); d.badge(s, …); d.txt(s, …);
//   d.save();                                   // writes, then applies the theme colors
//
// Runs from the deck workspace (%TEMP%\oes-deck), where pptxgenjs is installed; see SKILL.md.
const pptxgen = require("pptxgenjs");
const path = require("path");
const { applyTheme } = require("./apply_theme.js");

// The palette the 11/25 and 11/26 decks use. accent1 plasma orange (headline numbers, badges),
// accent2 teal (OK / the fix), accent3 red (NO-GO / alarm), accent4 amber (warning / expected),
// accent5 slate (captions, muted), accent6 steel blue.
const THEME = {
  name: "OES Plasma",
  headFontFace: "Microsoft JhengHei",
  bodyFontFace: "Microsoft JhengHei",
  colors: {
    dk1: "1F2933", lt1: "FFFFFF", dk2: "16233A", lt2: "EEF2F6",
    accent1: "E36414", accent2: "0F8B8D", accent3: "B3261E",
    accent4: "D89B1A", accent5: "5B6B7F", accent6: "2E5C8A",
    hlink: "2E5C8A", folHlink: "5B6B7F",
  },
};

function createDeck({ title, footer, out, imgDir }) {
  const HEX = THEME.colors;
  const pres = new pptxgen();
  pres.layout = "LAYOUT_WIDE"; // 13.333 x 7.5 in; content sits between y 1.35 and 6.8, footer at 7.0
  pres.title = title;
  pres.author = "OES Leak Monitor";
  pres.theme = { headFontFace: THEME.headFontFace, bodyFontFace: THEME.bodyFontFace };
  const C = pres.SchemeColor;

  // ---- layouts: dark title, dark closing list, light content with footer + slide number
  pres.defineSlideMaster({
    title: "TITLE_DARK",
    background: { color: C.text2 },
    objects: [
      { placeholder: { options: { name: "title", type: "title", x: 0.8, y: 2.3, w: 11.7, h: 1.6, fontSize: 40, bold: true, color: C.background1, valign: "bottom", align: "left", margin: 0 }, text: "" } },
      { placeholder: { options: { name: "body", type: "body", x: 0.8, y: 4.1, w: 11.7, h: 1.4, fontSize: 18, color: C.background2, valign: "top", margin: 0 }, text: "" } },
    ],
  });
  pres.defineSlideMaster({
    title: "CLOSE_DARK",
    background: { color: C.text2 },
    objects: [
      { placeholder: { options: { name: "title", type: "title", x: 0.8, y: 0.5, w: 11.7, h: 0.9, fontSize: 32, bold: true, color: C.background1, valign: "middle", align: "left", margin: 0 }, text: "" } },
    ],
  });
  pres.defineSlideMaster({
    title: "CONTENT",
    background: { color: C.background1 },
    margin: [0.5, 0.6, 0.6, 0.6],
    objects: [
      { placeholder: { options: { name: "title", type: "title", x: 0.6, y: 0.35, w: 12.1, h: 0.8, fontSize: 30, bold: true, color: C.text2, valign: "middle", align: "left", margin: 0 }, text: "" } },
      { text: { text: footer, options: { x: 0.6, y: 7.0, w: 6, h: 0.3, fontSize: 10, color: C.accent5, margin: 0 } } },
    ],
    slideNumber: { x: 12.2, y: 7.0, w: 0.5, h: 0.3, fontSize: 10, color: C.accent5, align: "right" },
  });

  // ---- helpers (each object gets a unique objectName)
  let objN = 0;
  const nm = (s) => `${s}-${++objN}`;
  /** Filled circle with a short label: step numbers, verdict letters. */
  function badge(slide, x, y, label, fill, d = 0.5, size = 16) {
    slide.addShape(pres.ShapeType.ellipse, { x, y, w: d, h: d, fill: { color: fill }, line: { color: fill }, objectName: nm("badge") });
    slide.addText(label, { x, y, w: d, h: d, fontSize: size, bold: true, color: C.background1, align: "center", valign: "middle", margin: 0, isTextBox: true, objectName: nm("badge-text") });
  }
  /** Rounded tinted panel behind a group of text. */
  function card(slide, x, y, w, h, fill = C.background2) {
    slide.addShape(pres.ShapeType.roundRect, { x, y, w, h, rectRadius: 0.08, fill: { color: fill }, line: { color: fill }, objectName: nm("card") });
  }
  /** Text box, zero margin so it aligns with cards and badges; default 15 pt top-aligned. */
  function txt(slide, text, opts) {
    slide.addText(text, Object.assign({ isTextBox: true, margin: 0, color: C.text1, fontSize: 15, valign: "top" }, opts, { objectName: nm("text") }));
  }
  /** Light content slide with its title filled. */
  function content(slideTitle, section) {
    const s = pres.addSlide({ masterName: "CONTENT", sectionTitle: section });
    s.addText(slideTitle, { placeholder: "title" });
    return s;
  }
  /** Picture from docs/images at width w, keeping the PNG's aspect (pass its pixel size). Returns the height. */
  function img(slide, file, x, y, w, pw, ph, alt) {
    slide.addImage({ path: path.join(imgDir, file), x, y, w, h: w * ph / pw, altText: alt, objectName: nm("img") });
    return w * ph / pw;
  }
  /** Rounded pill with a verdict word (OK / ALARM / 預期內). */
  function pill(slide, x, y, w, label, fill) {
    slide.addShape(pres.ShapeType.roundRect, { x, y, w, h: 0.5, rectRadius: 0.25, fill: { color: fill }, line: { color: fill }, objectName: nm("pill") });
    txt(slide, label, { x, y, w, h: 0.5, fontSize: 13, bold: true, color: C.background1, align: "center", valign: "middle" });
  }

  // ---- table styles: dark header row, 14 pt cells, light grey borders
  const HDR = { bold: true, color: C.background1, fill: { color: C.text2 }, fontSize: 14, valign: "middle" };
  const CELL = { fontSize: 14, color: C.text1, valign: "middle" };
  const BORDER = { type: "solid", pt: 0.75, color: "D5DCE4" };
  const cell = (t, extra = {}) => ({ text: t, options: Object.assign({}, CELL, extra) });
  const head = (t) => ({ text: t, options: HDR });
  function table(slide, rows, opts) {
    slide.addTable(rows, Object.assign({ border: BORDER, fill: { color: C.background1 } }, opts, { objectName: nm("table") }));
  }

  // ---- chart text: theme body font, slate labels, quiet grid (hex only — scheme colors are not allowed here)
  const CHART = {
    titleFontSize: 14, titleColor: HEX.dk2, titleFontFace: "+mn-lt",
    catAxisLabelColor: HEX.accent5, valAxisLabelColor: HEX.accent5, catAxisLabelFontSize: 11, valAxisLabelFontSize: 11,
    catAxisLabelFontFace: "+mn-lt", valAxisLabelFontFace: "+mn-lt",
    catAxisTitleFontSize: 11, catAxisTitleColor: HEX.accent5, catAxisTitleFontFace: "+mn-lt",
    legendFontSize: 12, legendFontFace: "+mn-lt", legendColor: HEX.accent5,
    valGridLine: { color: "E1E6EC", size: 0.75 }, catGridLine: { style: "none" },
  };

  async function save() {
    await pres.writeFile({ fileName: out });
    await applyTheme(out, THEME);
    console.log("wrote", path.resolve(out));
  }

  return { pres, C, HEX, THEME, nm, badge, card, txt, content, img, pill, HDR, CELL, cell, head, table, CHART, save };
}

module.exports = { createDeck, THEME };
