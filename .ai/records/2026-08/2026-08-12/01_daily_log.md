---
date: 2026-08-12
type: daily_log
project: AIVision — 多站實測（算力→HTTP網路延遲）+ 父子節點 POC + exe 打包
tags: [AIVision, 多站, 壓測, HTTP, 父子節點, POC, exe, PyInstaller]
status: final
---

# Daily Log - 2026-08-12

> 承 08-10/11 商品化拍板 → 戰線一 P1 多站。本段把「多站」從紙上推到可跑、可攜的實驗。

## 1. 本機算力壓測（前一段，摘要）

RTX 3050、lens-gpu，不動使用者程式/零污染。兩 pipeline（OCR-CRNN v3 代 v2、母模 diffnet_v2）實跑：
- OCR 單張 p50 54ms、母模 136ms；**多執行緒吞吐快飽和，超過 2 緒只讓延遲膨脹**（母模 4 緒延遲 3.1×、吞吐持平）＝ server 串行鎖的本機縮影。
- **VRAM 僅 224/176MB → 限制是算力非記憶體**；母模 CPU 定心綁死→前處理該分散 edge。
- 產物：`doc/強化策略/benchmark/`（測試計畫/記錄/驗證 12 項全 PASS/報告.html）。

## 2. HTTP 網路延遲測試（父子節點 POC）

建平行資料夾 `父子節點POC/`（**不碰 VISION，通過再議合併**）：
- `parent_server.py`（父=server，收圖→推論→回 JSON 信封，GET/ 狀態頁，engine stub/ocr/lens）
- `child_edge.py`（子=edge，GUI + `--bench` 壓測，keep-alive/TCP_NODELAY）
- `common.py`（協定對齊 `/api/infer/pair` + 多站信封，日後合併省事）
- 實測（真 HTTP + 真 CRNN，M101 150 張）：
  - **有線同網段 HTTP 傳輸≈0.6–1ms** → 端到端≈純推論（37.8ms），網路非瓶頸。
  - 網路延遲直接加單張延遲（+5→+5、+30→+30），但**吞吐可被併發藏住**（1 緒+30ms 砍半 21→12rps，4 緒回 42rps）。
  - **吞吐天花板~46rps 不論純算力或經 HTTP 都一樣 → 再證瓶頸是算力非傳輸。**
  - 結論：產線走有線別 WiFi；真正要解的是算力（P-B/P-C）。
- 產物：`父子節點POC/README.md`、`網路延遲測試結果.md`。

## 3. 打包 exe（明天可丟兩台機器）

用**乾淨 venv**（不污染 lens-gpu）PyInstaller 打包：
- `dist/parent_server.exe`（9MB，stub 免 torch 免 python）+ `child_edge.exe`（11MB，GUI+壓測）+ 4 支一鍵 .bat + 使用說明.txt + .py 原碼。
- **exe 對 exe 經 HTTP 實測互通 OK**。
- 用途：A1000 跑 `1_父_純傳輸測試_假推論.bat`（原名 1_父_啟動_模擬推論54ms，後改名）、隨意電腦跑 `2_子_壓測.bat` 輸入父 IP → 量真跨機網路延遲，**免安裝**。真 GPU 推論另附 `4_...bat`（需該機有 lens-gpu + D:\OCR_demo/Content_lens_OCR）。

## 4. ⚠ .bat 編碼踩雷與根治（重要教訓）

