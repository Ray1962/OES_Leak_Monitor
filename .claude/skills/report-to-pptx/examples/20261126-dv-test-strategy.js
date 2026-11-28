// 範例:11/26 DV 測試與測漏策略簡報(docs/production-20261126-leak-strategy-zh-TW.pptx,14 頁)。
// 來源:docs/production-20261126-dv-test-zh-TW.md、docs/production-20261126-leak-strategy-zh-TW.md。
// 新簡報複製這個檔改各頁內容;版型、配色、helper 都在 deck-base.js。
const { createDeck } = require("./deck-base.js");

const d = createDeck({
  title: "新生產機台 11/26 洩漏測試與測漏策略",
  footer: "OES Leak Monitor · 新機台 2026-11-26",
  out: "OES-11-26-dv-test-strategy.pptx",
  imgDir: "C:\\Users\\infor\\source\\repos\\Ray1962\\OES_Leak_Monitor\\docs\\images",
});
const { pres, C, HEX, nm, badge, card, txt, content, img, HDR, CELL, CHART } = d;
const BORDER = { type: "solid", pt: 0.75, color: "D5DCE4" };
const cellB = d.cell;

// ---------------------------------------------------------------- 1. title
pres.addSection({ title: "總覽" });
{
  const s = pres.addSlide({ masterName: "TITLE_DARK", sectionTitle: "總覽" });
  s.addText("新生產機台 11/26 洩漏測試與測漏策略", { placeholder: "title" });
  s.addText([
    { text: "recipe(C₂H₂ / He / Ar)上洩漏閥 DV 0 → 500", options: { breakLine: true } },
    { text: "2026-11-26 17:00–17:32 測試 · 新策略已用 Replay 驗證" },
  ], { placeholder: "body" });
  badge(s, 0.8, 1.2, "DV", HEX.accent1, 0.8, 16);
  s.addNotes("11/26 17:00:57 起,在 11/25 那台新生產機台的 recipe 上,用洩漏閥把空氣加進腔體,DV 依序 0、200、300、400、500,每個點位 3 個 recipe step(DV 500 多一個)。這份簡報整理測試結果、靈敏度、工具當時的狀態,以及據此修訂、已經在開發機 Replay 驗證過的測漏策略。來源文件:production-20261126-dv-test-zh-TW 與 production-20261126-leak-strategy-zh-TW。");
}

// ---------------------------------------------------------------- 2. conclusions
{
  const s = content("結論:只抓得到 DV 500 級的漏", "總覽");
  const items = [
    ["1", HEX.accent3, "只有 DV 500 看得到", "N₂ 第二正帶整組長出來,N₂ 337 / Ar ×5.5。DV 200 / 300 / 400 都落在無漏範圍內。"],
    ["2", HEX.accent4, "這個 recipe 看不到氧", "777 nm 沒有 O 峰,NO γ 帶不升反降。O 777 指標退場,只剩 N₂ 可用。"],
    ["3", HEX.accent3, "下限約 0.1–0.3 Torr/min", "DV 400 的 ROR 0.5 Torr/min 也看不出來。對 mTorr/min 級的規格是 NO-GO。"],
    ["4", HEX.accent2, "新策略:兩條 N₂ + 每天重建 GR", "N₂ 357 + N₂ 337 當 Alarm 對。Replay:DV 0–400 不報、DV 500 報;跨日 GR 會誤報。"],
  ];
  items.forEach(([n, col, head, body], i) => {
    const x = 0.6 + (i % 2) * 6.15, y = 1.55 + Math.floor(i / 2) * 2.45;
    card(s, x, y, 5.95, 2.15);
    badge(s, x + 0.3, y + 0.3, n, col, 0.6, 18);
    txt(s, head, { x: x + 1.1, y: y + 0.3, w: 4.6, h: 0.6, fontSize: 20, bold: true, color: C.text2, valign: "middle" });
    txt(s, body, { x: x + 1.1, y: y + 1.0, w: 4.6, h: 1.2, fontSize: 15 });
  });
  s.addNotes("(1) DV 500 時 N₂ 337、357、380、315 nm 的第二正帶全部出現,四個 step 都一樣、整段 step 平穩,約 70σ。DV 400 的 N₂ 337 / Ar 是 3.60–3.71(×10⁻³),跟 DV 0 / 200 的 3.4–4.0 分不出來。(2) 777 nm 在 DV 500 的增量是 774–776 nm N₂ 第一正帶的斜坡,沒有 O 峰形;NO γ 反而一路降。C₂H₂ 250 sccm 很可能把氧都變成 CO / H₂O。(3) 用客戶量的 ROR 與閥表推:偵測下限約 10–25 sccm 空氣,也就是 0.1–0.3 Torr/min。(4) 見策略段。");
}

