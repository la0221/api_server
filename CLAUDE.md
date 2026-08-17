# AIVision

## 🎯 主線目標（勿走偏）

本專案**必要完成的 3 大主項**定錨在根目錄 **`ROADMAP.md`**：
①線上版本控管（模型/軟體）②線上推論＝隔離試模（不污染本地）③線上/本地 × 離線/實時 四格全通。
**每次開新工作前先對照 `ROADMAP.md`**；不屬於主項的需求，先向使用者確認是否支線，避免發散。完成項目時回 `ROADMAP.md` 打勾。

<!-- pm-dashboard-progress-rule -->
## 📊 進度卡：更新 .ai 時務必一併更新（勿遺漏）

**每次更新本專案 `.ai/`（寫 records / daily_log / handoff 等）時，一定要同時更新 `.ai/status.json`。**
即使使用者沒特別提到「進度卡 / status.json / 進度更新」，這也是 `.ai` 更新流程的固定一環，不可略過。

- 怎麼填：見 `.ai/AGENT_進度更新指令.md`（格式、寫法規範、黃金範例）。
- 填完執行（把更新合併回儀表板）：
  ```
  python D:\專案管理\process\build_dashboard.py
  ```
<!-- pm-dashboard-progress-rule -->