使用者回報 .bat 一堆錯誤。兩層編碼陷阱：
1. UTF-8 + `chcp 65001` → 中文與 `set /p` 被讀壞，`%PIP%` 空 → `--host` 報錯。
2. 改 cp950 又踩 **Big5「0x5C 尾碼」**——中文字第二 byte 是 `\`，cmd 吃掉下一字（`echo`→`cho`）。
**根治（全做）**：①.bat 內容**純 ASCII**（中文說明移 使用說明.txt，提示改英文）②中文預設路徑改 python 程式帶（UTF-8 安全）③不用 `cd`，改 `"%~dp0exe"` 絕對路徑 + `chcp 950`。
**這次每步實測**：從 `C:\` 當工作目錄跑都能找到 exe、IP 抓對、輸出乾淨 ASCII 表格。
教訓：**交付 .bat/腳本前必須實際執行過**（前一版沒測就交，害使用者踩雷）。

## 5. 待辦 / 未決

- 🅰 明天跨機測：A1000 跑 `1_父_純傳輸測試_假推論`（ipconfig 記 IP、防火牆放行 8770）→ 隨意電腦 `2_子_壓測` 輸入父 IP → 看 `p50 − server-ms ≈ 真網路來回`。**未合併 VISION，通過再議**。
- 下一步：跨機數字回來定拓撲；真 A1000 GPU 吞吐（勿用思潔借機）；P-C session 池解串行後重量。
- 🔔 沿用：CRNN 策略文件使用者未給（主動提醒）、ROADMAP 升級三主線待點頭、TimeBudgetMs=120 矛盾、安全地基、vtest-0731 待刪。

## 6. 一句話

多站從「算力壓測」推到「HTTP 父子節點 + 可攜 exe」：證明有線區網傳輸≈免費、瓶頸自始至終是算力；exe 包已備妥、.bat 編碼踩雷已根治並實測通過，明天可直接兩台機器測。

## 7. 追記：GUI tkinter 修正

使用者實測回報：`2_子_壓測.bat`(壓測)完全正常(server-ms 54.5、傳輸~1ms)；但雙擊 `child_edge.exe`(GUI 模式)崩 `_tkinter DLL load failed`——PyInstaller 未包 tk DLL。修正:重建加 `--collect-all tkinter`(GUI 視窗實測可開)+ GUI import 加 try/except 防呆(起不來只印訊息不噴 traceback)。壓測與 GUI 皆實測通過。

## 8. 追記：架構定案（傳輸/Route A/PLC 角色）

釐清並拍板寫入 `2026-08-10_multi_station_plc_architecture.md` §11：
- **傳輸=push**（A 主動 POST 前處理圖給 B，一次來回），不走 pull（B 回頭撈＝多來回+落地+反向連線，更慢更不安全，只離線解耦才用）。POC 已實測 push 傳輸~1ms。
- **Route A 定案**：A 本地存原圖 + 只送前處理小圖給 server（server 只做 GPU 推論=多站吞吐最佳）。四補強：①存原圖非同步+選擇性/輪替 ②送的圖帶前處理版本標籤+無損 ③原圖綁結果可追溯 ④server 掛用手上前處理圖跑本機 ONNX 不停線。
- **PLC vs A 電腦**：PLC=純觸發+致動（不碰圖/不跑我方程式）；**A 電腦=代理/orchestrator**（收相機圖→前處理→存原圖→push server→收結果→Modbus 回 OK/NG 給 PLC）。圖路徑永遠「相機→A→server」，PLC 全程不碰影像。
- 另:demo 直觀化已做(子端 --demo 一張一張+父端狀態頁最新結果大字橫幅+5_子_DEMO.bat);父端狀態頁 % 格式化 bug 修好(ERR_EMPTY_RESPONSE);父端 console 每張印一行。

## 9. 追記：明天跨機測前置（收工前驗一遍）

明天要靠父子 exe 兩台機器測真延遲，收工前把包從頭實跑驗證 + 補最容易卡的防火牆步驟：
- **exe 實跑驗證**（交付前必測）：啟父 exe → `/health` 回 200；子 exe `--bench` loopback 60/60 ok（傳輸≈1ms、server-ms=54）；併發 1/2/4 rps 線性 18→35.6→70.8；`--sim-net-ms 20` 正常（端到端 55→75.7，如實 +20ms）。跑完 taskkill 父 exe、清 smoke log，零殘留。
- **新增 `0_父_開防火牆8770_右鍵系統管理員.bat`**：bundled exe 常不跳「允許連結」框→子端連不上。這支在父機右鍵系統管理員跑一次 `netsh` 放行 inbound TCP 8770（自帶非管理員偵測會擋下並提示）。已實測非管理員路徑正確擋下、exit 1。
- **明天流程更新**：A1000 ①右鍵管理員跑 `0_父_開防火牆8770` ②`ipconfig` 記 IPv4 ③跑 `1_父_純傳輸測試_假推論` → 隨意電腦 `2_子_壓測` 輸入父 IP → 看 `p50 − server-ms ≈ 真網路來回`。

## 10. 追記：Route A 資料流做進 POC（本機 127.0.0.1 先跑通）

使用者要「Route A 這條流先在本機測完，再做真跨機」。前面 child_edge 只證傳輸，還不是 Route A。作法：**不動已測的 child_edge/exe**，另建 `route_a_edge.py` 專跑 Route A，協定沿用同 `/api/infer/pair`；父端只加「回拋 rawId 溯源」小改（向後相容）；`common.Client.infer` 加可選 `extra_headers`。
- **Route A 四補強實作+本機實測（30 張 M101，全用系統 python Python313 含 cv2，不需 lens-gpu）**：
  ① 原圖**非同步**存本機（背景 thread，不擋送圖）→ raw-saved 30/30。
  ② 前處理成小圖+**前處理版本標籤**（X-Preproc-Version）+**無損 PNG**（代表性版：灰階+等比縮 H48）。
  ③ **只送小圖**：**原圖 3.01 MB → 上線只送 0.055 MB＝少 98.2%、小 55 倍**（Route A 吞吐論證首次有實測數字）。
  ④ **原圖綁結果**寫 `manifest.jsonl`，rawId 溯源閉環（manifest rawId == server 回拋 rawId，原圖檔存在磁碟可對回）；**server 掛掉→本機 fallback、不停線**（打死埠實測 LOCAL-FB、原圖照存）。
- **可攜性**：系統 `python`＝Python313（非 anaconda）已含 numpy/cv2/PIL → server 用 stub、前處理在 edge，**本機測 Route A 不需 lens-gpu**。給兩支 ASCII 檔名一鍵 bat：`LOCAL_1_parent_stub.bat`（起父端 127.0.0.1:8770）、`LOCAL_2_routeA_edge.bat`（跑 Route A，Enter=127.0.0.1，跨機改輸父 IP）。**跨機＝把 --host 換父機 IP，程式一字不改。**
- **交付前實測**：route_a_edge 直跑 20/30 張全 server-ok、溯源/fallback/payload 全驗；bat 機制在 ASCII 路徑經 cmd 實跑 30 張通過（中文路徑/檔名只是我方 shell 工具鏈限制，使用者檔案總管雙擊正常）。測完殘產（_routeA_out/battest/fbtest、__pycache__、誤建 127.0.0.1 檔）全清、父端按 PID 關閉，零殘留。
- **未動 VISION、未動使用者程式**（只 import + 讀圖）；前處理為代表性版，接產線 CRNN 正解＝只換 `preprocess_edge()`。

## 11. 追記：child_edge.exe GUI tkinter 崩潰「真正根因」找到並修好

使用者雙擊 `3_子_GUI.bat` 又崩 `ImportError DLL load failed while importing _tkinter`（§7 那次「--collect-all tkinter」其實沒真的解掉）。這次查到根因：
- **buildvenv 是從 anaconda3 建的**，anaconda 的 `_tkinter.pyd` 依賴 `tcl86t.dll`/`tk86t.dll` 放在 `anaconda3\Library\bin\`（非 pyd 旁），PyInstaller 依賴解析找不到 → build.log 明白警告 `could not resolve 'tk86t.dll'/'tcl86t.dll'` → 沒收進 exe → 執行期崩。**`--collect-all tkinter` 只收 tk 資料，收不到那兩個 DLL**。
- **正解**：重打時 `--add-binary "anaconda3/Library/bin/tcl86t.dll;." --add-binary ".../tk86t.dll;."`。在 ASCII 路徑（scratchpad）用 buildvenv PyInstaller 6.22 重建，警告消失。
- **實測交付檔**：新 child_edge.exe（12.6MB）在 dist 直接跑 → GUI 視窗存活不崩 ✓、`--bench` 仍正常（stub 30 張 rps 線性）✓。已覆蓋回 `父子節點POC/dist/child_edge.exe`，建置暫存清乾淨。
- 註：系統 python＝Python313 的 tkinter DLL 與 pyd 同目錄（正常），故 route_a 那套走系統 python 無此問題；只有從 anaconda buildvenv 打包的 exe 才需 --add-binary。教訓延續：**exe 交付前一定要把該模式實際跑起來看**（這次真的把 GUI 開起來驗證才算數）。

## 12. 追記：bat 統一到 dist/ + parent_server.exe 重打成 Route A 版

使用者要「把 bat 統一到 `父子節點POC/dist`」。
- 把 root 的 `LOCAL_1_parent_stub.bat`/`LOCAL_2_routeA_edge.bat` 搬進 dist；補齊 dist 缺的 `route_a_edge.py`、刷新 `parent_server.py`/`common.py`（我加 Route A 後 dist 的是舊版），讓 LOCAL bat 的 `%~dp0*.py` 在 dist 內可取用。**現在 8 支 bat 全在 dist、POC root 無 bat。**
- 順手把 `parent_server.exe` **重打成 Route A 版**（原 exe 是「加 rawId 回拋前」建的）：實測新 exe 對 route_a_edge 會回拋 rawId（edge==server、preprocVer 一致）＝Route A 溯源走 exe 父端也通；exe 8.97MB。
- **全部從 dist 跑 Route A 端到端實測**（parent_server.exe + route_a_edge.py，8 張）：server-ok 8/8、payload 少 98.2%、溯源正常。
- 更新 `dist/使用說明.txt`（cp950）：列全 8 支 bat 分兩類（A 類 exe 免安裝跨機測/demo；B 類 LOCAL 走系統 python 跑 Route A）＋明天跨機流程＋Route A 本機流程。建置暫存全清。
- ⚠ **交付即記**：dist 現在混 exe(免安裝) 與 LOCAL(需系統 python+cv2) 兩類，使用說明已標明各自需求。

## 13. 追記：完整驗收 + 1_父 改名 + 全 bat 轉 CRLF

使用者要「做完測試驗證確認無誤再看」→ 對 dist 做**完整驗收**（8 支 bat 逐一）：1_父 stub `/health`+bench+demo、2_子 bench(rps 線性)、5_子 demo、3_子 GUI(開得起來)、**4_父 真 GPU 實跑真 CRNN 讀值全對**(M101-09→09、M101-18→18、壞圖→no_object，冷啟 1581ms→穩定 93-120ms)、Route A(payload 少 98.2%/溯源 12/12/fallback 3/3)、0_父 防火牆非管理員守門。全過、零殘留。
- **使用者提問**：為何 4_父(真GPU)ms 高、1_父(模擬54ms)ms 低？→ 解釋：1_父=stub 假推論(不跑 AI、固定睡 54ms 回罐頭 M101/02，只量網路)；4_父=真跑 CRNN(~100ms 真算力、讀值會變)。54 是假數字不是效能目標。
- **改名**：`1_父_啟動_模擬推論54ms.bat` → **`1_父_純傳輸測試_假推論.bat`**（消除「54ms＝成績」的誤導）；bat echo、4_父 引用、使用說明.txt(cp950 重寫)、handoff/status/daily_log 全部同步。
- **★發現並根治 bat 行尾問題**：驗改名 bat 時 smoke test 每行第一 token 被吃(`'950' 不是內部指令`)。查位元組＝**Write 產生的 .bat 全是 LF-only**，cmd.exe 對 LF .bat 解析不穩（使用者雙擊有些能跑是運氣）。**8 支全轉 CRLF** 後重測：1_父 正常啟動、echo 正常；2_子 set/p 落預設+bench 150 張 rps 線性。→ **日後任何 .bat 都要 CRLF**。

## 14. 追記：明天跨機實驗文件 + dist 自足化（萬無一失）

使用者要「做測試/驗證文件、確保明天跨機實驗萬無一失」。
- **三份現場文件**（`父子節點POC/明天跨機實驗/`）：`01_測試計畫`（角色/環境需求/T1–T6 項目表/每項步驟/決策邏輯）、`02_驗證與記錄`（讀數公式=`p50−server-ms≈真網路`、Pass/Fail 標準、空白記錄表、異常判讀）、`03_萬無一失_SOP與故障排除`（出發前清單、父機3步、子機操作、故障對照表、一頁速記）。
- **堵最大翻車點＝子機沒圖**：`child_edge` 預設讀 `D:\模號穴號-穩定圖片區\M101`，隨意子機沒這夾→沒圖可送。**解法：打包 `dist\sample_images\`（30 張、3MB），改 `2_子`/`5_子`/`LOCAL_2` 預設吃 `%~dp0sample_images`。** → **子機只要複製 dist 資料夾就自足**（A 類免安裝；B 類 Route A 需 python+cv2）。
- **實測**：①改後三支 bat 全轉 CRLF ②bundled 圖 bench 30 張 rps 線性、Route A 15/15 payload 少 98.2% ③**「全新複製整個 dist 到別處」模擬子機**：1_父 起父端、2_子 set/p 落預設(IP 127.0.0.1、圖=內建 sample_images 顯示於輸出)、bench 30 張 err=0 → 證明零外部依賴可跑。
- 使用說明.txt(cp950) 也更新：標明內建 sample_images + 指向三份實驗文件。殘產全清、無殘留進程。

## 15. 追記：Linux 父端版本（dist/linux/）

使用者要「dist 內做一個父端是 Linux 的版本」。
- **關鍵限制**：PyInstaller 不能跨平台編譯，Windows 這邊**編不出 Linux ELF**。但父端 stub 是**純 Python 標準庫**（確認 parent_server.py 頂層只 import argparse/json/time/threading/http.server，無 common、無 Windows 專屬），Linux 有 `python3` 就直接跑、免 pip。→ 給**原始碼 + .sh** 而非執行檔。
- 產出 `dist/linux/`：`parent_server.py`（同檔跨平台，Linux 只用 --engine stub）、`run_parent_stub.sh`（LF 行尾、+x、自動找 python3/python→exec stub 父端、附 ufw/firewalld/hostname -I 提示）、`README_LINUX.md`（為何無 exe、怎麼跑、LF 注意、想要單檔要在 Linux 上 pyinstaller）。
- **實測**：dist/linux/parent_server.py 用真 python 跑＝正常 stub 父端（health ok、child exe 送圖 5/5 回信封）。.sh 經 bash 跑：cd/echo/找 python 邏輯正常；★我這台 Git Bash 的 `python3` 是 **Windows 商店假 stub**（WindowsApps/python3，只開商店不真跑）才沒起——**真 Linux 上 python3 是真的、會正常啟動**，屬 Windows 測試環境假象非 .sh 缺陷。
- **⚠ Linux 行尾鐵律（與 Windows bat 相反）**：`.sh` 必須 LF，若被 Windows 存成 CRLF→`bad interpreter: /bin/bash^M`。出廠 LF，README 附 `sed -i 's/\r$//'` 修法。
- 實驗文件 01/03 已加「父機是 Linux → 看 dist/linux/README_LINUX.md」指引。

## 16. ★跨機實測抓到真 bug：父端未關 Nagle（每筆 +40ms）+ 已修

實際跨機測試（子=Windows 開發機、父=lh-dmz Linux，因兩機不同網段+FortiGate 隔離改走 SSH 隧道）產出報告 `父子節點POC/doc/2026-08-13_跨機測試報告_lh-dmz.md`。**抓到一個 loopback 測不出、跨機才現形的真 bug**：
- **根因**：`parent_server.py` 的 `Handler(BaseHTTPRequestHandler)` 沒設 `disable_nagle_algorithm`（Python 標準庫預設 False＝Nagle 開），父端回應多次寫入被 Nagle 壓住等 delayed-ACK（Linux ~40ms）→ **每筆請求固定 +~40ms**。子端 `common.py` 有設 TCP_NODELAY、**父端漏了**（README 卻聲稱已關 Nagle，實際只子端做）。
- **實證**：payload 1KB→400KB 傳輸成本幾乎不變（44→48ms）＝固定延遲底噪非頻寬；關 Nagle 後三種 payload 一致 −40ms。修正前後端到端 p50 **99.3→58.8ms**、傳輸成本 **45→4.7ms**、吞吐 **+56~69%**。真網路來回實測 ≈ **3–4ms（100KB 圖）→ 傳輸極便宜、瓶頸在算力**。
- **為何昨天 acceptance 沒抓到**：loopback 不觸發 delayed-ACK（本機軟體開銷僅 0.9ms），8 支 bat 全在本機跑→全 PASS 卻帶此缺陷。**這正是跨機測試的價值**。
- **已修**：三份 `parent_server.py`（根/dist/dist/linux，原 SHA b34f26ca→新 07c4e7b9）加 `disable_nagle_algorithm = True`；**重編 `parent_server.exe`**（原 exe 含舊碼，明天父機為 Windows 雙擊會帶 bug）→ 新 exe sha eb828d32、實測 stub 正常；README 那句「已關 Nagle」改成標明子/父兩端+指向報告。
- **決策更新**：真網路便宜（~4ms）vs 推論 90–120ms → **拓撲採「少數 PC 集中收多站」**，火力集中 GPU + P-C session 池。
- **待辦**：①**查主程式**（若正式 api server 也用 BaseHTTPRequestHandler 極可能同缺陷；註：主程式 AIVision.Api 是 .NET，Python CRNN sidecar 待查）②真 A1000 GPU 吞吐 ③Route A 跨機（T4）。
- ⚠ **安全提醒（報告 §10）**：使用者在 lh-dmz 自行啟動的父端（PID 90408）綁 `0.0.0.0:8770`＝**無認證收圖端點**，目前靠 ufw+FortiGate 雙擋外部進不來，但建議測完 `kill 90408` 或改綁 127.0.0.1。

## 17. 追記：角色對調報告建議照做（加鎖）＋ Route A 可視化

看完 `父子節點POC/doc/2026-08-13_角色對調_真GPU推論測試報告.md`（T5 讀值 37/37 全對、單卡 3050 天花板 ~18 張/秒、併發無效）。做兩件：
- **①照報告 §7 建議加鎖**：`parent_server.py` 的 `OcrEngine`/`LensEngine` 各加 `self._lock`，把 GPU 推論序列化（多執行緒共用同一 torch 模型無安全保證；且併發對單卡吞吐零助益，序列化零損失）。**StubEngine 不加鎖**——保留 stub 併發以維持網路測試（T1–T3 靠 stub rps 隨併發成長證明傳輸有餘裕）。
- **②Route A 可視化（使用者要看：子端 原圖/前處理圖/json、父端 前處理圖/子端原圖位置）**：
  - `route_a_edge.py`：新增存 `preprocessed/`（前處理小圖）+ `json/`（每張一個）+ 產 `index.html` 檢視頁（一列＝原始圖｜前處理圖｜讀值｜JSON）；送圖多帶 `X-Raw-Path` header（原圖在子端位置）。
  - `parent_server.py`：do_POST 收 `X-Raw-Path`、Route A 小圖嵌 base64；狀態頁多「父端收到的前處理圖」面板 + 「子端原圖位置」欄。
- **實測**：起 stub 父端→跑 Route A 6 張→子端 raw/preprocessed/json 各 6 + index.html ✓；父端狀態頁面板顯示前處理圖(base64)+子端實際原圖路徑 ✓。三份 `parent_server.py` 同步（sha ccf7b51b）、**重編 exe**（sha 69720f7a，stub+Route A 面板實測正常）、`route_a_edge.py` 同步進 dist、README 更新。殘產全清。
- 待辦沿用：真 A1000 天花板量測、batch 推論評估、Route A 跨機（T4）、查主程式 Nagle/thread-safety。

## 18. ★使用者抓到 Route A 前處理硬傷：假前處理→真分類器全亂判，已修成真 to_strip

使用者實測 Route A 接真 `--engine ocr` → **分類器全部亂判**。根因：**route_a_edge 的「前處理」是我自己編的代表性版（灰階+縮 H48），根本不是模型要的輸入**；且 server 收到後又跑 `is_strip=False`（自己再前處理一次），雙重錯誤。之前吹的「小 55 倍」其實是**把圖毀掉才那麼小**。
- **正解（讀 `D:/OCR_demo/models/crnn/crnn_infer.py` 得知）**：真 CRNN 前處理＝`to_strip`（`v6_preprocess.find_circle`＋裁圓＋`crnn_dataset.annulus_polar` 極座標展開 → 640×640 strip），再 `read(strip, is_strip=True)` 只辨識。`v6_preprocess` 純 cv2 免 torch；`crnn_dataset` 需 torch（本機系統 python Python313 已有 torch 2.6+cu124）。
- **先驗拆分等價**：`read(img, is_strip=False)`（完整）== `read(edge_to_strip(img), is_strip=True)`（拆分），**8/8 讀值一致**才動手。
- **實作**：`route_a_edge.py` 加 `--preproc {real,none,repr}`（**預設 real**）：real＝真 to_strip 送 strip＋header `X-Is-Strip:1`；none＝原圖直送；repr＝舊假版（標明毀圖僅示意）。real 在無 torch 機器**自動退回 none**（讀值仍正確）。`parent_server.py` 三引擎 `infer(body,is_strip)`、do_POST 讀 `X-Is-Strip` → `OcrEngine` `read(img,is_strip=真)`。
- **真引擎端到端實測**（lens-gpu 父端 `--engine ocr` + edge `--preproc real`，10 張）：**讀值 10/10 全對**（M83-05→M83/05、M101-02→02…）；payload **原圖 101KB→送 31KB＝少 68.6%、小 3.2 倍**（誠實值，非假 55 倍）；父端狀態頁面板顯示**真 640×640 strip**、子端 index.html 前處理欄是真 strip。
- 同步三份 `parent_server.py`（sha b9e73fc0）+ **重編 exe**（36b1e91f）+ `route_a_edge.py` 進 dist；`LOCAL_2` bat 標明「real 需真 CRNN 父端才有正確讀值/無 torch 自動退 none」；README 用表格誠實列 real/none/repr。
- **架構啟示**：真 Route A 前處理需邊緣帶 torch+CRNN 前處理模組（v6_preprocess/crnn_dataset）；lh-dmz 那種無 torch 的 edge 只能走 none（原圖直送）。**教訓：邊緣前處理必須＝模型的真前處理，不能自編佔位。**

## 19. ★使用者糾正：前處理根本不用 torch → 做成自足免 torch 的 edge_preproc，並在 lh-dmz(無 torch)實證

§18 我說「真前處理需 torch」是**錯判**。使用者一句點破：**前處理不做辨識，關 torch 屁事**。查證屬實——讓 `crnn_dataset` 需 torch 的只是同檔的 `to_tensor`/`Dataset`；前處理三函式全純 cv2：`find_circle`/`white_pad_square`（v6_preprocess，本就免 torch）、`annulus_polar`（crnn_dataset，純 cv2 只是住在會 import torch 的檔裡）。
- **做法**：新增自足模組 **`edge_preproc.py`**（只 import numpy+cv2+math），忠實複製 find_circle/white_pad_square/annulus_polar + `to_strip`，**零 torch、零 D:/OCR_demo 相依**。`route_a_edge` 的 real 模式改用它（不再 import crnn_dataset）。
- **驗證**：①`import edge_preproc` 後 `torch not in sys.modules`＝True（免 torch）②`to_strip` 產 strip → 完整 pipeline 讀值 **8/8 一致**。
- **★lh-dmz 實證（無 torch！）**：scp `edge_preproc.py`/`route_a_edge.py`/`common.py` 上去（推對版本 sha de63aae7/8b2bced），用 **cv2 venv `/tmp/routeA-venv`（numpy+opencv-headless、無 torch）** 跑 `--preproc real` → strips 經既有 8775 反向隧道 → Windows ocr 父端 → **讀值 5/5 全對**（M101-01→01…）、payload 原圖 100KB→31KB（少 69%）。**證明 edge 免 torch 做真前處理、辨識在 server GPU。**
- 連線照《連線手冊》：ssh/scp 一律 PowerShell（Git Bash 認不到 ssh-agent）；ssh 命令純 ASCII+`~/*POC` glob（中文路徑/雙引號會被 PowerShell 吃）。全程唯讀+推檔+測試，reports-hub 生產服務未碰；測試殘產 /tmp/_routeA_out 已清。
- **⚠ 密碼安全**：使用者把 sudo 密碼貼進對話（違反連線手冊 §7⑦），且**這次根本不需 sudo/裝 torch**（venv 已有 cv2）→ 未使用。已提醒該密碼已留 transcript、建議事後更換。
- LOCAL_2 bat/README 已改：real 模式標明「只需 cv2、免 torch」。dist 加 `edge_preproc.py`。

## 20. 父端存圖 --save-recv（使用者要「父端收的圖」存到 POC）

先釐清：父端原本**不存圖**（只記憶體留最近 30 筆 base64 供狀態頁，重啟即消失；連線手冊 §8.2 有載）。使用者要存到 `D:\新增資料夾\父子節點POC`。
- **實作** `parent_server.py --save-recv [dir]`（不帶值＝存到 `parent_server.py 旁/_recv_out`；使用者從 POC 根目錄跑 ocr 父端→存到 `父子節點POC/_recv_out`）：do_POST 收到就寫 `<dir>/recv/<rawId><ext>`（實際收到的圖，Route A real 下＝strip）+ `<dir>/json/<rawId>.json`（reading/status/recvBytes/isStrip/preprocVersion/**edgeRawPath 子端原圖位置**）。副檔名靠 magic 判（PNG/JPEG/BMP）。SAVE_LOCK 保寫檔安全。
- **檢視**：新增 `GET /recv`（收圖 gallery：時間/站/收到的圖縮圖/讀值/狀態/bytes/子端原圖位置）+ `GET /recv/<檔>`（服務圖檔）；狀態頁加「💾 收圖存於 … ｜ 看父端收圖→」連結。預設關閉時狀態頁註明「加 --save-recv 才會存」。
- **實測**：①stub+save-recv：recv/json 各存、/recv/<檔> 服務圖 bytes 吻合 ②**真 ocr+save-recv**：存下 json 讀值全對（M83-05→M83/05…）、/recv gallery 5 列 ③exe 重編（sha 48805f80）save-recv 也正常。三份 parent_server.py 同步（sha 825416）。
- **4_父 bat** 已預設加 `--save-recv "%~dp0..\_recv_out"` → 真 GPU 父端自動存到 `父子節點POC/_recv_out`。README 補說明。
- ⚠ 註：stub 網路壓測若開 --save-recv 會每張寫 ~100KB 拖慢吞吐→預設關閉、僅需要時開（真 ocr/Route A 才有意義）。
- **連線手冊更新**（使用者要求）：§8.2 補「父端 --save-recv 存圖 how-to」；§9 速查卡連法 B 加 --save-recv + Route A 補 --preproc real + --out 改 ~/routeA_data（修 /tmp 坑）；§10 修掉**過時錯誤註記**（原寫「真讀值與 Route A 無法同時」已不成立→改成「--preproc real 可同時，本機10/10、lh-dmz無torch 5/5」）。

## 21. 使用者「沒收到，你亂說」＝其 8770 父端是舊行程 + 交接檔

使用者截圖 8770 狀態頁說沒存到圖。查證：**其 8770 父端是舊版行程**（我加 --save-recv 前啟動的），狀態頁完全無存圖字樣（新版就算沒開參數也會顯示「父端未存圖」）＝連線手冊 §7③b「改行為要重啟行程」。**在埠 8781 用新版父端實證存圖成功**：5 張 strip+json 存進 `D:\新增資料夾\父子節點POC\_recv_out`、讀值全對（M83-05→M83/05…）→ 功能為真、非亂說。**卡點：使用者需重啟 8770 加 --save-recv**。
- **📄交接檔已寫**：`.ai/HANDOFF_RouteA正解與父端存圖_2026-08-13.md`（下個 session 先讀）。含本段所有變動、兩機狀態、sha、立即待辦、踩坑、文件地圖、開場動作。

## 22. dist 依用途分夾（連線/真實測試）＋父子環境安裝包＋文件同步（明天萬無一失）

使用者五項需求，全部完成：

1. **dist 一鍵檔依用途重整**（原本 8 支平放）：
   - **通用（最外層）**：`父_開防火牆8770_右鍵系統管理員.bat`。
   - **`連線測試/`**（只傳圖、假推論，exe 免安裝）：`1_父_純傳輸測試_假推論`、`2_子_壓測`、`3_子_DEMO_一張一張`、`4_子_GUI`。
   - **`真實測試/`**（真前處理＋真 GPU）：`1_父_真GPU推論_存收圖`、**`2_子_RouteA真前處理送出_跨機`（★新增，明天主角）**、`本機預演_1_父_stub`、`本機預演_2_子_RouteA`。
   - 移進子夾的 bat 全部改好 `%~dp0..\`／`%~dp0..\..\` 相對路徑（存圖仍落 POC 根 `_recv_out`/`_routeA_out`），且**全 CRLF、cp950**（沿用踩過的雷）。
2. **★新增子端真實測試 bat**：`真實測試\2_子_RouteA真前處理送出_跨機.bat`＝子端像 lh-dmz 那樣做真 `to_strip`（純 cv2、免 torch）→送→自留紀錄（`_routeA_out\`：原圖/前處理圖/json/index.html），對真 GPU 父端就看得到正確讀值。
3. **父/子環境安裝包**（`dist/環境配置/`，依使用者拍板）：
   - **子/（Windows，離線＋線上兩版）**：`requirements.txt`(numpy==2.2.6+opencv-python==4.11.0.86＝已驗證版)＋`wheels/`(離線 52MB)＋`安裝_子_離線.bat`(--no-index --find-links)＋`安裝_子_線上.bat`＋README。**免 torch**（辨識在父端）。
   - **父/（僅線上）**：README 說明兩模式(stub 免裝用 exe／真 GPU)＋`requirements_父_真GPU.txt`(ultralytics 8.4.50+opencv)＋`安裝_父_真GPU_線上.bat`(torch 2.6.0+cu124 從專用源＋ultralytics；另需 D:\OCR_demo 程式權重)。
   - **★實測驗證（萬無一失）**：建乾淨 venv → 離線包 `--no-index` 裝起(numpy 2.2.6+cv2 4.11.0)→ 在**無 torch** 環境跑 `edge_preproc.to_strip` 對 4 張 sample → 全產出 640×640 strip、hough=True、99KB→31KB(正是已驗證 3.2 倍)、`torch not in sys.modules`。裝完刪 venv 零殘留。
4. **兩份專題報告補寫**（使用者指定補進現有報告，不新建總表）：
   - 跨機測試報告 → 附錄 B：Route A 真前處理跨機(免torch,讀值一致)、前處理曾走錯路、`--save-recv` 存圖、**B.4 明天真實測試 R1–R4 待辦表**。
   - 角色對調報告 → 附錄 B：thread-safety 加鎖已落地、**B.2 明天 GPU 面 G1–G4 待辦**(真 A1000 天花板/同網段直連/P-C 池)。
5. **文件同步**：`README.md`／`使用說明.txt`(cp950) 改成新資料夾結構；**明天跨機實驗三份 SOP＋連線手冊＋README_LINUX 的舊 bat 路徑全部批次更新**(兩階段 placeholder 取代，排除已是新路徑的報告，掃過無殘留/無雙前綴)；修 `route_a_edge.py` argparse 舊錯字(real「需該機有torch」→「免torch,只需numpy+cv2」)，同步 dist 複本(新 sha 5304b727)。
- ⚠ 立即待辦不變：使用者的 **8770 父端仍需重啟加 `--save-recv`** 才會存圖（舊行程吃不到）。
- 🔔 沿用提醒：CRNN 策略文件使用者還沒給。

## 23. 收工前「萬無一失」敵意稽核（6 視角 workflow）＋補掉 22 個風險裡的 confirmed 洞

使用者要「今天先這樣、寫 .ai」。收工前開了一個 **6 視角平行敵意稽核 workflow**（子機環境/bat路徑/父機真GPU/網路防火牆/文件一致性/前處理契約，各自讀真實檔找「明天實測會爆的點」），**共回報 22 個風險（0 blocker、5 high）**。獨立視角抓到我自己盲掉的真洞，逐一補好：

**行為修（bat + 程式，防「假成功」最關鍵）**
1. **跨機 3 支子 bat（`連線測試\2_子_壓測`、`3_子_DEMO`、`真實測試\2_子_RouteA`）Enter 預設是 127.0.0.1**＝正好踩「不可打自己」鐵律 → 改**必填 IP**（擋空 Enter、迴圈重問；顯式輸 127 才放行並提示）。
2. **`route_a_edge.py` 連不到父端會靜默本機 fallback、跑完像成功** → 加 **server-ok=0 大聲 `!!!!` 警告**；並加**父端 stub 偵測**（收到 `task='stub'` 就警告「讀值是罐頭 M101/02、非真 CRNN」）——直接防明天打到殘留 stub 或 IP 打錯。實測連死 port 會正確噴警告。
3. **`route_a_edge.py` 缺 cv2/numpy 是原始 traceback**（docstring 還假稱「無 torch 自動退 none」做不到）→ import 包 try/except 給**明確指引**（去跑環境配置\子），docstring 改誠實。（新 sha 288f22f7，root+dist 同步）
4. **真實測試\2_子** 加 **preflight `import numpy,cv2`**、輸出改**獨立 `_routeA_out_real`**（不覆蓋本機預演的 `_routeA_out`）、加**「跑前確認 engine=ocr」**提示。
5. **真實測試\1_父_真GPU** 加 **CUDA preflight 印 `torch.cuda.is_available()`**（驅動太舊會靜默退 CPU＝假 GPU 測試），標頭改「**只需 D:\OCR_demo**」（Content_lens_OCR 只有 --engine lens 要）。

**環境包修**
6. **離線 wheels 綁 cp313，但 README 沒擋 3.14** → README_子 改「只裝 3.13.x、附 release 連結、警告別按官網首頁 3.14」。
7. **`where python` 會命中 Windows 商店假 python** → 子安裝 bat 改**真版本檢查**（`python -c version_info==(3,13)`，擋商店 stub＋擋 3.14），線上版補 pip errorlevel。
8. **父安裝裝到 PATH python、執行卻鎖 lens-gpu python**（兩個直譯器）→ 父安裝 bat 預設 = lens-gpu python.exe（與執行同一支），README_父 強調同直譯器。
9. **requirements_父 釘錯版本**（我寫 ultralytics 8.4.50/opencv 4.11，實測 lens-gpu 是 **8.4.62/4.13**）→ 釘對（ultralytics==8.4.62、opencv>=4.13）。

**網路/文件修**
10. **防火牆只開 TCP 沒開 ICMP → 用 ping 確認會誤判** → 防火牆 bat 補 ICMP 規則＋安全 note（限隔離網、用完刪、netsh 只管 Defender）；SOP/記錄表把 **ping 一律改 `Test-NetConnection 父IP -Port 8770`**。
11. **父機多 IPv4** → SOP 補「挑與子機同網段、避開虛擬網卡」。
12. **01 說「Python313 就有 numpy/cv2」**與其他文件矛盾、且三份 SOP 沒提環境包 → 全改「子機須先裝 環境配置\子」；README.md line 59 同修。
13. **01 T4 允許本機預演當跨機父端**（綁 127、proc-ms 40 破壞公式）→ 改「跨機只用 連線測試\1_父」。
14. 殘留 `4_父`、`8 支 bat`（→12）、`1_父` 歧義、payload「少98%」（→少69%誠實值）等一併修。

**未動（判斷保留）**：防火牆 remoteip 維持 any（改 localsubnet 恐擋掉跨網段測試，改用 note＋Test-NetConnection 佐證）；repr 硬阻擋（明天全用 real，docstring 警語已足）。稽核輸出留於 workflow transcript。
- ✅ 收尾重驗：12 bat 全 CRLF、無殘留舊名、兩 _routeA_out 分離、route_a_edge 編譯+煙測過、python 版本閘門 3.13 過/3.14 擋。

## 24. 使用者提問：有沒有事件 log？→ 沒有，補上父/子各自的中央事件 log（供回填驗證紀錄）

使用者問「子或父有沒有考慮事件 log？沒有的話明天怎麼回填驗證紀錄」。**誠實答：沒有中央 log**——原本只有 console 印（視窗關就沒）、父端記憶體 RECENT 30 筆（重啟即失）、`--save-recv` 只存收到的圖、`route_a_edge` 的 `manifest.jsonl` 還是每跑 `'w'` 覆寫。→ 依要求補上：**每個節點一份 append-only 中央事件 log，任何操作都記**。
- **共用 helper**：`common.py` 加 `event_log(path,event,**fields)`（JSONL、含 ts/pid、thread-safe、**永不因記錄失敗中斷主流程**）+ `log_path_for(role,script)`。
- **父端 `parent_server.py`**（自足、不 import common，因 Linux 複本旁無 common.py）：內建 `elog`，記 `start`(engine/host/port/python/argv)、`engine_ready`(ocr/lens 印 device/CUDA)、每筆 `request`(client/station/task/isStrip/rawId/edgeRawPath/recvBytes/reading/status/ms)、`save_recv`、`health`、`view_recv`、`error`、`shutdown`。加 `--log`（預設 `<程式旁>/_logs/parent_events.jsonl`、`off` 可關）。
- **子端 `route_a_edge.py` + `child_edge.py`**（共用 `child_events.jsonl`）：route_a 記 `routeA_start`/每張 `send`/`warning`(server_ok_zero、parent_is_stub)/`routeA_summary`；child_edge 記 `bench_start`/`bench_result`/`demo_start`/`demo_send`/`demo_end`/`gui_send`。都加 `--log`。
- **★整合煙測（起 stub 父+跑 route_a 真前處理 3 張+/health）**：父 log 6 事件（start/engine_ready/health/request×3，每筆含 edgeRawPath/reading）、子 log 6 事件（routeA_start/send×3/**warning=parent_is_stub 正確偵測**/routeA_summary 完整統計）。兩份皆 append-only、可 grep 可解析。裝完清煙測產物。
- **落點**：預設 `dist/_logs/`（走 bat 時 `__file__`=dist）＝**每台機各自 dist 內一份中央 log**，正合「屬於自己的中央 log」。啟動視窗會印實際路徑。
- **同步**：common/parent_server/route_a_edge/child_edge 皆同步 dist（parent 另同步 dist/linux）。新 sha：common `71d7da2a`、parent_server `39a4dc36`、route_a_edge `a75d4846`、child_edge `e329001`。02/03/README 補中央 log 回填說明。
- ⚠ **exe 尚未含 log**：`parent_server.exe`/`child_edge.exe` 是加 log 前打的，走 exe 的連線測試(stub/壓測/DEMO)要記 log **需重建 exe**；**明天真實測試走 .py（父 parent_server.py、子 route_a_edge.py）已即時記錄**，不受影響。