// ---------------------------------------------------------------- 3. test conditions
pres.addSection({ title: "測試結果" });
{
  const s = content("測試:recipe 連續 16 步,DV 每點 3 步", "測試結果");
  const rows = [
    ["0", "17:01–17:04", "1–3", "—", "0"],
    ["200", "17:07–17:10", "4–6", "0.007", "0.59 sccm"],
    ["300", "17:13–17:17", "7–9", "0.14", "11.8 sccm"],
    ["400", "17:18–17:21", "10–12", "0.5", "≈ 42 sccm"],
    ["500", "17:23–17:32", "13–16", "(沒量)", "≈ 590 sccm"],
  ];
  s.addTable([
    [{ text: "DV", options: HDR }, { text: "時間", options: HDR }, { text: "位置", options: HDR }, { text: "ROR(Torr/min)", options: HDR }, { text: "空氣流量", options: HDR }],
    ...rows.map(([dv, t, p, r, f]) => [cellB(dv, { bold: true, color: dv === "500" ? HEX.accent3 : HEX.dk2 }), cellB(t), cellB(p), cellB(r), cellB(f)]),
  ], { x: 0.6, y: 1.45, w: 7.3, colW: [0.9, 1.8, 1.1, 1.8, 1.7], rowH: 0.55, border: BORDER, fill: { color: C.background1 }, objectName: nm("table") });
  txt(s, "ROR 是客戶量的值;流量用閥表,DV 400 依 ROR 反推(§靈敏度)。位置 = 16:21 precoat 之後第幾個 recipe step,測試期間沒有再 precoat。", { x: 0.6, y: 5.0, w: 7.3, h: 0.9, fontSize: 12, color: C.accent5 });

  const st = [["16", "個 recipe step", "C₂H₂ 250 · He 5 000 · Ar 10 000 sccm"], ["1.1 s", "每幀", "averageCount 5,一步約 29 幀穩態"], ["位置 1–3", "precoat 記憶效應", "DV 0 剛好落在這裡,比較以第 7 步後為主"]];
  st.forEach(([big, lab, sub], i) => {
    const y = 1.45 + i * 1.45;
    card(s, 8.3, y, 4.4, 1.25);
    txt(s, big, { x: 8.55, y: y + 0.1, w: 1.9, h: 0.6, fontSize: 26, bold: true, color: HEX.accent1, valign: "middle" });
    txt(s, lab, { x: 10.45, y: y + 0.15, w: 2.15, h: 0.55, fontSize: 15, bold: true, color: C.text2, valign: "middle" });
    txt(s, sub, { x: 8.55, y: y + 0.75, w: 4.0, h: 0.4, fontSize: 12, color: C.accent5 });
  });
  s.addNotes("現場紀錄(OES.txt):DV 0 17:01–17:04、200 17:07–17:10、300 17:13–17:17、400 17:18–17:21、500 17:23–17:26,17:31 再一步 DV 500。客戶量的 ROR:DV 200 0.007、DV 300 0.14、DV 400 0.5 Torr/min,DV 500 沒有量。DV 沒有關回 0 看恢復。17:54 那筆錄檔是 575 nm 附近的寬帶外部光,不是電漿。");
}

// ---------------------------------------------------------------- 4. spectra N2
{
  const s = content("光譜:DV 0–400 疊在一起,DV 500 才長出 N₂", "測試結果");
  img(s, "production-20261126-n2-bands.png", 0.6, 1.4, 12.1, 1430, 462, "DV 0 到 500 的 N₂ 第二正帶三個帶頭");
  const rows = [
    ["N₂ 337 帶頭", "+310 counts", "寬背景 peak / Ar:3.3–4.0 → 19(×10⁻³)"],
    ["N₂ 357 / 380 / 315", "整組出現", "第二正帶各序列都長出來"],
    ["UV 連續光", "−27 %(230 nm)", "30 分鐘內一路降,不是漏氣"],
  ];
  rows.forEach(([a, b, c], i) => {
    const x = 0.6 + i * 4.1;
    card(s, x, 5.5, 3.9, 1.3);
    txt(s, a, { x: x + 0.25, y: 5.6, w: 3.5, h: 0.35, fontSize: 14, bold: true, color: C.text2 });
    txt(s, b, { x: x + 0.25, y: 5.95, w: 3.5, h: 0.45, fontSize: 20, bold: true, color: i === 2 ? HEX.accent5 : HEX.accent3 });
    txt(s, c, { x: x + 0.25, y: 6.4, w: 3.5, h: 0.35, fontSize: 12, color: C.accent5 });
  });
  s.addNotes("每個 DV 的穩態幀平均譜。DV 0、200、300、400 形狀一樣,只是 UV 端整體往下(視窗沉積或電漿 UV 輸出漂移)。Ar 750 在 DV 300–400 降 8 %、DV 500 又回升,He 與 Hα 同步,是電漿本身的漂移;以 Ar 為分母的比值在這段會被抬高 5–8 %。DV 500 時 337 nm 帶頭比 DV 400 多約 310 counts。");
}

