---
date: 2026-07-24
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— 模型發布路線（主項1）
tags: [主項1, 模型發布, 版本控管, md5溯源, OCR, 前處理一致性, v6, v9]
status: draft
note: 本日另有選單收斂/批量試模主線（見 01_daily_log、02_bug_notes）；本檔為「模型發布路線」獨立 workstream。
---

# Daily Log - 2026-07-24（模型發布路線 workstream）

## 1. 今日主題

使用者要求：①先確保 AIVision 的辨識/前處理 code 正確；②做「模型發布/更新路線」，用 `D:\Content_lens_OCR\OCR` 的 **v6 系列**測試；③路線順了之後 v9 系列手動更新。本 workstream 直接對應 **ROADMAP 主項1（線上版本控管）**的「版本漂移收斂」與「三件套」病灶。先不上 server（照使用者「先走最簡單方式」）。

## 2. 進度

- **辨識/前處理正確性：實測確認 ✅**
  - 調查 `D:\Content_lens_OCR\OCR` Python 訓練/上線前處理 vs AIVision C# 前處理，**逐項吻合**：Hough(dp1/minDist100/param1 200/param2 30/minR200/maxR300、取半徑最大)、warpPolar(r,2πr)、annulus RInner=0.6、flip→transpose、白底255 letterbox(INTER_AREA)、BGR→RGB、/255、imgsz640、2-pass(0°,90°)max-conf。
  - 唯一理論疑點 normalize（是否含 ImageNet mean/std）→ **harness 實測反證一致**：v6.7.2 讀 M101/08 **conf 1.00/1.00**、混料偵測→氣吹、fail-closed 正確。
  - 提醒：OCR 的 `ocr_demo.py` 用**舊「整圓」warpPolar（無內圈）**，別拿它比對；真正對齊的是 `annulus_polar`(R_INNER=0.6)。

- **v6 模型 md5 全面溯源（直接對症主項1「版本漂移」）**
  - 證實**現有登錄夾 `pairs\*` 大多不是 `Content_lens_OCR` 來源**（早期 `OCR_demo` 鏡像）：v6.7 兩 head、v6.7.1 mohao、v6.7.2 xuehao 的 md5 都與 Content_lens 來源不同。
  - **只有 baseline `v671\mohao`(d42bb1b7) = Content_lens V6.7.1 mohao 來源**；`v671\xuehao`(5d80f690)= Content_lens **V6.7** xuehao（即 baseline 穴號其實借自 V6.7、非 V6.7.2）。
  - 「兩份 V6.7.1」疑點具體化：`v671\mohao`(d42bb1b7, Content_lens) vs `pairs\v6.7.1\mohao`(515a8271, 他源)。

- **發布路線建立 + 實測（本地，最簡方式）**
  - 建 `D:\AIVisionModels\publish_pair_model.ps1`：來源 onnx → md5 → **原子性落地**（.tmp→改名）→ 寫 `_publish.json`（version/來源/md5/時間）→ 報告。ASCII-only（避 PS5.1 讀 UTF-8 中文腳本 brace 誤判的坑）。
  - 實測：發布 Content_lens `V6.7.2`（mohao 6b0c59d3 / xuehao df32efea）→ `pairs\v6.7.2c`；harness paircycle 驗 **M101/08 conf 1.00/1.00、混料/氣吹正確**。→ **發布路線端到端通**（來源→腳本→登錄夾→AIVision可載入→harness驗），且不需改任何程式碼。
  - 附帶結論：Content_lens v6.7.2（xuehao md5 與現有登錄夾不同）讀值等價正確 → 是有效來源。

## 3. 待辦 / 未決

- **【使用者拍板】v6 來源策略**：把 v6 版本全部從 Content_lens 重新發布、統一來源（換 md5、讀值已驗證等價，風險低）？還是保留現有登錄夾、只從此往後用這條路線？`v6.7.2c` 範例留/清？
- **三件套仍缺**：`_publish.json` 已補「來源/md5」治理，但主項1 要的 `.names.json`/`.report.json` 尚未產（雙 head 辨識器靠 onnx 內嵌 names、可不需 names.json，但發布規格要定案）。
- **v9 手動更新（下一步）**：唯一多出來的關鍵環節＝**export（.pt→.onnx，寫入 names、imgsz640）**——OCR repo 無 export 腳本、需自建。坑：穴號新 .pt 是 **19 類含 NG**、舊 onnx 18 類無 NG，export 後必檢類別表；V9.2 穴號在 `yolo_a_V9\runs_v92n\xuehao\`、V9.3 只重訓穴號(模號沿用 v9.2)。
- **建議先跟 ROADMAP 主項1 對齊**再往下（本 workstream 與 server 端模型中樞 API 同屬主項1，避免重疊）。

## 4. 產出

- 新增設計書：`.ai/designs/2026-07-24_model_publish_route.md`（本地優先發布路線；階段一 v6／階段二 v9 export／版本治理／上線前 gate）。
- 新增工具：`D:\AIVisionModels\publish_pair_model.ps1`。
- 新增登錄：`D:\AIVisionModels\pairs\v6.7.2c\`（Content_lens v6.7.2 + `_publish.json`；測試範例，去留待拍板）。
- 更新 `ROADMAP.md` 主項1 現況；`status.json` 同步。

## 5. 今日一句話總結

確認辨識/前處理與 Content_lens 訓練端逐項一致（harness v6.7.2 conf1.00）；完成 v6 全面 md5 溯源（證實登錄夾多非 Content_lens 來源、僅 v671\mohao 是）並建 `publish_pair_model.ps1` 發布腳本，實測 Content_lens v6.7.2→v6.7.2c 讀值正確——模型發布路線（主項1）本地端到端打通，v9 待 export。