## 25. 使用者問「電腦不能掃描/不能 TCP、怎麼部署」→ 自帶 USB 網卡拉私網同網段

使用者問：明天若不能掃描、不能 TCP，怎麼部署，是不是整包壓縮隨身碟過去。**先釐清兩層**：①**搬檔案**＝壓縮 `dist` 隨身碟過去（本就自足、離線；子端 `安裝_子_離線.bat` 走內含 wheels `--no-index` 免網，惟需先有 Python 3.13＝沒有就連 python 安裝檔一起帶；父端真 GPU 是線上裝→真 GPU 父機須是已有 lens-gpu 那台）。②**但跨機測試本身靠 TCP 8770 傳圖**，「不能 TCP」要看是哪種。給了決策樹（同網段直連／單機 loopback 零新程式／USB 離線批次待補）。
- **使用者定案：帶 USB 網卡過去，讓兩台到同網段。**＝自帶私網、不碰公司網、IP 自設免掃描 → **真正的跨機測試照常跑，且是同網段直連＝數字最乾淨**（不用 SSH 隧道、免 ~30ms 偏差）。**不需新增程式**。
- 已把「**0.6 現場網路：自帶 USB 網卡拉私網同網段**」寫進 `03_萬無一失`：接線（USB 網卡直連/小 switch，驅動檔也帶）、兩台 USB 網卡設**靜態 IP 同網段**（父 192.168.50.1／子 .2、遮罩 /24、閘道留空）、父跑開防火牆+真 GPU（0.0.0.0 監聽含 USB 網卡）、子 `Test-NetConnection` 驗證後輸**父機 USB 網卡 IP**。踩坑：父機會有多個 IPv4，子機**只能連 USB 網卡那個**；免掃描（IP 自設）。