// ---------------------------------------------------------------- 5. O777 / NO
{
  const s = content("777 nm 沒有 O 峰:這個 recipe 看不到氧", "測試結果");
  const h = img(s, "production-20261126-o777-zoom.png", 0.6, 1.4, 6.9, 990, 660, "777 nm 附近 DV 300/400/500 的譜與差值");
  const rows = [
    ["O 777", "DV 500 − 400 在 777.4 nm 只有 +9,是 774–776 nm N₂ 第一正帶斜坡的尾巴", HEX.accent3],
    ["NO γ 237 / 247 / 259", "DV 500 是 −10 到 −19 counts,跟 UV 端一起往下,沒有 NO 帶", HEX.accent3],
    ["原因(推測)", "recipe 有 250 sccm C₂H₂,漏進來的 O 變成 CO / H₂O;11/19 實驗機沒有碳氫,DV 300 時 NO +30 %", HEX.accent5],
  ];
  rows.forEach(([head, body, col], i) => {
    const y = 1.45 + i * 1.6;
    card(s, 7.9, y, 4.8, 1.4);
    badge(s, 8.1, y + 0.2, String(i + 1), col, 0.45, 15);
    txt(s, head, { x: 8.7, y: y + 0.15, w: 3.85, h: 0.5, fontSize: 16, bold: true, color: C.text2, valign: "middle" });
    txt(s, body, { x: 8.7, y: y + 0.65, w: 3.85, h: 0.75, fontSize: 13 });
  });
  txt(s, "→ O 777 / Ar 退為 TrendOnly", { x: 7.9, y: 6.35, w: 4.8, h: 0.4, fontSize: 16, bold: true, color: C.accent2 });
  s.addNotes("上圖 772.4 nm 是 Ar 線(Ar 本身回升 2–3 %)。774–776 nm 有 +40–50 counts 的寬坡,是 N₂ 第一正帶;到 777.4 nm 只剩 +9,而且平滑往下,沒有 O 777 的峰形。引擎的 O 777 / Ar 在 DV 300–400 微升 +2–4 %,是 Ar 分母降了 8 %,不是 O。11/25 把 O 777 當零背景指標的想法,這次被否定。");
}

// ---------------------------------------------------------------- 6. per step chart
{
  const s = content("逐 step:N₂ 337 只在 DV 500 跳起來", "測試結果");
  const v = [1.80, 3.52, 3.51, 3.42, 3.98, 3.70, 3.31, 3.27, 3.29, 3.70, 3.71, 3.60, 19.86, 18.99, 19.19, 18.63];
  const labels = v.map((_, i) => String(i + 1));
  s.addChart(pres.ChartType.bar, [
    { name: "DV 0–400", labels, values: v.map((x, i) => (i < 12 ? x : 0)) },
    { name: "DV 500", labels, values: v.map((x, i) => (i >= 12 ? x : 0)) },
  ], {
    x: 0.6, y: 1.35, w: 7.6, h: 5.4, barGrouping: "stacked", barGapWidthPct: 40,
    showTitle: true, title: "N₂ 337 / Ar 750(寬背景 peak-height,×10⁻³)", titleFontSize: 14, titleColor: HEX.dk2, titleFontFace: "+mn-lt",
    chartColors: [HEX.accent5, HEX.accent3], showLegend: true, legendPos: "t", legendFontSize: 12, legendFontFace: "+mn-lt", legendColor: HEX.accent5,
    showValue: false,
    catAxisTitle: "precoat 後第幾個 recipe step(1–3 DV 0、4–6 DV 200、7–9 DV 300、10–12 DV 400)", showCatAxisTitle: true, catAxisTitleFontSize: 11, catAxisTitleColor: HEX.accent5, catAxisTitleFontFace: "+mn-lt",
    catAxisLabelColor: HEX.accent5, valAxisLabelColor: HEX.accent5, catAxisLabelFontSize: 11, valAxisLabelFontSize: 11,
    catAxisLabelFontFace: "+mn-lt", valAxisLabelFontFace: "+mn-lt",
    valAxisMinVal: 0, valAxisMaxVal: 22, valAxisMajorUnit: 5, valAxisLabelFormatCode: "0",
    valGridLine: { color: "E1E6EC", size: 0.75 }, catGridLine: { style: "none" },
    objectName: nm("chart"),
  });
  const st = [["×5.5", "DV 500 vs DV 0–400", "3.3–4.0 → 18.6–19.9", HEX.accent3], ["≈ 70σ", "相對 step 間 sd 0.22", "四個 DV 500 step 都一樣", HEX.accent3], ["0 – +0.4", "DV 400 的增量", "落在 DV 0–200 的範圍內", HEX.accent5]];
  st.forEach(([big, lab, sub, col], i) => {
    const y = 1.4 + i * 1.8;
    card(s, 8.6, y, 4.1, 1.6);
    txt(s, big, { x: 8.85, y: y + 0.1, w: 3.6, h: 0.7, fontSize: 32, bold: true, color: col, valign: "middle" });
    txt(s, lab, { x: 8.85, y: y + 0.8, w: 3.6, h: 0.35, fontSize: 15, bold: true, color: C.text2 });
    txt(s, sub, { x: 8.85, y: y + 1.15, w: 3.6, h: 0.35, fontSize: 12, color: C.accent5 });
  });
  s.addNotes("離線抽法:335.5–337.2 nm 最大值,扣 331–332.5 與 338.3–339.7 nm 的平均,除以 Ar 750 peak-height。位置 1 偏低(1.8)是 precoat 之後第一步。DV 500 的 step 從第 7 幀起就平在 18–20,跟每個 step 頭兩幀(N₂ / Ar 約 1 400–1 900)的轉換段分得開。引擎現在的取線(N₂ 337 RawMean)同樣的漏只看到 +41 %,CN / Ar +24 %、O 777 RawMean +12 %。");
}

