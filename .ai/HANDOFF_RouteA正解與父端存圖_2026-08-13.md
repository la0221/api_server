---
date: 2026-08-13
type: handoff（交接檔）
scope: Route A 前處理正解(免torch) + 父端存圖 + 跨機實測 + Nagle/thread-safety 修正
先讀順序: 本檔 → 上一份 .ai/HANDOFF_多站與商品化_2026-08-12.md → 父子節點POC/doc/(三份報告+連線手冊) → ROADMAP.md
---

# 交接檔：Route A 正解 + 父端存圖（2026-08-13）

> 這段對話很長，把父子節點 POC 從「架構驗證」推到「真跨機、真前處理、真讀值、可存證」。
> 商品化/多站的大方向見上一份交接檔（08-12），本檔只記 08-13 這段的變動與現況。

---

## 0. 一句話現況

父子節點 POC 已達成 **Route A 端到端可用**：子端（含 lh-dmz 無 torch 的 Linux）用 **`edge_preproc.py` 純 cv2 做真 CRNN 前處理 → 640×640 strip**，送到父端 `--engine ocr`（`is_strip=True` 只辨識）→ **讀值全對**（本機 10/10、lh-dmz 5/5）、payload 約原圖 1/3；父端可 **`--save-recv` 存收到的圖**。**唯一卡點：使用者的 8770 父端還是舊版行程，要重啟加 `--save-recv` 才會存**（見 §5）。

---

## 0.5 ★2026-08-13 續：dist 分夾 + 父/子環境包 + 敵意稽核硬化（**本段最新，覆蓋下方 §1–§8 的舊 bat 平放敘述**）

**A. dist 一鍵檔已依用途分夾**（下方舊文提到的 `0_/1_/…/LOCAL_` 平放檔名**已不存在**，改成）：
```
dist/
├─ 父_開防火牆8770_右鍵系統管理員.bat          ← 通用（最外層）
├─ 連線測試/  1_父_純傳輸測試_假推論、2_子_壓測、3_子_DEMO_一張一張、4_子_GUI   （exe 免安裝，只傳圖）
├─ 真實測試/  1_父_真GPU推論_存收圖、2_子_RouteA真前處理送出_跨機、本機預演_1_父_stub、本機預演_2_子_RouteA
├─ 環境配置/  子/(離線wheelhouse+線上,numpy+cv2免torch)  父/(僅線上,真GPU torch+cu124)
└─ sample_images/、linux/、共用 .py/.exe、使用說明.txt
```
子夾 bat 用 `%~dp0..\` 取最外層共用程式，全 CRLF+cp950。**明天真實測試主角＝`真實測試\2_子_RouteA真前處理送出_跨機`**（子做真 to_strip→送→自留 `_routeA_out_real\`）。

**B. 父/子環境安裝包**（`dist/環境配置/`）：子=Windows 離線 wheelhouse（numpy 2.2.6+opencv 4.11.0.86）＋線上，免 torch，**已在無 torch 乾淨 venv 實測裝起+to_strip 4/4 產出 640 strip**；父=僅線上（stub 免裝用 exe／真 GPU 跑 `安裝_父_真GPU_線上.bat`，torch 2.6.0+cu124、ultralytics 8.4.62、opencv 4.13，另需 `D:\OCR_demo`）。

**C. ★收工前 6 視角敵意稽核（workflow）抓 22 風險並補掉 confirmed 洞**（細節見 daily_log §23）——與明天成敗最相關：
- **跨機 3 支子 bat 原本 Enter 預設 127.0.0.1**（正踩「不可打自己」鐵律）→ 改**必填 IP**。
- **`route_a_edge.py` 連不到會靜默本機 fallback、跑完像成功** → 加 **server-ok=0 大聲警告** + **父端 stub 偵測警告**（`task='stub'`＝罐頭 M101/02 非真讀值）；缺 cv2/numpy 改印**明確指引**（去裝環境包）而非 traceback。（route_a_edge.py 新 sha **288f22f7**）
- **真實測試\2_子** 加 preflight import 檢查、輸出改**獨立 `_routeA_out_real`**、提示「跑前確認 engine=ocr」；**真實測試\1_父** 加 CUDA preflight 印可用性、標頭改「只需 D:\OCR_demo」。
- 子安裝 bat **擋 Windows 商店假 python / 擋 3.14**；父安裝 bat **鎖同一 lens-gpu 直譯器**；requirements_父 **釘對 8.4.62/4.13**。
- **ping 一律改 `Test-NetConnection 父IP -Port 8770`**（防火牆沒開 ICMP，ping 不通≠埠不通）；SOP 補「多 IPv4 挑同網段、避虛擬網卡」；01 矛盾（「Python313 就有 cv2」）與殘留 `4_父`/「8 支 bat」全修。
- 未動（判斷保留）：防火牆 remoteip 維持 any（只加 note＋Test-NetConnection 佐證，避免 localsubnet 擋掉跨網段測試）。

**D. 明天真實測試 SOP（照這跑）**：
1. 父機：`父_開防火牆8770`(右鍵管理員一次) → `ipconfig` 挑**與子機同網段**的 IPv4 → `真實測試\1_父_真GPU推論_存收圖`（會印 CUDA 可用性；需 lens-gpu+`D:\OCR_demo`）。
2. 子機：先 `環境配置\子\安裝_子_離線.bat` → `真實測試\2_子_RouteA真前處理送出_跨機` → **輸父機 IP（不可 Enter/127）**；跑前先開 `http://父IP:8770/` 確認 **engine=ocr**。
3. 看：子端 `_routeA_out_real\index.html`（原圖/strip/讀值/json）、父端 `/` 與 `/recv`。對照組純傳輸＝`連線測試\1_父` + `2_子`。連通用 `Test-NetConnection`（別用 ping）。