## 26. 使用者抓到：說「帶 Python 安裝檔」卻沒真的包進去 → 已把 Python 3.13.7 包進子環境包

使用者一句「你是不是忘記包 python 進去…請用我這台的 version」點破：我先前只在文件寫「隨身碟帶 python 安裝檔」，**卻沒真的把它放進交付包**＝正是剛存的記憶 [[build-tools-plan-real-deployment]] 的現行版翻車（只想到、沒做到）。已補：
- 從 python.org 抓**官方 `python-3.13.7-amd64.exe`**（HTTPS 直下、PE 頭 MZ、28.8MB、SHA256 `b12e2e82…37cf2bb`），版本**正是本機 3.13.7**、與離線 `wheels\`(cp313) 一致，放進 `dist\環境配置\子\`。
- **子環境包現在完全離線自足**：`python-3.13.7-amd64.exe` + `wheels\`(numpy 2.2.6+opencv 4.11.0.86) + 兩支安裝 bat + README（總 ~81MB）。子機從零到能跑真前處理：跑內含安裝檔(勾 Add to PATH) → `安裝_子_離線.bat`(--no-index 免網)。
- 兩支子安裝 bat 的 Python 缺失訊息改指向**本夾內含的安裝檔**（不再叫人上 python.org）；README_子 / 03_萬無一失 §0.6 / 使用說明.txt 同步改「已內含、不用另外帶」。
- ⚠ 父端真 GPU 那套仍是線上裝（torch cu124 巨檔），真 GPU 父機仍須是已有 lens-gpu 那台——這點不變。

## 27. 現場網路診斷：手機熱點隔離＋ping 誤導（非網段問題）；做連線檢查小工具

使用者現場回報：手機分享網路下兩台 ping 不到，以為是「網段不同」（我方 `10.208.185.134`、測試機 `10.208.185.209`）。**釐清**：
- `10.208.185.134` 與 `.209` **是同一個 /24**（遮罩 255.255.255.0）＝**網段沒差異**，判斷方向錯了。
- 真正致命＝**手機熱點的 client/AP isolation**：熱點預設**禁止裝置互連**（各自上網、彼此看不到）→ 同網段也 ping 不到、TCP 也不通。（或筆電走熱點、測試機走公司網＝兩個實體網，IP 看似同段卻無 L2 路徑。）
- 使用者手動把兩台 Wi-Fi 改 `192.168.1.105`/`.108` 仍不通＝**還是透過會隔離的熱點 AP**；且 ping 本就被 ICMP 防火牆誤導（我方稽核早已定調「用 `Test-NetConnection`，不看 ping」）。實跑 `Get-NetIPAddress` 佐證：本機 Wi-Fi 確已是 192.168.1.105（手改有生效），但同掛熱點＝隔離照擋。
- **解法＝就用原本要帶的 USB 網卡「一條線直連兩台」**（中間無 AP＝無隔離），設靜態 192.168.50.1/.2，**別用手機熱點當機器間的橋**；熱點只留給要上網的那張卡（雙介面並存）。
- 做了 **`dist\檢查連線_TCP8770.bat`**（通用）：列本機各介面 IPv4（挑同網段那張）＋以 `Test-NetConnection <IP> -Port 8770` 判讀（Tcp=True 就 OK；Tcp=False Ping=True＝父端沒起；兩者皆 False＝L2 不通→USB 直連）。實測 PowerShell 列介面段可跑。
- `03_萬無一失` 故障排除表加兩列：「同網段仍互 ping 不到＝熱點隔離→USB 直連」「ping 準不準＝一律看 TcpTestSucceeded」。← 再次印證記憶 [[build-tools-plan-real-deployment]]（現場網路的實際限制）。

## 28. ★重大自我疏失：bat 在驗證機解析爆炸（我違反自己 08-12 訂的規則且從未實跑）

使用者在驗證機跑 `安裝_子_線上.bat` 得到 **`此時不應有 。。`**、且雙擊會閃退（被迫用 PowerShell 才看得到錯誤）。**根因（已本機 100% 重現）**：
1. **我把中文寫回 bat**——違反我自己 08-12 記下的「**bat 內容一律純 ASCII**（cp950 Big5 尾碼 0x5C 會被 cmd 吃掉），中文只放 使用說明.txt」。
2. **`if (...)` 區塊內的 echo 含未跳脫括號**：`Python 3.13 (64-bit)。`、`(no internet?)`、`(Enter would target your own PC)` 的 `)` **提早關閉 if 區塊** → cmd 報 `. was unexpected at this time`／`此時不應有`。
3. **`pause` 正好在壞掉的區塊內** → 雙擊時視窗直接閃退，使用者只好用 PowerShell。（補：PowerShell 需 `.\x.bat` 是其正常安全設計，非 bug。）
4. **最根本的失誤：我只檢查 CRLF/編碼，從未真的執行過任何一支 bat。**

**修復（全部做完並實測）**：
- **全部 14 支 bat 重寫成純 ASCII**，產生器加**硬性守門**：非 ASCII 直接 `SystemExit` 擋下（防我再犯）。
- 產生器加 **`_esc()` 自動把 echo/rem 行的括號跳脫成 `^( ^)`**（區塊內外皆安全、輸出相同）——根治第 2 類 bug。
- `requirements_父_真GPU.txt` **改名 `requirements_parent_gpu.txt`**（原檔名含中文卻被 bat 執行）。
- **★逐支實跑驗證 harness**（`run_all_bats.py`）：把阻塞/安裝/需管理員的指令中和成 echo，其餘一字不改，**每支測「成功路徑」與「失敗路徑」兩條**（bug 就藏在失敗路徑的 if 區塊）→ 首輪抓出**還有 2 支**未修（安裝_子_線上、2_子_RouteA），修完 **26 項全過**。
- **真跑一次真檔** `安裝_子_離線.bat`：完整跑完、訊息正確、log 有寫。（副作用：本機系統 python 的 opencv 由 4.12.0.88 → **4.11.0.86**＝對齊已驗證版，這是預期行為。）

## 29. 使用者要求「每個狀態都要反映到中央 log」→ bat 層也接上（免 python）

新增 **`dist\_log.bat`**（純 cmd，**Python 還沒裝好時也能寫**）：`call _log.bat <parent|child> <event> "<detail>"` → append 一行 JSON 到 `dist\_logs\<role>_events.jsonl`（wmic 取地區無關的 ISO 時間戳，失敗回退 `%date% %time%`；含 COMPUTERNAME）。
- **每支 bat 的每個狀態都回寫**：`install_start/ok/fail_python/fail_pip/fail_verify`、`firewall_start/ok/fail/need_admin`、`gpu_parent_preflight/launch/exit/fail`、`routeA_bat_start/launch/done/fail`、`netcheck_start/ok/fail`、`stub_parent_launch/exit`、`bench/demo/gui_launch/exit`、`rehearsal_*`。
- **★實測整合**：bat 層 `bat_layer_test` 與 python 層 `routeA_start/send/warning/routeA_summary` **寫進同一份 `child_events.jsonl`**，且每行都能被 `ConvertFrom-Json` 解析；父端 `parent_events.jsonl` 同時有 `start/engine_ready/request`。測完清乾淨。
- 連線測試的 **exe 本身仍不寫 log**（舊版打包），但**其 bat 有寫**（launch/exit），所以「每個操作都有紀錄」這點成立。

## 30. ★★跨機真 GPU 實測成功（2026-08-14，非隧道、真實體網路線）

使用者拿到網路線接上驗證機（`192.168.0.222`，本機 `192.168.0.221` 乙太網路），**當天就把整條鏈路跑通**：

**傳檔（先解決）**：SMB 445 TCP 通但認證三種格式皆 error 86（`000000` 研判是 Windows Hello PIN，不能用於網路認證）→ 改用 **HTTP 傳檔**（`_share_tmp\send_file_server.py` + bat）→ 驗證機瀏覽器下載 `POC_latest.zip`(104.5MB) 成功。
- 過程踩到 3 個坑並修好：①**`python -m http.server` 被我自己的 `Test-NetConnection` 探測卡死**（只做 TCP 握手就斷 → CLOSE_WAIT 堆積 → 之後全部沒回應）→ 改寫成 **ThreadingHTTPServer + 逾時 + 不做反向 DNS + 印出誰連進來**，並實測「半開連線攻擊 5 次後 GET 仍正常」；②**Windows 防火牆有 `python.exe` = Block(Public) 規則壓過我們的 8770 Allow 規則**（Block 優先於 Allow）→ 做 `修防火牆_解除python封鎖_右鍵管理員.bat`；③zip 中文檔名在 URL 變 `%E7%88%B6…` → 改 ASCII `POC_latest.zip`。

**★真 GPU 跨機實測結果（父端中央 log 佐證）**：
| 項目 | 實測 |
|---|---|
| 父端 | `engine=ocr`（真 CRNN），`device=cuda:NVIDIA GeForce RTX 3050 Laptop GPU` ← **確實吃到 GPU** |
| 請求 | **71 筆，全部來自 `192.168.0.222`（驗證機）** |
| 讀值 | **M101/14、M101/15、M101/16、M101/18 … 全部正確** |
| 單張 | ~140–150ms（真 GPU） |
| 存圖 | **71 張**落 `_recv_out\recv\`，`/recv` 檢視頁可看 |

→ **跨機 → 真 GPU → 真讀值 → 自動存圖 → 中央 log 全記錄，整條鏈路今天已打通**（`isStrip=False`＝送原圖、父端做完整前處理；明天的 Route A 才是子端先 `to_strip`）。

**又一個自抓 bug（中央 log 的致命傷）**：`_log.bat` 用 cmd echo 寫 JSON 時**沒跳脫 Windows 路徑的反斜線**（`"detail": "...C:\Users\..."` 的 `\U` 非法）→ **log 寫得出來卻無法程式化解析**＝失去回填價值。已修：`detail`/`event` 的 `\`→`/`、`"`→`'`，並實測含路徑的兩筆皆能被 `ConvertFrom-Json` 解析。（舊的壞行可用「反斜線換正斜線」容錯讀回。）