// ---------------------------------------------------------------- 7. sensitivity
{
  const s = content("靈敏度:下限約 0.1–0.3 Torr/min", "測試結果");
  const rows = [
    ["200", "7.5 × 10⁻³", "0.007", "64 L", "0.59 sccm", "39 ppm"],
    ["300", "0.15", "0.14", "64 L", "11.8 sccm", "775 ppm"],
    ["400", "(內插 1.36)", "0.5", "—", "≈ 42 sccm", "0.28 %"],
    ["500", "7.5", "沒量", "—", "≈ 590 sccm", "3.9 %"],
  ];
  s.addTable([
    [{ text: "DV", options: HDR }, { text: "閥表 Q(Torr·L/s)", options: HDR }, { text: "ROR(Torr/min)", options: HDR }, { text: "推得 V", options: HDR }, { text: "空氣", options: HDR }, { text: "占 recipe", options: HDR }],
    ...rows.map((r) => r.map((t, j) => cellB(t, j === 0 ? { bold: true } : {}))),
  ], { x: 0.6, y: 1.45, w: 7.6, colW: [0.7, 1.75, 1.65, 0.9, 1.3, 1.3], rowH: 0.55, border: BORDER, fill: { color: C.background1 }, objectName: nm("table") });
  txt(s, [
    { text: "DV 200 與 300 各自推出的腔體容積都是 64 L,閥表在這兩點可信。", options: { breakLine: true } },
    { text: "DV 400 實測只有內插值(107 sccm)的 40 %,DV 500 的 590 sccm 也可能高估。", options: { breakLine: true } },
    { text: "3σ ÷ DV 500 反應 × 流量 → 10–25 sccm → 0.12–0.3 Torr/min。" },
  ], { x: 0.6, y: 4.5, w: 7.6, h: 1.6, fontSize: 14, paraSpaceAfter: 6 });

  card(s, 8.6, 1.45, 4.1, 2.4, C.text2);
  txt(s, "0.1–0.3", { x: 8.85, y: 1.6, w: 3.6, h: 0.9, fontSize: 44, bold: true, color: C.background1, valign: "middle" });
  txt(s, "Torr/min 以上才抓得到", { x: 8.85, y: 2.5, w: 3.6, h: 0.4, fontSize: 16, bold: true, color: C.background2 });
  txt(s, "64 L 腔體 · recipe 15 250 sccm", { x: 8.85, y: 3.0, w: 3.6, h: 0.4, fontSize: 12, color: C.background2 });
  card(s, 8.6, 4.1, 4.1, 2.0);
  badge(s, 8.85, 4.35, "!", HEX.accent3, 0.55, 20);
  txt(s, "mTorr/min 級規格:NO-GO", { x: 9.55, y: 4.35, w: 3.0, h: 0.55, fontSize: 17, bold: true, color: HEX.accent3, valign: "middle" });
  txt(s, "差兩到三個數量級,要靠製程端換測漏步驟,不是調門檻", { x: 8.85, y: 5.05, w: 3.7, h: 0.9, fontSize: 14 });
  s.addNotes("閥表(dosing-valve-reference-zh-TW)與客戶量的 ROR 比對:DV 200、300 兩點各自推得 64 L,所以閥表可信;DV 400 用 64 L 由 ROR 反推約 42 sccm,只有表上內插值 107 sccm 的 40 %。DV 500 表上是 7.5 Torr·L/s ≈ 590 sccm,若跟 DV 400 一樣高估 2.5 倍則約 240 sccm。反應:DV 500 讓 N₂ 337 / Ar 多 +15.6(×10⁻³);雜訊:位置 2–12 的 step 間 sd 0.22,3σ ≈ 0.66。線性推算 3σ ≈ 25 sccm(或 10 sccm),64 L 下約 0.3(或 0.12)Torr/min。DV 400 按比例應 +1.1,實際 0 到 +0.4,差約 3σ,線性與否要 DV 450 或 DV 500 的 ROR 才分得出來。");
}

