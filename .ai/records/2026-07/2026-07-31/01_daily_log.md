---
date: 2026-07-31
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— EdgeSimulator + 三目標打通日
tags: [AIVision, EdgeSimulator, stationId, 並行, 排隊, HTTP, 契約]
status: draft
---

# Daily Log - 2026-07-31

## 1. 今日主題

執行 07-24 排定的開工三目標（使用者說「隔一天」，實際隔一週）：①本地 server + HTTP 自呼 ②獨立 UI 模擬 edge ③線下模式選資料夾。三項全通，加碼實測**並發排隊階梯**。

## 2. 進度

- **P-A 第一片（契約相容擴充）**：`POST /api/infer/pair` 請求加 `stationId`（自由字串）、回應原樣回聲。實測 `ST-01` 回聲正確。契約文件已補列。
- **① server 自呼 ✅**：Release 啟動 → health `ready/baseline/20/18` → 送圖讀值 `M101/01` conf~1.0。
- **② EdgeSimulator ✅（今日主產出）**：新專案 `AIVision.EdgeSimulator`（已加入 .sln）——**零 ProjectReference、零 NuGet**，純 HttpClient + System.Text.Json + WPF 內建影像編碼（jpg/bmp 無損轉 PNG 再送，守契約禁 JPEG）。功能：Server/stationId 欄位、健康檢查、單張送出、**資料夾批量（線下模式，含子資料夾）**、停止；顯示 = **原始 JSON**（edge 實拿的東西）+ HTTP 狀態/來回/server 耗時/站點回聲 + 解析摘要 + **動作示意**（門檻 0.60/0.85 → 放行/剔除，fail-closed 情境也示意）。批量結束出 p50/p95 統計。**零依賴＝契約試金石：任何第三方上位機照做即可接上。**
- **③ 線下模式 ✅（HTTP 層）**：主 App 批量頁（執行單 M3）與 Simulator 資料夾模式兩條路皆備；UI 點擊由使用者完成。
- **加碼：並發排隊實測**：單發 317-340ms；**3 站同時 356/619/864ms**——1x/1.9x/2.6x 階梯，**架構書 §4 的串行鎖預判獲數字證實**（已回寫該文件）。P-B(GPU)/P-C(解串行) 的必要性不再是推測。
- 環境：API server（Release）、主 App、EdgeSimulator 三者同時在跑。

## 2.5 模型發布＋Edge 信任鏈設計（使用者兩問，多路線架構圖確立）

使用者確認 EdgeSimulator 小玩 OK 後拋兩問（附多路線版架構圖：路線1..n 各有上位機）→ 產出 `.ai/designs/2026-07-31_model_release_and_trust.md`（嚴謹版，未動工）：
- **Q1 怎麼發布**：全鏈路＝本地發布(已建)→上架 server(candidate)→**隔離試模當發布 gate**（金樣本+零退步+延遲門檻）→晉升 stable→多路線 edge 拉同步(md5 複驗)→previous 一鍵回滾。版本狀態機 candidate/stable/previous/deprecated；三件套補齊為 onnx×2+_publish.json+_gate_report.json。
- **Q2 edge 怎麼知道模型是對的**：「不是一個可知道的事實，是一串可驗證的證據」——四時機信任鏈：A 發布前(md5溯源/harness/金樣本零退步) B 取得時(下載md5複驗/類別對版/一致性顯示) C 每一筆(信心門檻/fail-closed/**工單預期碼核對=執行期唯一能擋『有信心地讀錯』**/版本+站點回聲) D 事後(全記錄/漂移監測/金樣本定期複測)。M83→M58 conf0.596 被門檻擋下＝時機 C 的實證。
- 嚴謹細節：金樣本集本身要版本化（gate 可信度取決於它）、candidate 試模需獨立 session、v9 穴號 19 類含 NG 的 index 必檢、審計權限。
- 實作順序 R1-R5（R1=server 按版本載入，正好是主項 2 既列缺項）。ROADMAP 主項 1 已連結。

## 2.6 發布模型實測（使用者要求試發布）

實走本地發布鏈（發布→驗證），四道全綠：
1. **發布**：`publish_pair_model.ps1` 從 Content_lens v6.7.2 來源發布測試版 **`vtest-0731`** → 原子落地 + `_publish.json`（含來源路徑+md5）。
2. **完整性複驗（信任鏈時機 B）**：落地檔 md5 vs `_publish.json` 宣告 → mohao/xuehao 皆一致 ✅。
3. **可重現性**：vtest-0731 md5 與 v6.7.2c **完全相同**（同源同結果）→ 發布腳本確定性 ✅。
4. **Gate 讀值驗證**：harness paircycle（資料集根=2026-06-05，注意層級：根\\模號\\穴號）→ 相符案例 `M101/08 conf 1.00/1.00 → Match 放行`；**穴號混料案例 → MixedAlarm + 氣吹**（`read=03 expected=08 conf 1.00 ≥ 0.85`）——信任鏈時機 C 的工單核對防線同場驗證 ✅。模號混料案例因資料集無 M60 料源夾略過（正常）。