## 31. ★★★Route A 跨機真實測試「全數通過」＋ 害整天卡關的 header 中文路徑 bug

**報告**：`父子節點POC/doc/2026-08-14_跨機真實測試報告_RouteA成功.md`；`明天跨機實驗/02_驗證與記錄.md` 已補「C0 實測結果」；子端 log 已歸檔 `父子節點POC/_logs_from_child/child_events_2026-08-14.jsonl`。

**成果（真實體網路線、同網段直連，非 SSH 隧道 → 數字最乾淨）**：
| 指標 | 實測 |
|---|---|
| 送達 | **30/30**（`serverOk=30`、`fallback=0`） |
| **讀值** | **30/30 全對**（連歷史誤判樣本 `exp_M83-05_got_M58-05` 也正確讀成 **M83/05**） |
| 引擎 | `engine=ocr` + `device=cuda:RTX 3050 Laptop`，回傳 `task=ocr_pair` |
| payload | 3016KB → **946KB（少 68.6%，3.2×）**；單張 30–32KB |
| e2e | p50 **83.7ms** / p90 91.2 / p99 480.9（p99＝首張 warm-up） |
| 父端推論 | 45.6–102.2ms（平均 **66.1ms**）；30 張 **4 秒**跑完 |
| 雙邊對帳 | 子端 `sentBytes=946KB` ↔ 父端 30 筆 × 平均 31.5KB ≈ 945KB **完全吻合** |