// ---------------------------------------------------------------- 8. tool status
{
  const s = content("工具在測試當下:全程 Idle,沒有判定", "測試結果");
  const bad = Object.assign({}, CELL, { color: HEX.accent3 });
  const fix = Object.assign({}, CELL, { color: HEX.accent2, bold: true });
  const rows = [
    ["Golden Run", "16:01–16:51 擷取 16 次,當時沒有 recipe 電漿,全部 GoldenRunEmpty 丟棄", "recipe 穩態再用 Baseline Builder 建"],
    ["作用中的 GR", "10/29「Recipe 1」只有一個舊 key 的基準 → 17:00–17:32 全程 Idle", "換成當天平台 GR"],
    ["分類器 / 比值", "分類器停用、比值沒有 ProcessClass、O 777 當 Alarm", "照新策略整組換"],
    ["SECS / EAP", "SECS 綁不到 10.2.81.83(16 次);EAP 10.2.3.120 連不上", "要新機台的位址"],
    ["資料夾", "21.25 GB,超過 20 GB 上限", "把舊封存檔搬走"],
  ];
  s.addTable([
    [{ text: "項目", options: HDR }, { text: "現況(diag 包 17:56)", options: HDR }, { text: "處理", options: HDR }],
    ...rows.map(([a, x, y]) => [{ text: a, options: Object.assign({ bold: true }, CELL) }, { text: x, options: bad }, { text: y, options: fix }]),
  ], { x: 0.6, y: 1.45, w: 12.1, colW: [2.2, 6.6, 3.3], rowH: 0.75, border: BORDER, fill: { color: C.background1 }, objectName: nm("table") });
  txt(s, "即使當時有平台 GR,舊比值組在 DV 500 也只有 CN / Ar +24 %(Warning),RequireTwo → 不會 Alarm。取樣 averageCount 5 已照 11/25 建議改回。", { x: 0.6, y: 6.2, w: 12.1, h: 0.6, fontSize: 13, color: C.accent5 });
  s.addNotes("16 次擷取命名為 Recipe、Recipe 1/2/3,每次 5 個比值都 GoldenRunRatioDropped(no frame passed the plasma-present gate)→ GoldenRunEmpty。作用中的 Recipe 1 是 10/29 的,只有 R_e97c9ee6 一個基準,跟現在的比值 key 對不上,所以引擎沒有任何基準、全程 Idle。SECS 錯誤是本機沒有 10.2.81.83 這個 IP。");
}