**E. ★中央事件 log（使用者要求，供明天回填驗證紀錄）**：每個節點一份 **append-only JSONL**，任何操作都記一行（含 ts/pid）。
- 父端 → `dist\_logs\parent_events.jsonl`：`start`/`engine_ready`(印 device/CUDA)/每筆 `request`(client/rawId/edgeRawPath/reading/status/ms)/`save_recv`/`health`/`error`/`shutdown`。
- 子端 → `dist\_logs\child_events.jsonl`（`route_a_edge`＋`child_edge` 共用）：`routeA_start`/每張 `send`/`warning`(server_ok_zero、parent_is_stub)/`routeA_summary`；及 bench/demo/gui 事件。
- 預設落 `<程式旁>/_logs/`＝走 dist bat 時是 `dist/_logs/`；啟動視窗會印路徑；`--log <路徑>` 可改、`--log off` 關。已整合煙測（父 6／子 6 事件全寫出，含 parent_is_stub 正確偵測）。
- ⚠ **exe（`parent_server.exe`/`child_edge.exe`）是加 log 前打的舊版 → 走 exe 的連線測試尚未記 log；要記需重建 exe。明天真實測試走 .py（父 `parent_server.py`、子 `route_a_edge.py`）已即時生效。**
- 回填：02 表任何欄位可由這兩份 log 對回（`serverOk`/`localFallback`←子 `routeA_summary`；每張讀值←`send`；父端每筆←`request`）。

**F. POC 檔案新 sha（加中央 log 後，root 與 dist 同步；parent 另同步 linux）**：common `71d7da2a`、parent_server `39a4dc36`、route_a_edge `a75d4846`、child_edge `e329001`（exe 仍舊版，需重建才含 log）。

---

## 1. 這段對話做了什麼（大事記）