→ **原訂「明天」的主菜（R1/R2/R3、G2/G3）今日已全部完成**。Route A 定案四項（原圖留子端／只送 strip／server 只辨識／溯源可回推）全部驗到。

**★★根因：`X-Raw-Path` header 塞了含中文的路徑（我的 bug，不是網路/防毒）**
- 現象：每張 `LOCAL-FB`、`server-ok=0`，`e2e` 只有 **1–16ms**（瞬間失敗、非逾時），父端 log **零紀錄**。
- 誤導性極強：`child_edge.exe` 送原圖**成功**、驗證機**瀏覽器**開 `/recv` **成功**、`socket.create_connection` 測試 **PYTHON OK**、父端 `/health` **200** → 一路把方向帶去查網路/防火牆/防毒，耗掉使用者一整天。
- 真因：**HTTP header 只能 latin-1**，子機路徑含中文（`…\父子節點POC\…`）→ `http.client` 在**送出前**就丟 `UnicodeEncodeError`，**封包從未離開子機**。`child_edge.exe` 不送這個 header 所以沒事 → 造成「exe 行、python 不行」的假象。
- **我為何沒抓到**：開發機測試一路用 `/tmp`、`C:\…\Temp\…` 等**純英文路徑**，完美錯過。
- 修法：`common.py` 新增 `header_safe()`（所有 header 值非 latin-1 一律 percent-encode）+ 父端 `unquote()` 還原；並補「↳ 送不到父端的原因：<例外>」到畫面與 `send_failed` 事件。
- 驗證：**用含中文的 `--out` 路徑**重跑 → `server-ok=3` 讀值全對；子機實跑 → **30/30**。
- 新 sha：`common 112e620f`、`parent_server ce3dbb0b`、`route_a_edge 94e74dcb`（root/dist/linux 同步）。