// ---------------------------------------------------------------- 9. ratio choice
pres.addSection({ title: "新策略" });
{
  const s = content("為什麼改用 N₂ 357:引擎能建得起基準", "新策略");
  const ok = (t) => cellB(t, { bold: true, color: HEX.accent2 });
  const no = (t) => cellB(t, { bold: true, color: HEX.accent3 });
  s.addTable([
    [{ text: "取線(÷ Ar 750 Integral)", options: HDR }, { text: "GR mean/σ", options: HDR }, { text: "SNR", options: HDR }, { text: "precoat 後位置 1", options: HDR }, { text: "DV 500", options: HDR }],
    [cellB("N₂ 357 Integral(356.4 ±1.4)", { bold: true }), ok("27"), ok("29"), cellB("+21 %"), ok("+78–84 %")],
    [cellB("N₂ 357 PeakHeight"), cellB("41"), cellB("15"), cellB("+26 %"), cellB("+62–67 %")],
    [cellB("N₂ 337 RawMean(現場那條)", { bold: true }), ok("39"), cellB("—(σ 判)"), cellB("+7 σ"), ok("+14–19 σ")],
    [cellB("N₂ 337 PeakHeight(寬背景)"), no("4.6"), no("3.1"), cellB("—"), cellB("—")],
    [cellB("CN 388 PeakHeight(現場 ±1.5)"), cellB("22"), no("2.9"), cellB("—"), no("+6 %")],
    [cellB("CN 388 PeakHeight(±0.6)"), cellB("28"), cellB("8"), cellB("+28 %"), no("+4 %")],
  ], { x: 0.6, y: 1.45, w: 12.1, colW: [4.3, 1.8, 1.8, 2.1, 2.1], rowH: 0.55, border: BORDER, fill: { color: C.background1 }, objectName: nm("table") });
  const notes = [["GR 門檻", "mean/σ ≥ 10(逐幀)"], ["SNR 門檻", "5,低於就 LowSignal"], ["兩條都是 N₂", "RequireTwo 擋取線與雜訊的單邊誤報"]];
  notes.forEach(([a, b], i) => {
    const x = 0.6 + i * 4.1;
    card(s, x, 5.55, 3.9, 1.15);
    txt(s, a, { x: x + 0.25, y: 5.65, w: 3.4, h: 0.4, fontSize: 15, bold: true, color: C.text2 });
    txt(s, b, { x: x + 0.25, y: 6.05, w: 3.4, h: 0.55, fontSize: 13 });
  });
  s.addNotes("離線抽法最乾淨的 N₂ 337 PeakHeight 上了引擎就不行:沒漏時只有約 80 counts、一個像素的雜訊占 20 %,mean/σ 只有 4.6,Baseline Builder 直接拒絕。CN 388 用現場的 ±1.5 nm 取線,SNR 中位數 2.9,每幀都是 LowSignal。表中數字是離線模擬引擎取線(Ar Integral、線性背景)的結果,N₂ 357 Integral 與 N₂ 337 RawMean 的基準之後在 App 的 Baseline Builder 上確認(27.9 與 39.2)。沒有第二種物種可以用:看不到 O,CN 對 DV 500 只有 +4–6 %。");
}

// ---------------------------------------------------------------- 10. settings
{
  const s = content("新策略設定", "新策略");
  const role = (t, col) => ({ text: t, options: Object.assign({}, CELL, { bold: true, color: col }) });
  s.addTable([
    [{ text: "比值(全部 Recipe)", options: HDR }, { text: "分子取線", options: HDR }, { text: "角色 · 門檻", options: HDR }],
    [cellB("N₂ 357 / Ar 750", { bold: true }), cellB("Integral 356.4 ±1.4"), role("Alarm ×1.3 / ×1.4", HEX.accent3)],
    [cellB("N₂ 337 / Ar 750", { bold: true }), cellB("RawMean 337.1 ±1.5"), role("Alarm 5 σ / 10 σ", HEX.accent3)],
    [cellB("CN 388 / Ar 750"), cellB("PeakHeight ±0.6"), role("TrendOnly", HEX.accent5)],
    [cellB("O 777 / Ar 750"), cellB("RawMean ±0.5"), role("TrendOnly", HEX.accent5)],
    [cellB("OH 309 / Ar 750"), cellB("PeakHeight"), role("TrendOnly", HEX.accent5)],
    [cellB("CN 388 / Hα 656"), cellB("PeakHeight ±0.6"), role("TrendOnly", HEX.accent5)],
  ], { x: 0.6, y: 1.45, w: 7.3, colW: [2.4, 2.5, 2.4], rowH: 0.55, border: BORDER, fill: { color: C.background1 }, objectName: nm("table") });
  txt(s, "N₂ 337 的 10 σ:位置 1–2 是 +7 σ、DV 500 最小 +14 σ,10 σ 在中間。", { x: 0.6, y: 5.5, w: 7.3, h: 0.6, fontSize: 13, color: C.accent5 });

  card(s, 8.3, 1.45, 4.4, 2.9);
  badge(s, 8.55, 1.65, "C", HEX.accent1, 0.5, 16);
  txt(s, "分類器(同 11/25)", { x: 9.2, y: 1.65, w: 3.4, h: 0.5, fontSize: 18, bold: true, color: C.text2, valign: "middle" });
  txt(s, [
    { text: "N₂ 336.7 / Ar 750.8 RawMean", options: { bold: true, breakLine: true } },
    { text: "> 5 Precoat、< 0.5 Recipe", options: { breakLine: true } },
    { text: "DecideAfterFrames 10(約 11 s)", options: { breakLine: true } },
    { text: "Recipe 門檻 3000、觸發 p99 > 1000" },
  ], { x: 8.55, y: 2.3, w: 4.0, h: 1.9, fontSize: 14, paraSpaceAfter: 4 });
  const chips = ["RequireTwoForAlarm", "averageCount 5", "Batch:Precoat 開批", "片段檔一次替換"];
  chips.forEach((t, i) => {
    const x = 8.3 + (i % 2) * 2.25, y = 4.6 + Math.floor(i / 2) * 0.9;
    card(s, x, y, 2.15, 0.7, C.background2);
    txt(s, t, { x: x + 0.1, y, w: 1.95, h: 0.7, fontSize: 13, bold: true, color: C.text2, valign: "middle", align: "center" });
  });
  s.addNotes("設定在 tools/analysis/20261126/strategy-settings-fragment.json,只替換 logger 四個鍵與 leakMonitor 五個鍵(requireTwoForAlarm、wavelengthCorrections、ratios、processClassifier、batch)。N₂ 357 的背景窗:左 gap 2.0 寬 1.5、右 gap 1.5 寬 1.5,不搜峰。CN 換成 11/25 的 ±0.6 取線,只是為了讓它在趨勢圖上有基準。開發機的 settings.json 已經套用,作用中 GR 是 recipe-20261126。");
}

