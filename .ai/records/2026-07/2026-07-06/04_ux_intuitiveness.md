---
date: 2026-07-06
type: ux_intuitiveness
project: AIVision（.NET8 WPF 產線檢測 App）
tags: [RWD, 比例佈局, 直觀度, WorkOrder, History]
status: draft
---

# 直觀度評估 - 2026-07-06

> 本次主題：延續「固定佈局→比例佈局」逐頁掃，聚焦活躍流程頁（工單管理 / 歷史圖庫 / 辨識過程 / 工單建立）。build 0 錯、App 可啟動（PID 22820 登入畫面）。

## 1. 本次改了什麼（UX 相關）

- **工單管理頁 `WorkOrderManagementView`**：修掉真正的按鈕裁切 bug——DataGrid「操作」欄原設 `Width="*"`，在預設 1200 寬下前面固定欄已吃掉約 1040px，只剩約 120px 給操作欄，但編輯/切換/刪除三顆鈕需 ~200px → 按鈕被裁（正是先前回報「要放大才看到按鈕」）。改「產品名稱」為彈性欄 `*`（MinWidth 120）、「操作」欄改 `Auto` + `MinWidth 210` 保證三鈕永遠完整顯示。視窗補 `MinWidth 900 / MinHeight 500`。
- **歷史圖庫頁 `HistoryView`**：本身已用 WrapPanel 篩選列 + WrapPanel 卡片 + Grid 星號分頁，補上 `MinWidth 900 / MinHeight 560`，避免視窗被拖太小時篩選列擠壓。（另註：使用者待辦「只看混料」篩選其實此頁**已存在**——結果下拉含 Match/TrustInput/MixedAlarm/Skip，可劃掉。）
- **辨識過程頁 `RecognitionProcessView`**：已用 DockPanel + Grid 星號欄 + `Image Stretch=Uniform`，補 `MinWidth 820 / MinHeight 520`。
- **工單建立/編輯頁 `WorkOrderInputView`**：檢視後**不需改**——`NoResize` 固定對話框、表單在 ScrollViewer、按鈕用 UniformGrid（恆等分不裁）。

## 2. 自評（滿分 10，待使用者回覆體感）

| 指標 | 自評 | 理由 |
|---|---|---|
| 體感 | 8 | 工單管理三顆操作鈕不再被裁，最痛點解掉。 |
| 流暢 | 8 | 視窗縮放時按鈕不消失、DataGrid 需要才出水平捲軸。 |
| 導向 | 8 | 未動流程，維持既有「下一步」串接。 |
| 樣式 | 7 | 工單管理仍是淺色 MessageBox 風，尚未做現代深色統一（待辦#2）。 |

## 3. 待使用者回覆 / 待決策

1. **工單管理頁的視窗最小寬 900** 是否夠用？（900 以下操作欄改走水平捲軸而非裁切。）
2. **「只看混料」需求在歷史圖庫已有**（結果下拉 MixedAlarm）——這樣是否算滿足？還是你想要一顆更醒目的快捷鈕？
3. 工單管理頁的**現代深色風統一**（待辦#2）要不要接著做？目前它與雙 head 頁的深色風不一致。
4. 周邊頁（`ProjectEditWindow`、`ModelSelectView`、`Online*/Offline*ManagementView`、`IoPanelView`、`Light*View`）非活躍流程，要現在一起掃，還是等你確認活躍頁體感後再掃？

## 4. 一句話

活躍流程頁的比例佈局掃完（工單管理的操作鈕裁切為本次最有感修正），周邊頁待你決定是否續掃。