**教訓（已寫進報告 §3，並應納入工作紀律）**：
1. **任何進 HTTP header 的值都要 latin-1 安全**（路徑、檔名最危險）。
2. **測試必須涵蓋「含中文路徑」情境** —— 只用英文路徑測 = 沒測到現場。
3. **失敗一定要把真正的例外印出來**：原本只顯示 `(local)`，把所有人導向網路排查；補上原因後 5 秒定位。
4. 呼應既有記憶 [[build-tools-plan-real-deployment]]：現場條件（中文路徑）就是「實際部署」的一部分。

**同日沿路修掉**：bat 純 ASCII+括號跳脫（26 項實跑全過）、`http.server` 卡死改 ThreadingHTTPServer、防火牆 `python.exe=Block` 壓過 Allow、父端推論鎖卡死（GET 正常但 POST 全卡，極易誤判）、`_log.bat` JSON 跳脫、SMB 認證(PIN)改 HTTP 傳檔。


## 31. ★POC 成果併入主程式（2026-08-17）：站端 App × 父端監控 App，實機逐項驗過

使用者拍板「開始把父子實驗結果跟當前軟體結合」，並要求**介面沿用原軟體**、**父子拆成兩個獨立程式**（因為實際部署不在同一台）。

**做出來的東西**
- **站端**（產線機，既有 `AIVision.Presentation.Wpf`）：新增「站端送檢（前處理下放）」頁（`RouteAEdgeView/ViewModel`）——選資料夾→本機前處理→只送小圖→統計（總數/送達中央/本機接管/本機讀出/傳輸量縮減/端到端 p50）+ 逐張表格 + 原圖⇄前處理圖上下對照。
- **父端**（推論機，**新建獨立專案 `AIVision.Presentation.Server`**，AssemblyName `AIVisionServerConsole`）：服務狀態大號誌燈、模型行程池、最近辨識紀錄（待端點）。**只依賴 Infrastructure，不依賴站端專案**，可各自建置安裝。
- **零重造**：前處理直接用主程式既有的 `WarpPolarPreprocessor`（`RInner=0.6/Imgsz=640/PadValue=255`，**與 POC python 版參數完全相同**）；通訊用既有 `CrnnInferClient`；端點就是既有的 `/api/infer/ocr_crnn`。
- **`Harness routea` 煙測工具**（三模式 strip/crop/raw），以後回歸測試一行搞定。