// ---------------------------------------------------------------- 11. replay 11/26
{
  const s = content("Replay 11/26:只有 DV 500 報警", "新策略");
  img(s, "production-20261126-replay-1126.png", 0.6, 1.4, 6.9, 1200, 717, "11/26 Replay 結束時的 Leak Monitor");
  const rows = [
    ["precoat", "Idle(不判定)", "OK", HEX.accent2],
    ["DV 0 位置 1–2", "+21 % / +7 σ → Warning", "預期內", HEX.accent4],
    ["DV 0 位置 3 · DV 200–400", "−5 … +11 % → Normal", "OK", HEX.accent2],
    ["DV 500", "+82 % / +18.5 σ → 開段後 25 s 報警", "ALARM", HEX.accent3],
  ];
  rows.forEach(([name, res, tag, col], i) => {
    const y = 1.4 + i * 1.2;
    card(s, 7.8, y, 4.9, 1.05);
    txt(s, name, { x: 8.0, y: y + 0.08, w: 3.3, h: 0.4, fontSize: 15, bold: true, color: C.text2 });
    txt(s, res, { x: 8.0, y: y + 0.5, w: 3.4, h: 0.45, fontSize: 13 });
    s.addShape(pres.ShapeType.roundRect, { x: 11.45, y: y + 0.28, w: 1.1, h: 0.5, rectRadius: 0.25, fill: { color: col }, line: { color: col }, objectName: nm("pill") });
    txt(s, tag, { x: 11.45, y: y + 0.28, w: 1.1, h: 0.5, fontSize: 13, bold: true, color: C.background1, align: "center", valign: "middle" });
  });
  txt(s, "GR:當天位置 7–12、每筆 15–40 s · 開發機 App master 0232ba7 · 20× 播放", { x: 0.6, y: 6.4, w: 12.1, h: 0.35, fontSize: 12, color: C.accent5 });
  s.addNotes("precoat 與 16 個 recipe step 用 make_replay_seq.py 串成一個檔(step 間空檔縮成 12 s),20 倍速播一次。每個 step 前 14 幀是 Idle:分類器在第 10 幀才判定類別。DV 500 第一步開段後 25 s 進入 ALARM(判定開始約 11 s + EMA + 15 s 確認),之後一直鎖存。GR 的 6 步其實開著 DV 300 / 400,那天沒有其他平台區的無漏 step;上機要用真的沒漏的 step。");
}

// ---------------------------------------------------------------- 12. replay 11/25 cross-day
{
  const s = content("Replay 11/25:跨日 GR 會誤報", "新策略");
  img(s, "production-20261126-replay-1125.png", 0.6, 1.4, 6.9, 1200, 717, "11/25 Replay 判 11/26 GR 的 Leak Monitor");
  const st = [["+20–30 %", "N₂ 357 平台跨日漂移", "DV 500 也才 +80 %", HEX.accent3], ["+57 % / +11 σ", "位置 1 兩條都過 Alarm 門檻", "只因 step 短、確認沒走完", HEX.accent3]];
  st.forEach(([big, lab, sub, col], i) => {
    const y = 1.4 + i * 1.75;
    card(s, 7.8, y, 4.9, 1.55);
    txt(s, big, { x: 8.05, y: y + 0.1, w: 4.4, h: 0.65, fontSize: 28, bold: true, color: col, valign: "middle" });
    txt(s, lab, { x: 8.05, y: y + 0.75, w: 4.4, h: 0.35, fontSize: 15, bold: true, color: C.text2 });
    txt(s, sub, { x: 8.05, y: y + 1.1, w: 4.4, h: 0.35, fontSize: 12, color: C.accent5 });
  });
  card(s, 7.8, 4.95, 4.9, 1.3, C.text2);
  txt(s, "GR 必須當天重建", { x: 8.05, y: 5.05, w: 4.4, h: 0.5, fontSize: 20, bold: true, color: C.background1, valign: "middle" });
  txt(s, "清腔、換零件、停機超過一天之後也要重建", { x: 8.05, y: 5.55, w: 4.4, h: 0.55, fontSize: 13, color: C.background2 });
  txt(s, "11/25 沒漏氣、每幀 4 s;這一輪暫時用 DecideAfterFrames 3", { x: 0.6, y: 6.4, w: 12.1, h: 0.35, fontSize: 12, color: C.accent5 });
  s.addNotes("同一個 GR(11/26)判 11/25 的 precoat + 弱 step + 19 步。結果整體 WARNING、沒有 composite Alarm,但 N₂ 357 單獨鎖在 ALARM。位置 1–2 兩條的中位數都超過 Alarm 門檻(N₂ 357 +57 %、N₂ 337 +11 σ),只是 4 s 一幀、step 36 s,判定開始後只剩約 24 s,EMA 加 15 s 確認沒來得及同時成立。平台區 N₂ 357 還高 20–30 %,跟 DV 500 的 +80 % 只差三倍。");
}