1. **exe/bat 硬化**：tkinter GUI 崩→根因 anaconda tcl/tk DLL 在 `Library\bin`，用 `--add-binary` 修好；**所有 .bat 從 LF 改 CRLF**（cmd 對 LF 解析不穩）；`1_父_啟動_模擬推論54ms`→改名 `1_父_純傳輸測試_假推論`（消除「54ms=成績」誤導）。
2. **dist 自足化**：打包 `sample_images/`（30 張），`2_子`/`5_子`/`LOCAL_2` 預設吃內建圖 → **子機只複製 dist 就能跑**。
3. **明天跨機實驗三文件**：`父子節點POC/明天跨機實驗/`（01 測試計畫 / 02 驗證與記錄 / 03 萬無一失 SOP）。
4. **Linux 父端**：`dist/linux/`（parent_server.py + run_parent_stub.sh(LF) + README_LINUX），純 stdlib、只支援 stub。
5. **跨機實測抓到真 bug**：父端 Handler 沒關 Nagle → 每筆 +40ms（loopback 測不出）。已加 `disable_nagle_algorithm=True` 修好。報告 `doc/2026-08-13_跨機測試報告_lh-dmz.md`。
6. **角色對調真 GPU 測試**：Windows 當父、lh-dmz 當子，T5 讀值 37/37 全對；**單卡 RTX3050 天花板 ~18 張/秒、併發無效**。報告 `doc/2026-08-13_角色對調_真GPU推論測試報告.md`。照其 §7 建議：**OcrEngine/LensEngine 加鎖序列化推論**（stub 不鎖，保留網路測試併發）。
7. **Route A 可視化**：子端產 `raw/preprocessed/json/index.html`；父端狀態頁「收到的前處理圖」面板+「子端原圖位置」欄。
8. **★Route A 前處理正解（最重要）**：舊版前處理是我自編的「灰階縮 H48」代表性版→**接真分類器全亂判**（使用者實測抓到）。正解＝真 `to_strip`（find_circle→裁圓→annulus_polar→640 strip）+ server `is_strip=True`。加 `--preproc real/none/repr`（預設 real）。
9. **★edge_preproc.py 免 torch（使用者糾正）**：前處理不做辨識、根本不用 torch。做**自足純 cv2 模組** `edge_preproc.py`，**在 lh-dmz（無 torch）用 cv2 venv 實證讀值 5/5 全對**。
10. **父端存圖 `--save-recv`**：收到的圖寫 `<dir>/recv/*.png` + `<dir>/json/*.json`，新增 `GET /recv` 檢視頁。
11. **連線手冊 + 三報告**：全部寫在 `父子節點POC/doc/`，手冊每條都實機驗過。

---

## 2. ★核心定案/修正（不要再反覆）

- **Route A 真前處理**：子端跑 `route_a_edge.py --preproc real` → `edge_preproc.to_strip()`（**純 cv2、免 torch**）→ 640×640 strip → 帶 header `X-Is-Strip:1` → 父端 `read(strip, is_strip=True)` 只辨識。**讀值與完整 pipeline 一致**。
- **前處理免 torch**：辨識(YOLO+CRNN)才要 torch，在 **server**。edge 任何有 cv2 的機器都能做真前處理。`edge_preproc.py` 是三函式（find_circle/white_pad_square/annulus_polar）的忠實純 cv2 複製，零 torch、零 D:/OCR_demo 相依。
- **`--preproc` 三模式**：`real`（預設，正解）/ `none`（原圖直送，server 自己前處理，讀值也對）/ `repr`（舊灰階縮圖，**毀圖、只能配 stub、勿接真分類器**）。無 cv2 時 real 自動退 none。
- **payload 誠實值**：真 strip ~31KB vs 原圖 ~100KB＝**少 ~69%、小 ~3.2 倍**（不是舊版吹的「小 55 倍」——那是把圖毀光才那麼小）。
- **Nagle**：父端 `Handler.disable_nagle_algorithm=True`（子端 common.py 早有 TCP_NODELAY）。跨機每筆省 ~40ms。
- **thread-safety**：真引擎加 `self._lock` 序列化推論（多執行緒共用 torch 模型不安全，且單卡併發零助益）。**stub 不鎖**（網路壓測要靠併發）。
- **父端存圖**：預設**不存**；`--save-recv [dir]` 才存（recv/ + json/ + `/recv` 檢視頁）。**stub 壓測別開**（每張寫 100KB 拖慢）。
- **算力天花板**：單卡 RTX3050 ≈ 18 張/秒、加併發無效 → 要突破得 **P-C session 池 / 多 GPU / batch**，不是客戶端加併發。真 A1000 天花板待量。

---

## 3. 父子節點 POC 現況（檔案 sha + 用途）