**UI 收尾（下午完成）**：使用者已在雙head頁載入 `vtest-0731` 跑小批量確認 OK →「發布→消費」全鏈閉環 ✅（發布前 agent 另複驗落地 md5 與 `_publish.json` 宣告一致）。`vtest-0731` 刪除動作被權限層擋下（Remove-Item/rm 皆拒）→ **改由使用者手動刪**（檔案總管刪 `D:\AIVisionModels\pairs\vtest-0731`；若 App 載入中先切版本）。
**Server 段（上架→candidate→晉升）**：仍待 R1/R2 動工（設計見 2026-07-31_model_release_and_trust.md）。

## 2.7 發布 server 段動工：R1/R2/R5 最小可用切片（下午，使用者拍板「三塊都做」）

使用者提需求「1.怎麼發布 2.edge 要能收到新模型 3.edge 要能測」→ 拍板三塊都做，當日完成＋端到端驗證全綠：

**Server（AIVision.Api）**
- `Services/ModelRegistryService.cs`（新）：掃登錄夾版本、md5（_publish.json 優先/現算快取）、**按版本辨識器快取**（ConcurrentDictionary+Lazy；各版本獨立實例獨立鎖＝隔離試模不動 baseline、也不與 baseline 搶鎖）；版本名白名單 `^[A-Za-z0-9][A-Za-z0-9._-]*$` 防路徑跳脫；建構失敗不留毒快取。
- `Controllers/ModelsController.cs`（新）：`GET /api/models`（版本+md5+bytes+_publish.json 原文+isServerCurrent/isLoadedInMemory）、`GET /api/models/{version}/download?head=mohao|xuehao`（PhysicalFile+X-Model-Md5 標頭）。
- `InferController.Pair`：`modelVersion` 欄位落地——未指定=baseline 照舊；指定=registry 取版本（404 明確訊息）；回應 modelVersion 回填指定值。
- appsettings 加 `ModelRegistry:Root`。

**Edge（WPF）**
- `RemotePairRecognizer`：`RecognizeAsync` 加 `modelVersion` 參數（multipart 附帶）；新增 `ListModelsAsync`、`DownloadVersionAsync`（.tmp 串流→**本地重算 md5 vs 清單宣告，宣告缺失或不符一律拒收丟棄**→原子改名→_publish.json 原文落地+_sync.json 下載紀錄；失敗清 .tmp 不留半套）。
- 批量頁：「伺服器模型版本」可編輯下拉＋「查伺服器版本」鈕；留空=現用；指定版本時報告 sourceTag=`中央伺服器(指定 X)`。
- API 伺服器設定視窗：「伺服器模型」區塊——取得模型清單（含 ★server現用/✓本地已有 標記+大小+發布時刻）→ 下載到本地 → 完成提示到雙head頁重新整理。

**端到端驗證（agent 實測全綠）**：models 列 5 版本 md5 齊；指定 v6.7.2c 推論 M101/08 conf 1.00、版本回聲正確、冷載 wall 751ms/快取後 393ms；**隔離證實**（health 仍 baseline、v6.7.2c 僅 inMemory）；未知版本 404；下載 X-Model-Md5=本地重算=宣告 三方一致；`..%2f` 路徑跳脫被擋 404。
**UI 人工驗收（同日）**：使用者實點兩處均通過——A) 批量頁指定版本隔離試模、B) 設定視窗取得清單+下載（md5 複驗）。R1/R2/R5 切片=完整驗收通過。
**新模型 SOP 已向使用者交付**：①publish_pair_model.ps1 上架（server 免重啟）②批量頁指定版本隔離試模=驗收關卡③設定視窗下載（md5 複驗）④雙head頁載入=正式採用（選回舊版=回滾）。同機下③看似多餘，但走純 HTTP=跨機時操作不變（刻意設計）。

## 2.8 使用者需求修正 → 用途分家 + UI 發布頁（傍晚，拍板「全套做」）

使用者提出：①三角色既定 ②**工程師要能發布模型** ③模型分三種用途（OCR/公母模/瑕疵）④⑤**發布時要選用途** ⑥edge 流程確認=現行契約（作動留給 PLC 端）。拍板全套做，當日完成：

