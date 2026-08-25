# CRNN 效果測試報告 — nonar_v931_fix on 前處理區

**Weights**: `D:\incoming\Content_lens_OCR\OCR\crnn_fallback\runs\nonar_v931_fix\best.pt` (ep 24, val 99.96%)
**Detector**: `D:\incoming\Content_lens_OCR\OCR\crnn_fallback\runs\detector\weights\best.pt` conf=0.1
**Source**: `D:\incoming\模號穴號-穩定圖片區\前處理區`

## Overall (per raw image, i.e. per p0/p90 pair)

| 指標 | 數字 |
|---|---|
| Total pairs (raw images) | 5675 |
| **Fully correct** | **5654/5675 (99.63%)** |
| Partial (mo only) | 2/5675 (0.04%) |
| Partial (xu only) | 19/5675 (0.33%) |
| Wrong (both) | 0/5675 (0.00%) |
| Detect-miss (any '?') | 0/5675 (0.00%) |
| Overlap w/ training (was in rehearsal) | 92/5675 (1.62%) |

## Per-mold accuracy

| mold | total | full_correct | acc% |
|---|---|---|---|
| M101 | 1350 | 1348 | 99.85% |
| M17 | 625 | 625 | 100.00% |
| M28 | 1350 | 1331 | 98.59% |
| M54 | 1000 | 1000 | 100.00% |
| M83 | 1350 | 1350 | 100.00% |

## Errors by mold (top-20 confusions)

### M101

| gt | pred | count |
|---|---|---|
| M101-03 | M101-01 | 2 |

### M28

| gt | pred | count |
|---|---|---|
| M28-14 | M4-14 | 6 |
| M28-14 | 14-14 | 6 |
| M28-14 | M2-14 | 4 |
| M28-14 | M48-14 | 3 |

## 產出檔案結構

```
D:\incoming\crnn結果/
├── report.md            ← 本檔
├── summary.csv          ← 每對 raw 圖的推論結果
├── errors_detail.csv    ← 每個錯誤的詳細（gt/pred/信心/路徑）
├── errors/{mold}/gt_{X}__pred_{Y}/*.png   ← 錯誤 strip 對備份
└── detect_miss/{mold}/*.png                  ← detector 沒抓到框的 strip
```

## Notes

- 每對 raw 圖有兩個 strip（p0 和 p90），採 2-pass per-head 選信心高者
- Detector conf 用 0.10（比產線 0.25 低，今日測得可撈回 80% detect-miss）
- Overlap 標示：92 對 raw 圖曾用於 rehearsal 訓練，其餘為完全 unseen