本機 `D:\新增資料夾\父子節點POC\`（三份 parent_server.py 同步）：

| 檔 | sha(前12) | 用途 |
|---|---|---|
| `parent_server.py`（+dist/+dist/linux/ 各一份） | `825416616ffb` | 父端：3 引擎 infer(body,is_strip)、Nagle關、鎖、Route A 面板、`--save-recv`+`/recv` |
| `dist/parent_server.exe` | `48805f80c5d8` | 父端 exe（stub 免 torch；含 is_strip/存圖/面板） |
| `route_a_edge.py`（+dist/） | `288f22f75511`（08-13 硬化後） | Route A 子端：`--preproc real/none/repr`、存 raw/preprocessed/json、index.html、送 X-Raw-Path/X-Is-Strip；**import 守門、server-ok=0 與父端 stub 假讀值大聲警告**（見 §0.5C） |
| `edge_preproc.py`（+dist/） | `de63aae7373d` | **免 torch 前處理**（純 cv2 to_strip） |
| `common.py`（+dist/） | `9630ca8390d9` | HTTP client（keep-alive/NoDelay/extra_headers） |
| `child_edge.py` / `child_edge.exe` | — | 子端送原圖：`--bench`/`--demo`/GUI(tkinter已修) |

**dist bat（8 支，全 CRLF）**：`0_父_開防火牆8770`、`1_父_純傳輸測試_假推論`(stub)、`2_子_壓測`、`3_子_GUI`、`4_父_真GPU推論`(已內建 `--save-recv` 存到 `..\_recv_out`)、`5_子_DEMO`、`LOCAL_1_parent_stub`、`LOCAL_2_routeA_edge`(預設 `--preproc real`)。
**dist 其他**：`sample_images/`(30 張)、`linux/`(Linux 父端)、`使用說明.txt`(cp950)。
**`_recv_out/`**：剛示範存的 5 張 strip+5 json（可刪，是示範產物）。

---

## 4. 兩台機器狀態 + 怎麼連（詳見 doc/連線手冊）

**Windows 開發機**（有 GPU，唯一能真推論）：`192.168.1.221` 等多 IP。lens-gpu conda（torch2.6+cu124、RTX3050）。系統 python(Python313)也有 torch+cv2。
**lh-dmz（Linux）**：`192.168.10.10`(LAN)/`210.68.26.33`(公網)、user `lienhong`、**無 torch**、cv2 venv 在 `/tmp/routeA-venv`（⚠重開機清空）。是 reports-hub 正式機（別碰生產服務）。

- **SSH 一律用 PowerShell**（Git Bash 的 ssh 認不到 Windows ssh-agent → publickey denied）。`ssh lh-dmz` 已設好（config → 210.68.26.33）。
- **兩機不同網段+FortiGate**，直連不通 → 用 SSH 隧道（正向 -L / 反向 -R）；反向 -R 有 ~30ms 固定開銷。**現場同網段直連最準**。
- ssh 命令**純 ASCII**、中文路徑用 `~/*POC` glob、避免巢狀雙引號（PowerShell 會吃）。
- lh-dmz 上已推**新版** `route_a_edge.py`(`8b2bced`)/`edge_preproc.py`(`de63aae7`)/`common.py`；但 **lh-dmz 的 `parent_server.py` 還是舊版**（沒推 save-recv 版；反正 lh-dmz 當子端、Windows 當父，用不到）。
- lh-dmz `~/routeA_data/`：我測 Route A 持久輸出留在那（原圖在此可回溯）。

---

## 5. ⚠ 立即待辦（對話中斷在這）

**使用者的 8770 ocr 父端是「舊版行程」（我加 `--save-recv` 前啟動的），所以沒存圖。** 證據：其狀態頁完全沒有存圖字樣。
→ **要讓 8770 存圖，必須重啟父端**（python 行程啟動後吃不到新碼，連線手冊 §7③b）：
```powershell
# 在 8770 父端視窗 Ctrl+C 停掉，然後：
cd "D:\新增資料夾\父子節點POC"
& "C:\Users\User\anaconda3\envs\lens-gpu\python.exe" -u parent_server.py --engine ocr --host 0.0.0.0 --port 8770 --save-recv
```
從 POC 根目錄跑 → 存到 `D:\新增資料夾\父子節點POC\_recv_out`。看：`http://127.0.0.1:8770/recv`。
（我已在埠 8781 用新版實證存圖成功，5 張 strip+json 就在 `_recv_out\`，讀值全對。使用者截圖前沒重啟才「沒收到」。）
⚠ 重啟 8770 會讓 lh-dmz 8775 隧道打進來的那個父端斷一下（隧道還在，父端重載 CRNN ~10 秒）。

---

## 6. 踩過的坑 / 工作紀律（務必守）

- **改「行為」要重啟行程；改「檔案」要推到對面**（連線手冊 §7③）。← 這段最常踩。
- **.bat 一律 CRLF**（LF 會被 cmd 亂解析）；內容純 ASCII，中文放使用說明.txt。
- **ssh/scp 一律 PowerShell**、純 ASCII、`~/*POC` glob。
- **邊緣前處理必須＝模型真前處理**（不能自編佔位）；且**前處理不需 torch**（辨識才要）。
- **--out 別放 /tmp**（重開機清空）；要留證用 `~/routeA_data`。
- **不動使用者程式**（crnn_infer/judge_core/v6_preprocess/crnn_dataset 只讀不改；edge_preproc.py 是忠實複製非修改原檔）。
- **lh-dmz 是 reports-hub 生產機**：只讀、別碰 ufw/systemd/nginx/生產資料；測完清殘產。
- **密碼別貼進對話**（會留 transcript）；使用者這次貼了 sudo 密碼但根本不需 sudo→未用，已建議更換。
- 每次動 `.ai` 要同步 `status.json` + 跑 `python D:\專案管理\process\build_dashboard.py`。

---

## 7. 相關文件地圖

| 主題 | 位置 |
|---|---|
| 上一份交接（商品化/多站大方向） | `.ai/HANDOFF_多站與商品化_2026-08-12.md` |
| 多站架構設計 §11 定案 | `.ai/designs/2026-08-10_multi_station_plc_architecture.md` |
| 跨機測試報告（Nagle bug） | `父子節點POC/doc/2026-08-13_跨機測試報告_lh-dmz.md` |
| 角色對調真 GPU 報告（算力天花板/thread-safety） | `父子節點POC/doc/2026-08-13_角色對調_真GPU推論測試報告.md` |
| **連線手冊**（Windows↔lh-dmz，每條實機驗過） | `父子節點POC/doc/連線手冊_Windows與lh-dmz.md` |
| 明天跨機實驗（計畫/記錄/SOP） | `父子節點POC/明天跨機實驗/01,02,03` |
| POC 說明 | `父子節點POC/README.md`、`dist/使用說明.txt` |
| 每日流水 | `.ai/records/2026-08/2026-08-12/01_daily_log.md`（§9–§20 是本段） |

---

## 8. 下一步（優先序）

1. **使用者重啟 8770 加 `--save-recv`**（§5），確認存圖 + `/recv`。
2. **真 A1000 GPU 天花板量測**（§3.2 方法；勿用思潔借機）。
3. **P-C session 池解串行**（單卡併發無效已證，這是突破 18 張/秒的正解之一）。
4. **Route A 跨機同網段直連**驗一次（免隧道 30ms 偏差，數字最乾淨）。
5. **查主程式**（AIVision.Api 是 .NET；Python CRNN sidecar 是否有 Nagle/thread-safety 同類問題）。
6. POC 通過後再議合併進 VISION（現在別碰 VISION）。

---

## 9. 給下一個 session 的開場動作

1. 讀本檔 + 上一份交接（08-12）+ 連線手冊。
2. **先問使用者：8770 父端重啟加 --save-recv 了嗎？存圖有出來嗎？**（對話卡在這）。
3. 🔔 沿用提醒：**CRNN 策略文件使用者還沒給**（每 session 提醒）；ROADMAP 升級三主線待點頭。
4. 連 lh-dmz 一律 PowerShell + 純 ASCII + `~/*POC`；別碰 reports-hub 生產。
5. 動 `.ai` 記得同步 status.json + 重生儀表板。