**★核心卡關與根因（使用者一句話點破）**
站端送檢讀值全空、server 回「未偵測到鏡片」。根因：**父端 `CrnnEngine.predict()` 是「全包式」**——不管誰送來都先找圓+展開；站端已前處理的 strip 再被找一次圓當然失敗。使用者說「**誰叫你把已做過前處理的再做一次？我們只有子節點需要做前處理，父節點做辨識**」→ 依此加 `is_strip` 開關（python 2 處：`crnn_engine.predict` / `serve.py`；C# 4 處：SidecarService/Controller/Client/ViewModel），**全部向後相容**（預設 false），python 動前已備份 `.bak_20260817`。修完煙測 **30/30 讀值全對、傳輸量 −68.7%**。

**★本機接管（使用者提的架構補強）**
使用者指出「**我們有本地模型可以自己呼叫，中央掉線就讓本地接管，過一陣子再問中央，沒斷就切回去**」。原本的「本機備援」只是標記 `(local)` 沒有實際辨識 → 改為**注入既有 `IMoldCodePairRecognizerPort`（雙 head ONNX）真正接管**，並加 **30 秒冷卻**（中央掉線後期間不重試，避免每張空等 TCP 逾時 ~4100ms），冷卻期滿自動再試、通了狀態列提示「中央推論已恢復，切回中央辨識」。

**實機逐項驗證結果（`doc/包一包/02_驗證與記錄.md` 已填）**
| 項目 | 結果 |
|---|---|
| A1 站端頁能開 | ✅（修好按鈕 disabled 視覺：Background 要寫在 Style 內才壓得過 IsEnabled Trigger）|
| A2 前處理正確 | ✅（修好 strip 縮圖糊成一條線：顯示時裁掉 letterbox 白邊）|
| A3 讀值 | ✅ **30/30**（含刻意異常樣本 exp_M83-05 正確讀出 M83/05）|
| A4 傳輸量 | ✅ **−68.7%**（POC 68.6%／煙測 68.7%，三者一致）|
| A5/A6 父端監控 | ✅ 橘燈待機→綠燈運作中→紅燈未回應，5 秒自動更新雙向皆正確 |
| B4 父端看得到活動 | ✅ 行程池 `b3 已就緒` |
| C1 中央掉線 | ✅ 不停線；**本機接管 30、本機讀出 29**；第 1 張 4117ms 後**全部 0ms** |
| C1b 切回中央 | ✅ 送達 30/本機 0/讀值 30/30/p50 44ms |

**⚠ 待決策**：本機接管讀值準確率約 **11/18 ≈ 61%**（中央 CRNN `b3` 是 100%）——**引擎/版本差異**（本機是雙 head ONNX `baseline`，較舊）。本機備援定位是「不停線」非「同等準確」，要嘛接受降級運轉、要嘛升級本機模型。
**⚠ 未做**：B 類跨機（本次為單機 loopback）、C2/C3/C4、站端事件記錄檔（數字目前只在畫面上）。
**文件**：`doc/包一包/01_測試計畫.md`（A/B/C/D 四類項目）、`02_驗證與記錄.md`（已填實測）、`03_操作SOP與故障排除.md`。

- **📄交接檔已寫**：`.ai/HANDOFF_主程式整併_2026-08-17.md`（下個 session 先讀）。含架構、is_strip 定案、本機接管、驗證結果、待決策、跨機搬運指引、踩坑與開場動作。
- **📦跨機搬運清單**：`驗證機部署清單_跨機測試.md`（根目錄）——角色分配（開發機=父不可反）、要傳 3 樣約 420MB（站端 bin 376MB self-contained／v671 兩個 ONNX 41MB **絕對路徑寫死要放同位置**／測試影像 3MB）、只改 1 個設定值（BaseUrl）、3 步檢查、6 個已知坑。**文件內每個路徑都已實地驗證存在**。
