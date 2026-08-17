---
date: 2026-07-16
type: bug_notes
project: AIVision（.NET8 WPF 產線檢測 App）— Edge↔Server 整合
tags: [契約, 校驗, fail-closed, 診斷, raw, multipart, ImageData]
promote_to_pitfall: true
---

# Bug Notes - 2026-07-16

## 問題

`POST /api/infer/pair` 的 `format=raw` 路徑，原本用 **`bytes.Length >= expected`**（長度足夠即放行）校驗 buffer。

自測時把同一張 Bgr24 600×580 的 buffer **故意錯報成 `pixelFormat=Mono8`**（預期應被擋下），結果：

```
HTTP 200
{"objectPresent":false, "hasReading":false, "failureReason":"hough miss (no lens)", ...}
```

錯報寬高（宣告 300×290）也一樣回 200 + `hough miss`。

## 排查

- `>=` 讓「宣告 Mono8（需 348000 bytes）」通過（實得 1044000 ≥ 348000）→ server 拿**前 348000 bytes 當灰階圖**去跑前處理 → 垃圾像素 → Hough 找不到圓 → 回 fail-closed 的 `NoObject`。
- **安全性沒破**：fail-closed 守住了，**沒有**產生「有信心的錯誤讀值」（這是最危險的情況）。垃圾進 → Hough miss → 不分類。
- **但診斷性破了**：`objectPresent:false / "hough miss (no lens)"` 與**「真的沒鏡片」完全無法區分** → **客戶端 bug 會偽裝成有效觀測**。現場排查時，工程師只會看到「一直沒讀到」，不會知道是 edge 送錯中繼。
- 這也違反契約既定原則（`2026-07-14_api_infer_pair_contract.md` §4）：**4xx 留給「請求壞掉」、200 只留給「有效觀測」**。錯報中繼屬前者，卻回了後者。

## 結論 / 修法

`ValidateRaw` 改成**精確比對**：

```csharp
long expected = (long)stride * h;   // stride 省略時 = w * channels
if (bytes.LongLength != expected)
    return $"raw buffer 長度不符：需剛好 {expected} bytes（stride={stride} × height={h}），實得 {bytes.LongLength}。…";
```

正確客戶端一定送剛好 `stride×height`；**長度對不上就是宣告的中繼與實際 buffer 不符**。

**修後實測**：

| 案例 | 修前 | 修後 |
|---|---|---|
| 正確中繼 Bgr24 600×580 | 200 + `M101/01` ✅ | 200 + `M101/01` ✅（不受影響） |
| 錯報 `pixelFormat=Mono8` | 200 + 偽裝成 NO OBJECT ❌ | **400**「需剛好 348000 bytes…實得 1044000」✅ |
| 錯報寬高 300×290 | 200 + 偽裝成 NO OBJECT ❌ | **400** ✅ |

## 下次遇到類似問題，AI 應先檢查

- **`>=` 型的長度校驗是「診斷黑洞」**：它讓錯誤輸入沉默地走完流程，最後以「正常但沒結果」呈現。凡是「宣告的中繼（寬/高/格式/stride）+ 不透明 buffer」的 API，**長度應精確比對**。
- **fail-closed 有效 ≠ 沒問題**：安全性守住只代表「不會做錯決策」，不代表「查得出哪裡錯」。**安全**與**可診斷**是兩件事，兩個都要。
- 判斷回 200 還是 4xx 的準則：**「這是被觀測物的性質，還是請求本身的性質？」** —— 沒鏡片 = 觀測性質（200）；buffer 和宣告不符 = 請求性質（400）。
- 自測時**刻意送錯**（錯格式/錯尺寸/錯型別），別只測 happy path —— 本次就是靠這個發現的。
