---
date: 2026-07-02
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— 工單強化 + 比例佈局
tags: [AIVision, WPF, 工單, WorkOrder, MVVM, RWD, 佈局]
status: draft
---

# Daily Log - 2026-07-02

## 1. 今日主題

線下雙 head 模型使用感已良好（見 2026-07-01），今日把相對落後的「工單」拉到可用水準：預期碼與模型連動、目前工單持久化+全頁同步、可編輯；並開始處理全案「固定佈局→比例佈局」的按鈕被裁問題。

## 2. 今日完成事項

- **工單預期碼改下拉**：建/編輯工單的「預期模號」由自由輸入改為**模號/穴號雙下拉**，選項來自目前載入雙 head 模型類別（`IMoldCodePairModelSwitch.CurrentMohaoNames/XuehaoNames`）；NG 不列入模號、「（不核對）」= 不核對。免手打、格式必正確、與模型連動。
- **目前工單持久化 + 全頁同步**：`GetCurrentWorkOrderAsync` 啟動時從 DB 還原「最近 Active」工單（只還原一次，不與 End/Switch 衝突）→ 重啟後不遺失；管理清單加 **★目前 標示 + 目前列高亮 + 自動選取 + 雙擊列＝設為目前工單**。Shell/批量/離線頁本就訂閱 `WorkOrderChangedMessage` → 切換即時同步。
- **編輯工單**：`WorkOrder.UpdateDetails` + `IWorkOrderManagementService.UpdateWorkOrderAsync`；建立表單支援「編輯模式」（`LoadForEdit`：代碼唯讀、產品/批次/預期碼可改、預期碼拆回下拉）；管理清單加「編輯」鈕。編輯目前工單同步記憶體 + 發 `WorkOrderChangedMessage`。
- **比例佈局修正（起手）**：工具列固定寬度 + 水平 StackPanel → 視窗變窄按鈕被裁。改 **WrapPanel（自動換行）** + 動作按鈕**優先配置空間** + 路徑用 `MaxWidth`。已修 `MoldCodePairBatchView`、`BatchInferenceView`。
- **.ai 整理**：研讀 2026-06-04 範例結構後刪除 `.ai/records/2026-06/`；建立 07-01（補昨日）與 07-02 紀錄。

## 3. 今日重要決策

- **先把工單各項拉到平均 8 分，再談線上模型等進階功能**（使用者指定順序）。→ 工單同步/持久化/編輯體感達 8/8/8。
- **全域「目前狀態」必須持久化 + 啟動還原**；清單型選取要標示並自動選取「目前項」，否則使用者以為要重選（見 02_bug_notes Bug 1）。
- **佈局一律用 Grid 星號(*) / DockPanel / WrapPanel 做比例/自適應**，避免固定位置大小（見 02_bug_notes Bug 2）。

## 4. 今日改動摘要（AIVision）

- Domain：`WorkOrder.UpdateDetails(...)`。
- Application：`IWorkOrderManagementService.UpdateWorkOrderAsync`；`WorkOrderManagementService`（UpdateWorkOrderAsync + 啟動還原目前工單）；`IMoldCodePairModelSwitch` 加 `CurrentMohaoNames/XuehaoNames`；`SwitchableTwoHeadRecognizer` 切換時捕捉類別名。
- Presentation：`WorkOrderInputViewModel/.xaml`（模號/穴號下拉 + 編輯模式）；`WorkOrderManagementViewModel/.xaml(.cs)`（★目前/自動選取/雙擊/編輯鈕）；`MoldCodePairBatchView.xaml`、`BatchInferenceView.xaml`（WrapPanel 比例佈局）。

## 5. 尚未完成 / 明日接續

- **比例佈局逐頁掃**：HistoryView、ProjectEditWindow、OfflineTestView、Model*/Online*/Offline*ManagementView、IoPanel/Light* 等仍有固定佈局風險（每頁改完 build+啟動驗證）。
- 工單其餘強化：批量頁 inline 建工單；管理頁改現代深色風 + 切換工單的模型不一致 MessageBox 改非阻斷提示。
- 歷史圖庫「只看混料」篩選鈕。

## 6. 今日一句話總結

工單補齊「預期碼連動模型 / 目前工單持久化+全頁同步 / 可編輯」達 8/8/8，並啟動全案「固定佈局→比例佈局(WrapPanel/星號欄)」的按鈕被裁修正。
