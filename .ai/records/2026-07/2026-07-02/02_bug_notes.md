---
date: 2026-07-02
type: bug_notes
project: AIVision（.NET8 WPF 產線檢測 App）
tags: [WorkOrder, 全域狀態, 持久化, WPF佈局, RWD]
status: draft
promote_to_pitfall: true
---

# Bug Notes - 2026-07-02

> 註：warpPolar 相機 ROI 誤判、批量頁殘留單 head 下拉兩坑屬 2026-07-01，見該日 02_bug_notes。

## Bug 1：目前工單只存記憶體 → App 重啟後消失 → 每頁像要「重選工單」

### 1. 錯誤情境

設定/切換目前工單後，關閉再開 App（或開發中頻繁重啟），到批量推論/其他頁時目前工單不見了，需重新選。

### 2. 錯誤現象

`WorkOrderManagementService._currentWorkOrder` 是記憶體欄位，重啟後為 null；`GetCurrentWorkOrderAsync` 回 null → 各頁顯示「無工單」。且工單管理清單未標示/自動選取「目前工單」，使用者無從得知現在是哪張 → 體感「每次都要重選」。

### 3. 已嘗試但失敗的方法

（直接定位；訊息機制(WorkOrderChangedMessage)在單一 session 內其實有效，問題是跨重啟遺失 + 清單未標示。）

### 4. 最終原因

目前工單無持久化還原；DB 有 Active 工單但啟動時未載回記憶體。加上管理清單無「目前」標示與自動選取。

### 5. 最終解法

- `GetCurrentWorkOrderAsync` 首次存取且記憶體無工單 → 從 DB 載回「最近一筆 Active」（只還原一次，不與明確 End/Switch 衝突）。
- 工單管理清單：加「目前」★ 欄 + 目前列高亮 + 載入後自動選取目前工單 + 雙擊列＝設為目前工單。

### 6. 下次遇到類似問題，AI 應先檢查

- 「目前 X」這種全域狀態是否有持久化/啟動還原？只存記憶體必在重啟後遺失。
- 清單型選取要標示並自動選取「目前項」，否則使用者以為要重選。

### 7. 是否應升級成避坑指南？

- [x] 已驗證失敗　[x] 容易重複踩坑　[x] 未來應該排除　[x] 對開發決策有約束價值

結論：yes（「全域目前狀態需持久化+啟動還原、清單需標示目前項」是通用陷阱）。

---

## Bug 2：固定寬度 + 水平 StackPanel 工具列 → 視窗變窄時按鈕被擠出畫面（要放大才看得到）

### 1. 錯誤情境

多個頁面（雙 head 測試頁、批量推論頁…）視窗未放大時，工具列右側按鈕（執行/停止/查看歷史）看不到。

### 2. 錯誤現象

按鈕被裁切、需放大視窗才出現。

### 3. 最終原因

- 工具列用 `StackPanel Orientation="Horizontal"`：**不換行**，內容超出視窗寬度就被裁掉。
- 內含**固定寬度**元素（路徑 TextBox `Width="470/480/400"`）先佔位，把後面的按鈕推出可視區。
- `DockPanel` 中先宣告的子項（左側輸入）**優先配置空間**，後宣告的（右側按鈕）空間不足被裁。

### 4. 最終解法（比例/自適應佈局原則）

- 按鈕列：`StackPanel(Horizontal)` → **`WrapPanel`**（空間不足自動換行，按鈕永不消失）。
- 可伸縮元素（路徑）：固定 `Width` → **`MaxWidth` + `TextTrimming`**，不硬撐。
- `DockPanel` 中把**動作按鈕列排在最前**（Dock=Right 但先宣告）→ 優先取得空間，唯讀輸入才被壓縮。
- 通則：**優先用 Grid 星號(*) 欄 / DockPanel / WrapPanel 做比例佈局；避免固定位置大小**。

### 5. 下次遇到類似問題，AI 應先檢查

- 工具列/按鈕列是否 `StackPanel(Horizontal)` + 固定寬度 → 改 WrapPanel + 星號欄。
- 視窗給 `MinWidth/MinHeight`，內容區用 `*` 而非固定 px。

### 6. 是否應升級成避坑指南？

- [x] 已驗證　[x] 容易重複踩坑　[x] 未來應排除　[x] 對開發有約束價值

結論：yes（WPF 佈局通用陷阱；全案應逐頁掃）。已修：MoldCodePairBatchView、BatchInferenceView；其餘頁待掃。
