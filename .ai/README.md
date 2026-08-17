# `.ai/` 使用說明 — 怎麼讀、怎麼用

> 本資料夾是本專案（AIVision）的「AI 工作記憶 / 交接系統」。
> 目的：任何新的對話視窗（agent）或接手的人，都能快速接上進度、無縫繼續。

## 一、這裡有什麼（檔案地圖）

| 路徑 | 是什麼 | 何時讀 / 寫 |
|---|---|---|
| `context/current_handoff.md` | 滾動式交接摘要（目前任務 / 已完成 / 下一步 / 風險） | **接手先讀這個** |
| `status.json` | 進度卡：state / headline / 鎖定項 / 最新 3 筆進度；儀表板讀它 | 每次收工更新 |
| `records/YYYY-MM/YYYY-MM-DD/` | 每日詳細紀錄（原始細節都在這） | 每天寫 |
| `records/.../01_daily_log.md` | 當天主題 / 進度 / 待辦 / 產出 | 每天寫 |
| `records/.../02_bug_notes.md` | 當天踩雷 / 排查 / 修法 | 有 bug 才寫 |
| `records/.../03_reusable_flow.md` | 可重用的流程 / 指令 | 有可複用的才寫 |
| `templates/` | 上面三種的空白模板 | 建新紀錄時複製 |
| `AGENT_進度更新指令.md` | `status.json` 的填寫規範（格式 / 寫法 / 黃金範例） | 更新 status.json 前讀 |
| `README.md` | 就是本檔 | — |

## 二、怎麼讀（新視窗接手的順序）

1. **`context/current_handoff.md`** — 一次掌握「現在在幹嘛、做到哪、下一步」。
2. **`status.json`** — 看 state（進行中 / 暫停 / 受阻 / 已結案）、鎖定的里程碑與風險、最新進度。
3. **最近一天的 `records/.../01_daily_log.md`** — 要細節時再往下挖。

> 讀完這三層就能接手，不必翻完整個 `records/`。

## 三、怎麼用（每次收工的更新流程）

1. 寫今天的 `records/YYYY-MM/YYYY-MM-DD/01_daily_log.md`（可從 `templates/` 複製）。
2. 更新 `context/current_handoff.md`，讓它永遠代表「最新交接狀態」。
3. 更新 `status.json` —— **照 `AGENT_進度更新指令.md`**（只留最新 3 筆、鎖定項獨立、風險用 ⚠ 開頭）。
4. 跑 `python D:\專案管理\process\build_dashboard.py`，把進度合併回儀表板。

> 第 3、4 步是「進度卡」慣例；專案根 `CLAUDE.md` 有規則會提醒 agent 別漏（見全域 / 專案 CLAUDE.md）。

## 四、名詞速查

- **status**（daily_log frontmatter / status.json）：`wip` 進行中、`done` 完成、`validated` 已驗證、`blocked` 受阻。
- **鎖定項 locked**：長期目標 / 里程碑 / 未解阻塞，不受「最新 3 筆」限制，一直顯示；`⚠` 開頭者在儀表板顯示為紅色風險卡。
