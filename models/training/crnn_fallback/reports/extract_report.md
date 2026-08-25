# Extract crops from v9.3 error strips — report

Input: `D:\incoming\v9.3vsrcnn\crnn_errors` (896 strips)
Output: `D:\incoming\Content_lens_OCR\data_stable_crops\train`

## Stats

| status | count |
|---|---|
| ok_both | 806 |
| ok_m_only | 47 |
| ok_x_only | 42 |
| detect_miss_all | 1 |

**Detect miss manifest**: `detect_miss_manifest.csv` (90 rows)

## Saved crops per class

| class | saved | prior (data_v671_crops_v2/train) | new_total |
|---|---|---|---|
| 01 | 26 | 294 | 320 |
| 02 | 22 | 287 | 309 |
| 03 | 49 | 309 | 358 |
| 04 | 50 | 362 | 412 |
| 05 | 27 | 312 | 339 |
| 06 | 97 | 307 | 404 |
| 07 | 18 | 318 | 336 |
| 08 | 38 | 286 | 324 |
| 09 | 59 | 297 | 356 |
| 10 | 75 | 277 | 352 |
| 11 | 26 | 328 | 354 |
| 12 | 50 | 327 | 377 |
| 13 | 45 | 320 | 365 |
| 14 | 24 | 305 | 329 |
| 15 | 79 | 320 | 399 |
| 16 | 68 | 302 | 370 |
| 17 | 46 | 316 | 362 |
| 18 | 49 | 320 | 369 |
| M101 | 197 | 220 | 417 |
| M17 | 50 | 252 | 302 |
| M28 | 191 | 261 | 452 |
| M54 | 2 | 231 | 233 |
| M83 | 413 | 238 | 651 |
