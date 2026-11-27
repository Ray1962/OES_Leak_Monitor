"""把多筆錄檔串成一個 Replay 用的 CSV(docs/production-20261126-leak-strategy-zh-TW.md §4)。

用法: python3 -I make_replay_seq.py <輸出.csv> <錄檔.csv> [<錄檔.csv> ...]

Replay 一次只播一個檔,開始時會重設引擎。把 precoat 與連續的 recipe step 串起來,
一次就能看到分類器、逐 step 的判定與警報鎖存。檔與檔之間的空檔縮成 12 s
(recipe step 之間原本約 11 s),每筆檔自己的幀距不變 —— EMA 與確認時間看的是幀距。
輸出檔名要像錄檔(P_OES1_MMddHHmmss…)才會被 Replay 接受;放在資料夾以外,
不要放進 C:\\DualOES 的日資料夾。
"""
import sys
from datetime import datetime, timedelta

out_path, inputs = sys.argv[1], sys.argv[2:]
GAP = timedelta(seconds=12)
header, cur = None, None
with open(out_path, "w", encoding="utf-8", newline="\r\n") as out:
    for p in inputs:
        with open(p, encoding="utf-8-sig") as f:
            lines = f.read().splitlines()
        if header is None:
            header = lines[0]
            out.write(header + "\n")
        elif lines[0] != header:
            sys.exit(f"{p}: 波長軸跟第一筆不同,不能串")
        ts = [datetime.strptime(l.split(",", 1)[0], "%H:%M:%S.%f") for l in lines[1:]]
        shift = timedelta(0) if cur is None else cur + GAP - ts[0]
        print(f"{p} -> {(ts[0] + shift):%H:%M:%S}")
        for t, l in zip(ts, lines[1:]):
            out.write(f"{(t + shift):%H:%M:%S.%f}"[:-3] + "," + l.split(",", 1)[1] + "\n")
        cur = ts[-1] + shift
print(f"結束 {cur:%H:%M:%S}")