// ---------------------------------------------------------------- 13. go-live
pres.addSection({ title: "下一步" });
{
  const s = content("上機步驟", "下一步");
  const steps = [
    ["備份現場 settings.json,用片段檔替換 logger 4 鍵與 leakMonitor 5 鍵", "strategy-settings-fragment.json;分類器只能改檔"],
    ["確認取樣:積分 200 ms、averageCount 5", "Ratio Setup 看到 6 條比值、Process class 都是 Recipe"],
    ["當天 precoat 後跑到第 12 步,用 Baseline Builder 建 GR", "勾第 7–12 步 → 每筆 15–40 s → Build → Save as active"],
    ["確認 N₂ 357 mean/σ > 25、N₂ 337 > 35", "OH 沒有基準是預期的"],
    ["每天(與每次清腔後)重建一次", "不要在沒有電漿或只有 precoat 時按 Capture Golden Run"],
  ];
  steps.forEach(([a, b], i) => {
    const y = 1.4 + i * 0.98;
    badge(s, 0.6, y + 0.1, String(i + 1), HEX.accent1, 0.5, 16);
    txt(s, a, { x: 1.35, y: y + 0.02, w: 11.3, h: 0.45, fontSize: 17, bold: true, color: C.text2, valign: "middle" });
    txt(s, b, { x: 1.35, y: y + 0.48, w: 11.3, h: 0.4, fontSize: 13, color: C.accent5 });
  });
  txt(s, "App 從 master 750b0e6 重新發佈(含 #144:Replay 不再寫出 P_Ratio_*)", { x: 0.6, y: 6.5, w: 12.1, h: 0.35, fontSize: 12, color: C.accent5 });
  s.addNotes("步驟詳見 production-20261126-leak-strategy-zh-TW §5。Baseline Builder 的視窗從 15 s 開始,是因為每個 step 開頭的 N₂ 轉換段要約 10–12 幀才退完。11/26 的 16 次擷取都是在沒有電漿時按的,全部被丟掉。#142 是這次 Replay 時發現的 App 問題(重播資料被寫成正式檔名),已由 #144 修正並重新發佈。");
}

// ---------------------------------------------------------------- 14. questions
{
  const s = pres.addSlide({ masterName: "CLOSE_DARK", sectionTitle: "下一步" });
  s.addText("請客戶確認", { placeholder: "title" });
  const qs = [
    "DV 500 的 ROR(與 DV 400 同樣量法)—— 決定下限是 0.1 還是 0.3 Torr/min",
    "ROR 怎麼量的(泵隔離、起始壓力)?腔體容積是否約 64 L",
    "漏率規格是多少(mTorr/min 或 mbar·L/s)",
    "能否補一次 DV 450(或 425、475),DV 500 後關閥看 3 步恢復",
    "製程順序裡的「undcoat」是哪一段(沒有錄到光譜)",
    "有沒有不含 C₂H₂ 的含 Ar 步驟可以拿來測漏",
  ];
  qs.forEach((q, i) => {
    const y = 1.75 + i * 0.8;
    badge(s, 0.8, y + 0.04, String(i + 1), HEX.accent1, 0.45, 15);
    txt(s, q, { x: 1.5, y, w: 11.0, h: 0.55, fontSize: 17, color: C.background1, valign: "middle" });
  });
  s.addNotes("第 1、4 題決定偵測下限與反應是否線性;第 2、3 題決定 GO / NO-GO 的換算;第 6 題是唯一可能大幅改善靈敏度的方向:C₂H₂ 會把漏進來的氧吃掉、讓 N₂ 也被稀釋,在沒有碳氫的含 Ar 步驟上 O 777 與 NO 可能看得到(11/19 實驗機 DV 300 時 NO +30 %)。");
}

d.save();