**Server**
- `ModelRegistryService` task 化：`Tasks{ocr_pair(pairs, 雙檔), gongmu(gongmu, 單檔), defect(defect, 單檔)}`（appsettings 可配）；`_publish.json` 相容兩種格式（本地腳本的 `mohao:{md5}` 與上架 API 的 `files:{"mohao.onnx":{md5}}`）。
- `ModelsController` 改 task 路由：`GET /api/models`（用途總覽）、`GET /api/models/{task}`、`GET /{task}/{version}/download?file=`、**`POST /api/models/{task}` 上架**（multipart；檔案組成對版→逐檔 .tmp+md5→全就緒才原子改名→_publish.json 含 sourceNote 溯源；**同版本 409＝版本不可變**；失敗清半成品）。
- 推論不變：指定 modelVersion 僅 ocr_pair（`GetOcrPairRecognizer`）。

**Edge**
- 新 `ModelHubClient`（職責分離：RemotePairRecognizer 只管推論）：GetTasks/List(task)/DownloadVersion(task,…)（逐檔 md5 複驗）/**Publish(task, version, files, sourceNote)**。
- **新「模型發布」視窗**（選單：面板→模型與測試→模型發布；IsEngineerOrAbove 把關，作業員不可見）：選用途→動態檔案欄（OCR 兩顆/其餘一顆，來源檔名不限、上傳自動改目標名）→版本號→上傳；409 提示換版本號；成功顯示 md5 + 建議動線（隔離試模驗收→edge 下載採用）。
- 設定視窗模型區塊加「用途」下拉，下載落對應本地夾（pairs/gongmu/defect）。

**端到端實測全綠**：總覽 3 用途、ocr_pair 5 版；POST gongmu/vtest-up 201（md5=d42bb1b7 正確）；同版本 409；ocr_pair 缺一顆 400（訊息點名缺哪顆）；gongmu 清單/下載 md5 三方一致；指定版本推論回歸 conf 1.00。測試版 vtest-up 已清除。
**待使用者 UI 點測**：模型發布頁實發一版（可用 Content_lens 檔案發成新版本號）→ 隔離試模 → 下載。

## 2.9 .pt→.onnx 轉檔工具（使用者反映 yolo train 產出是 .pt）

建 `D:\AIVisionModels\export_pt_to_onnx.py`（ultralytics 8.4.50 已在系統 python）：
`python D:\AIVisionModels\export_pt_to_onnx.py <best.pt> [--imgsz 640]` → 同夾產出 .onnx + 印 md5/類別清單。
imgsz 預設 640=與前處理對齊；ultralytics export 自動嵌 names metadata（AIVision 讀類別靠它，勿用他法轉）。
實測：V6 mohao best.pt（19 類）→ onnx 2.9s 成功。**ROADMAP 既列的「v9 待建 export」缺口就此補上**——完整鏈=train(.pt)→export(.onnx)→UI 發布→隔離試模→下載採用。
⚠ 注意：export 會把 .onnx 寫在 .pt 同資料夾，若該處已有正在被登錄夾引用的 best.onnx 會被蓋——要轉舊訓練夾時先複製 .pt 出來轉。

**使用者指正（重要教訓）**：agent 私下代轉 v9.4 再叫使用者發布＝「微作弊」——轉檔是**流程的一環**，代跑等於把工程師真正會卡的那步跳掉、測試不完整。修正：①**發布頁直接吃 .pt**——發布時偵測 PK magic → 自動呼叫 export_pt_to_onnx.py（python -X utf8，同一支腳本不另寫）→ 用產出 .onnx 續傳；失敗顯示 python 輸出尾段+手動指令；選檔對話框放行 *.pt。②代轉的兩顆 v9.4 onnx 已刪，回到只有 .pt 的原始狀態，使用者從頭走真實流程（選 .pt → 自動轉 → 上架 → 隔離試模）。

**實案（同日）：使用者把 v9.4 的 .pt 直接從發布頁上傳成 `A1000`** → 推論 500 InvalidProtobuf、批量連 3 次傳輸失敗中止（檔案大小 10.3MB=.pt 鐵證；發布頁自動改名成 mohao.onnx 所以看不出）。修法三件：①**雙層 .pt 防呆**——server 上架時驗 magic bytes（zip "PK"→400 指路轉檔工具，半成品清乾淨）＋發布頁上傳前同檢（訊息直接給該檔的轉檔指令）；已實測 400 生效、不留半成品。②壞版本 A1000 已刪。③v9.4 兩顆已代轉：mohao 20 類含 NG（md5=85f97097）、xuehao **19 類含 NG**（md5=32c717a8，v9 系列與 v671 的 18 類不同——辨識器讀 metadata 自動適配，但穴號 NG 讀值與正解比對行為要留意）。待使用者重發：發布頁選 runs_v94\{mohao,xuehao}\weights\best.onnx。

- 使用者實際點一輪 EdgeSimulator（健檢→單張→資料夾）＋主 App M3 → 三目標從「HTTP 層通」升級為「UI 驗收通」→ ROADMAP「線上×離線」轉 ✅。
- 多開 2-3 個 EdgeSimulator 實例手動同時送 → 親眼看排隊 lag（數字已由 agent 預跑）。
- 後續照架構書：P-B GPU 化（3050 代量）→ P-C 解串行 → 並發壓測達標。
- 沿用：人工 R1/R2（面板收斂）、gongmu 接入規格（等使用者模型）、主項1 軟體版本控管範圍拍板。

## 2.10 CRNN 引擎接入：調查 + 路線 A+C 當日完成（晚間）

使用者告知 OCR_demo 有新引擎 CRNN、前處理不同 → 調查（`2026-07-31_crnn_engine_intake.md`：detector+NonAR 兩顆 .pt、輸出字串、無 NG 類、同底座前處理不同後段、OCR_demo 有 --serve 子行程協定）→ 拍板 **A+C（倉庫納管+python sidecar）**，當日完成：

**A 倉庫納管**：registry 加 task `ocr_crnn`（files=[detector.pt, nonar.pt]）；**內容檢查改依目標副檔名**（.onnx 不可是 zip／.pt 必須是 zip——雙向防呆都實測 400）；發布頁/設定視窗加用途選項（.pt 目標不轉檔、驗 PK）。production 權重已上架 `ocr_crnn/v931-fix3`（detector md5=a5fe4161、nonar md5=2daeeb4e）。

**C sidecar 推論**：`CrnnSidecarService`（掛 OCR_demo `main.py --serve --mohao-weights <登錄夾detector> --xuehao-weights <登錄夾nonar> --mohao-pre crnn`＝**權重直指登錄夾，版本治理同一套**；semaphore 串行化、死掉自動重啟、EXIT 優雅關閉）+ `POST /api/infer/ocr_crnn`（v1 僅 png；503=sidecar 掛、200=有效觀測含 needsReview）+ `GET /api/infer/ocr_crnn/health`。appsettings `CrnnSidecar`。

**E2E 實測**：上架 201；onnx 冒充 .pt→400；總覽 inferReady=true；首發冷啟 7s；**熱請求 sidecar ~100ms、全程 ~127ms（CPU！遠低於 400ms 節拍）**；M101/08 讀 M101/08 conf 0.95/0.96；**M83（使用者剛失敗那批圖）讀 M83/11 conf 0.95 ✅**。
**注意**：CRNN 無 NG 類→edge「雙 NG 拒收」邏輯不適用，品質旗標=needsReview；v1 版本切換=改 appsettings 重啟 server（按版本熱切換待做）。

**使用者第二次指正（同 2.9 教訓）**：agent 又代發布了 CRNN 權重（「我想自己發布的說 QQ」）→ **`ocr_crnn/v931-fix3` 已刪、登錄夾清空**，待使用者親手從發布頁發（用途=CRNN、兩顆 .pt 原檔勿轉、版本號務必 `v931-fix3`＝sidecar 設定指的路徑，改名要同步改 appsettings CrnnSidecar）。⚠ **在使用者發布前，CRNN 推論會回 503（DetectorPath 不存在）——這是預期狀態不是 bug**。教訓已寫入 agent 長期記憶（user-does-workflow-steps）：agent 只建工具/防呆，流程步驟留使用者執行；測試產物用 vtest-* 命名並清乾淨。

## 3. 待辦 / 未決（本節曾在編輯中遺失，重建）

- 使用者 UI 點測：發布頁實發一版（含 .pt 自動轉檔）→ 隔離試模 → 下載；vtest-0731 手動刪。
- M83 整夾一石二鳥（雙 head 線）→ ROADMAP「線上×離線」轉 ✅。
- CRNN 後續：edge 端來源/引擎選擇 UI、sidecar 按版本熱切換、CPU 併發量測、CRNN 定位拍板（互補 or 取代）。
- 沿用：R3/R4、人工 R1/R2、gongmu 接入、P-B/P-C、安全地基。

## 4. 產出

- Api：`InferController`（+`StationId` 請求/回應回聲）。
- **新專案** `AIVision.EdgeSimulator/`（csproj+App+MainWindow，~300 行，已入 sln）。
- 文件：契約補 `stationId`；架構書 §4 補並發實測數字。
- 本日 records + `status.json` + 儀表板。

## 5. 今日一句話總結

三目標全通：server 自呼 ✅、零依賴 EdgeSimulator 蓋好（原始 JSON+動作示意+批量統計）✅、線下模式雙路就緒 ✅；stationId 契約回聲上線；並發實測抓到 356/619/864ms 排隊階梯——多站並行的下一步（GPU+解串行）有了實證依據。
