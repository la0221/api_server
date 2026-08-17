# -*- coding: utf-8 -*-
"""Route A 邊緣端（edge）— 把定案的 Route A 資料流跑起來，先本機再跨機。

Route A（設計書 §11 定案）：
  相機圖(raw) ──▶ A(edge)：① 本機存原圖（非同步）
                         ② 前處理成「小圖」＋帶前處理版本標籤（無損 PNG）
                         ③ 只把「小圖」送去 server（server 只做推論＝多站吞吐最佳）
              ◀── server 回結果（原樣帶回 rawId）
                         ④ 原圖綁結果寫 manifest（可追溯）；server 不可達時本機 fallback（不停線）

先在本機 127.0.0.1 跑通驗證，再把 --host 換成父機 IP 做真跨機（協定/程式完全一樣）：
  # 本機：一個視窗開父端  python parent_server.py --engine stub --proc-ms 40
  #      另一個視窗開本檔  python route_a_edge.py --host 127.0.0.1 --port 8770 --n 30
  # 跨機：把 --host 換成父機 IP 即可

前處理模式 --preproc（重要）：
  real（預設）＝真 CRNN 前處理 to_strip（find_circle→裁圓→annulus_polar 極座標展開）→ 640×640 strip，
               server 用 is_strip=True 只做辨識。★讀值與「原圖跑完整 pipeline」一致（實測 10/10）、
               payload 約原圖 1/3。★用自足的 edge_preproc.py，**只需 numpy+cv2、免 torch**（前處理不做辨識）。
  none        ＝原圖直送，server 自己做完整前處理（is_strip=False）。★讀值正確、無邊緣前處理、payload=原圖。
               （real 只需 numpy+cv2；缺套件會直接報錯要你先裝環境包，不會硬跑。只有 edge_preproc.py 檔案遺失才退回 none。）
  repr        ＝代表性縮圖（灰階H48）僅示意傳輸縮減，★會毀圖、接真分類器會亂判，只能配 stub 做傳輸 demo。

要看正確讀值＝父端須是真 CRNN（parent_server.py --engine ocr / 4_父_真GPU），且本檔用 real（或 none）。
"""
import argparse
import hashlib
import json
import queue
import sys
import threading
import time
from pathlib import Path

try:
    import numpy as np
    import cv2
except ImportError as _e:
    sys.stderr.write(
        "\n[錯誤] 缺少 numpy / opencv-python：" + str(_e) + "\n"
        "  子端做真前處理(to_strip) 需要 numpy+opencv（免 torch）。\n"
        "  請先安裝環境包：dist\\環境配置\\子\\安裝_子_離線.bat（或 安裝_子_線上.bat）。\n"
        "  裝完再重跑本程式。\n\n")
    sys.exit(2)

sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import Client, event_log, log_path_for

LOG_PATH = None   # 子端中央事件 log（append-only JSONL）；route_a_edge 與 child_edge 都寫進同一份 child_events.jsonl
def elog(event, **fields):
    event_log(LOG_PATH, event, **fields)

try:
    sys.stdout.reconfigure(errors="replace")
except Exception:
    pass

IMG_EXT = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}
_EP = None   # 延遲載入的 edge_preproc（自足、免 torch，只需 numpy+cv2）


def load_real_preproc():
    """載入 edge 端免 torch 前處理模組 edge_preproc（純 cv2，不碰 torch）。回 True=成功。
    ★前處理不需 torch——辨識(YOLO+CRNN)才在 server。edge 只要有 cv2 就能做真 strip。"""
    global _EP
    if _EP is not None:
        return True
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import edge_preproc
        _EP = edge_preproc
        return True
    except Exception as e:
        print(f"[warn] edge_preproc 載入失敗（{type(e).__name__}: {e}）→ 自動改用『原圖直送(none)』（讀值仍正確）")
        return False


# ----------------------------- ② 前處理（依 --preproc 模式） -----------------------------
def preprocess_edge(raw_bytes, mode):
    """回 (sent_bytes, is_strip, version, meta)。失敗回 (None, False, '', {'error':...})。
      real = 真 CRNN 前處理 to_strip（find_circle→裁圓→annulus_polar 極座標展開）→ 640×640 strip，
             server 用 is_strip=True 只做辨識。★讀值正確、payload 約原圖 1/3。
      none = 原圖直送，server 自己做完整前處理（is_strip=False）。★讀值正確、無邊緣前處理。
      repr = 代表性縮圖（灰階H48）僅示意傳輸縮減，★毀圖、不可接真分類器（會亂判）。"""
    if mode == "none":
        return raw_bytes, False, "raw-passthrough", {"note": "raw; server does full preprocess"}
    arr = np.frombuffer(raw_bytes, np.uint8)
    img = cv2.imdecode(arr, cv2.IMREAD_COLOR)
    if img is None:
        return None, False, "", {"error": "decode_failed"}
    if mode == "real":
        strip, hough = _EP.to_strip(img)        # 免 torch，純 cv2（find_circle→裁圓→annulus_polar）
        ok, buf = cv2.imencode(".png", strip)   # 無損，確保讀值與原圖完整 pipeline 一致
        if not ok:
            return None, True, "", {"error": "encode_failed"}
        return buf.tobytes(), True, "crnn-strip-640", {"w": 640, "h": 640, "hough_used": hough}
    # repr：代表性縮圖（毀圖，僅示意傳輸，勿接真分類器）
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    h, w = g.shape[:2]; nh = 48; nw = max(1, int(round(w * nh / h)))
    small = cv2.resize(g, (nw, nh), interpolation=cv2.INTER_AREA)
    ok, buf = cv2.imencode(".png", small)
    if not ok:
        return None, False, "", {"error": "encode_failed"}
    return buf.tobytes(), False, "edge-repr-v1", {"w": nw, "h": nh,
            "warn": "representative shrink only; NOT for real classifier"}


# ----------------------------- ① 本機存原圖（非同步，不擋送圖） -----------------------------
class RawStore:
    def __init__(self, root):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.q = queue.Queue()
        self.saved = 0
        self.t = threading.Thread(target=self._worker, daemon=True)
        self.t.start()

    def save_async(self, raw_id, raw_bytes, ext):
        self.q.put((raw_id, raw_bytes, ext))

    def _worker(self):
        while True:
            item = self.q.get()
            if item is None:
                self.q.task_done()
                break
            raw_id, raw_bytes, ext = item
            try:
                (self.root / (raw_id + ext)).write_bytes(raw_bytes)
                self.saved += 1
            except Exception:
                pass
            finally:
                self.q.task_done()

    def drain(self):
        self.q.join()

    def stop(self):
        self.q.put(None)
        self.q.join()


def read_bytes(p):
    with open(p, "rb") as f:
        return f.read()


def pctl(xs):
    if not xs:
        return {}
    s = sorted(xs)
    g = lambda q: s[min(len(s) - 1, int(len(s) * q))]
    return {"p50": round(g(.5), 1), "p90": round(g(.9), 1), "p99": round(g(.99), 1)}


def build_gallery(out, records):
    """產生子端檢視頁 index.html：每列＝原始圖 | 前處理圖 | 讀值 | JSON。"""
    rows = []
    for r in records:
        raw_rel = "raw/" + Path(r["rawPath"]).name
        pre_rel = "preprocessed/" + Path(r["preprocPath"]).name
        js_rel = "json/" + r["rawId"] + ".json"
        rows.append(
            "<tr>"
            "<td>" + str(r.get("idx", "")) + "</td>"
            "<td style='font-family:monospace;font-size:11px'>" + r["rawId"] + "</td>"
            "<td><img src='" + raw_rel + "' style='height:74px;border-radius:4px'></td>"
            "<td><img src='" + pre_rel + "' style='height:46px;background:#fff;padding:2px;"
            "image-rendering:pixelated;border-radius:4px'></td>"
            "<td style='font-size:22px;color:#8ff0b4;font-weight:700'>" + str(r["reading"]) + "</td>"
            "<td style='font-size:11px;opacity:.7'>" + str(r["sentBytes"]) + "B</td>"
            "<td><a href='" + js_rel + "'>json</a></td>"
            "</tr>")
    html = ("<!doctype html><meta charset=utf-8><title>子端 Route A 檢視</title>"
            "<style>body{font-family:'Microsoft JhengHei',sans-serif;background:#0e2a47;color:#e8eef6;padding:20px}"
            "table{border-collapse:collapse;background:#12345a;border-radius:8px;overflow:hidden}"
            "th,td{padding:8px 12px;border-bottom:1px solid #24507e;text-align:left;vertical-align:middle}"
            "th{background:#1b3f6b}img{display:block}a{color:#7fd0ff}</style>"
            "<h2>子端（edge） Route A 檢視</h2>"
            "<p style='opacity:.75'>每列一張圖：<b>原始圖</b>（留在子端本機）→ <b>前處理圖</b>（實際送給 server 的小圖）"
            "→ <b>讀值</b> → 對應 <b>JSON</b>。只有前處理小圖上網。</p>"
            "<table><tr><th>#</th><th>rawId</th><th>原始圖</th><th>前處理圖</th>"
            "<th>讀值</th><th>送出</th><th>JSON</th></tr>"
            + "".join(rows) + "</table>")
    idx = out / "index.html"
    idx.write_text(html, encoding="utf-8")
    return idx


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8770)
    ap.add_argument("--dir", default=r"D:/模號穴號-穩定圖片區/M101")
    ap.add_argument("--n", type=int, default=30)
    ap.add_argument("--station", default="ST-01")
    ap.add_argument("--task", default="ocr_pair")
    ap.add_argument("--out", default=None, help="Route A 產物夾（原圖 raw-store + manifest），預設本檔旁 _routeA_out")
    ap.add_argument("--store-raw", default="1", help="1=存原圖(Route A 定案) 0=不存(僅量傳輸)")
    ap.add_argument("--interval", type=float, default=0.0, help="每張間隔秒（demo 可放慢）")
    ap.add_argument("--preproc", default="real", choices=["real", "none", "repr"],
                    help="real=真CRNN前處理to_strip(讀值正確,建議;免torch,只需numpy+cv2) / none=原圖直送 / repr=示意縮圖(毀圖,勿接真分類器)")
    ap.add_argument("--log", default=None,
                    help="子端中央事件 log（append-only JSONL）；不給=<程式旁>/_logs/child_events.jsonl；'off'=關閉")
    args = ap.parse_args()

    global LOG_PATH
    if args.log != "off":
        LOG_PATH = str(Path(args.log).resolve()) if args.log else log_path_for("child", __file__)
        print(f"[routeA] 中央事件 log：{LOG_PATH}  (append-only；啟動/每張送圖/總結/警告都記)")

    mode = args.preproc
    if mode == "real" and not load_real_preproc():
        mode = "none"   # 沒 torch/模組 → 退回原圖直送，讀值仍正確

    out = Path(args.out) if args.out else Path(__file__).resolve().parent / "_routeA_out"
    raw_dir = out / "raw"
    preproc_dir = out / "preprocessed"    # 前處理小圖（實際送出的那張）
    json_dir = out / "json"               # 每張一個 JSON（前處理資訊 + 讀值 + 路徑）
    manifest_path = out / "manifest.jsonl"
    out.mkdir(parents=True, exist_ok=True)
    preproc_dir.mkdir(parents=True, exist_ok=True)
    json_dir.mkdir(parents=True, exist_ok=True)

    files = [p for p in sorted(Path(args.dir).rglob("*")) if p.suffix.lower() in IMG_EXT]
    if args.n and len(files) > args.n:
        step = len(files) / args.n
        files = [files[int(i * step)] for i in range(args.n)]
    if not files:
        print("No images in:", args.dir)
        return

    store = RawStore(raw_dir) if args.store_raw == "1" else None
    cli = Client(args.host, args.port)
    mf = open(manifest_path, "w", encoding="utf-8")

    print("=" * 92)
    print(f"Route A edge  ->  parent {args.host}:{args.port}   task={args.task}   images={len(files)}")
    print(f"  (1)store raw async: {'ON -> ' + str(raw_dir) if store else 'OFF'}")
    print(f"  (2)preprocess mode: {mode}  ("
          + ("真 CRNN to_strip → 640 strip，讀值正確" if mode == "real"
             else ("原圖直送，server 自己前處理" if mode == "none" else "示意縮圖，★勿接真分類器"))
          + f")  -> {preproc_dir}")
    print(f"  (3)traceability: manifest + json/ + index.html(子端檢視頁)")
    print("=" * 92)
    hdr = f"{'#':>4}  {'rawId':<22}  {'rawKB':>7}  {'sentKB':>7}  {'cut%':>5}  {'reading':<10}  {'e2e':>6}  src"
    print(hdr)
    print("-" * 92)
    elog("routeA_start", host=args.host, port=args.port, dir=str(args.dir), images=len(files),
         preproc=mode, task=args.task, out=str(out), storeRaw=(store is not None))

    tot_raw = tot_sent = 0
    rtts = []
    ok_srv = 0
    fb = 0
    records = []
    srv_tasks = set()   # 收集父端回的 task；stub→'stub'、真 CRNN→'ocr_pair'，用來防呆假讀值
    for i, p in enumerate(files, 1):
        raw = read_bytes(p)
        raw_id = f"{args.station}_{i:06d}_{p.stem}"          # 決定性 id（可重現、綁得回原圖）
        raw_sha = hashlib.sha1(raw).hexdigest()[:12]
        raw_path = str(raw_dir / (raw_id + p.suffix.lower())) if store else str(p)   # 原圖在子端(edge)的位置
        # ① 本機存原圖（非同步，不擋送圖）
        if store:
            store.save_async(raw_id, raw, p.suffix.lower())
        # ② 前處理（依模式：real=真 to_strip / none=原圖直送 / repr=示意）
        small, is_strip, version, meta = preprocess_edge(raw, mode)
        if small is None:
            print(f"{i:>4}  {raw_id:<22}  preprocess failed: {meta.get('error')}")
            continue
        # ② 存「實際送出的圖」（供子端檢視／對照）
        sent_ext = p.suffix.lower() if version == "raw-passthrough" else ".png"
        preproc_path = preproc_dir / (raw_id + sent_ext)
        try:
            preproc_path.write_bytes(small)
        except Exception:
            pass
        tot_raw += len(raw)
        tot_sent += len(small)
        # ③ 送圖 + 溯源標籤（原圖在子端位置、前處理版本、是否為 strip）
        extra = {"X-Raw-Id": raw_id, "X-Preproc-Version": version,
                 "X-Raw-Sha": raw_sha, "X-Raw-Path": raw_path}
        if is_strip:
            extra["X-Is-Strip"] = "1"   # 告訴 server：這已是前處理好的 strip，只做辨識
        st, obj, rtt = cli.infer(small, station=args.station, model="poc", task=args.task, extra_headers=extra)
        # ④ server 不可達 → 本機 fallback（不停線；真接本機 ONNX，這裡佔位標記）
        if st != 200:
            src = "LOCAL-FB"
            fb += 1
            reading = "(local)"
            server_result = {"fallback": True, "error": obj.get("error")}
            elapsed = None
            # ★把真正的錯誤印出來（第一次 + 每 10 張一次），否則現場只看到 (local) 無從查起
            err = obj.get("error") or f"HTTP {st}"
            if fb == 1 or fb % 10 == 0:
                print(f"      ↳ 送不到父端的原因：{err}", flush=True)
                if fb == 1:
                    print(f"        目標 {args.host}:{args.port}；父端要在跑且防火牆/防毒需放行此埠", flush=True)
            elog("send_failed", idx=i, rawId=raw_id, error=str(err), host=args.host, port=args.port)
        else:
            src = "server"
            ok_srv += 1
            rtts.append(rtt)
            res = obj.get("result", {}) if isinstance(obj, dict) else {}
            srv_tasks.add(obj.get("task") if isinstance(obj, dict) else None)
            reading = (res.get("mohao") and f"{res.get('mohao')}/{res.get('xuehao','')}") or res.get("verdict") or "-"
            server_result = obj
            elapsed = obj.get("elapsedMs")
        cut = (1 - len(small) / len(raw)) * 100 if raw else 0
        print(f"{i:>4}  {raw_id:<22}  {len(raw)/1000:>7.1f}  {len(small)/1000:>7.1f}  {cut:>4.0f}%  "
              f"{str(reading):<10}  {rtt:>5.0f}m  {src}", flush=True)
        # ③ 原圖綁結果：寫 manifest（一行）＋ 每張一個 json（供子端檢視）
        rec = {
            "idx": i, "rawId": raw_id, "rawPath": raw_path, "preprocPath": str(preproc_path),
            "rawSha": raw_sha, "rawBytes": len(raw), "sentBytes": len(small),
            "preprocMode": mode, "preprocVersion": version, "isStrip": is_strip, "small": meta,
            "src": src, "reading": reading, "serverElapsedMs": elapsed, "e2eMs": round(rtt, 1),
            "server": server_result,
        }
        mf.write(json.dumps(rec, ensure_ascii=False) + "\n")
        try:
            (json_dir / (raw_id + ".json")).write_text(
                json.dumps(rec, ensure_ascii=False, indent=2), encoding="utf-8")
        except Exception:
            pass
        records.append(rec)
        # 中央 log：每張送圖都記（任何操作都進中央 log）
        elog("send", idx=i, rawId=raw_id, rawBytes=len(raw), sentBytes=len(small), isStrip=is_strip,
             preprocVersion=version, reading=str(reading), src=src, e2eMs=round(rtt, 1),
             serverElapsedMs=elapsed, serverTask=(server_result.get("task") if isinstance(server_result, dict) else None))
        if args.interval > 0:
            time.sleep(args.interval)

    mf.close()
    cli.close()
    if store:
        store.drain()

    P = pctl(rtts)
    print("-" * 92)
    print(f"images={len(files)}  server-ok={ok_srv}  local-fallback={fb}"
          + (f"  raw-saved={store.saved}" if store else ""))
    if files and ok_srv == 0:
        print("!" * 92)
        print("⚠ server-ok=0：沒有任何一張真的送達父端（全部走本機 fallback）＝這次沒有任何真實讀值！")
        print("  多半是父機 IP 打錯 / 父端沒開 / 防火牆沒放行（跨機請勿用 127.0.0.1）。請檢查後重跑。")
        print("!" * 92)
        elog("warning", kind="server_ok_zero", images=len(files), host=args.host, port=args.port)
    elif ok_srv and "stub" in srv_tasks:
        print("!" * 92)
        print("⚠ 父端引擎是 stub（假推論）：回的讀值是罐頭 M101/02，不是真 CRNN！")
        print("  要正確讀值請讓父端跑 --engine ocr（真實測試\\1_父_真GPU推論_存收圖），再重跑本測試。")
        print("!" * 92)
        elog("warning", kind="parent_is_stub", host=args.host, port=args.port)
    if tot_raw:
        print(f"payload on wire:  raw would be {tot_raw/1e6:.2f} MB  ->  sent {tot_sent/1e6:.3f} MB"
              f"   ({(1-tot_sent/tot_raw)*100:.1f}% less  =  {tot_raw/max(1,tot_sent):.1f}x smaller)")
    if P:
        print(f"end-to-end rtt:  p50={P['p50']}ms  p90={P['p90']}ms  p99={P['p99']}ms")
    print(f"traceability:  {manifest_path}  ({'raw stored at ' + str(raw_dir) if store else 'raw not stored'})")
    # 產生子端檢視頁（原圖 | 前處理圖 | 讀值 | JSON）
    gallery = None
    try:
        gallery = build_gallery(out, records)
    except Exception as e:
        print("gallery gen skipped:", type(e).__name__, e)
    if gallery:
        print(f"child view:    {gallery}")
        print("               ^ 用瀏覽器打開，一列看到：原始圖 | 前處理圖 | 讀值 | JSON")
    print("=" * 92)
    print("verify traceability:  每筆 manifest 的 rawId 都能對回 raw/ 夾裡的原圖，"
          "server 回的 rawId 也一致 -> 原圖<->結果可追溯。")
    elog("routeA_summary", images=len(files), serverOk=ok_srv, localFallback=fb,
         rawSaved=(store.saved if store else 0), rawBytes=tot_raw, sentBytes=tot_sent,
         serverTasks=sorted(str(t) for t in srv_tasks), rtt=P, out=str(out))


if __name__ == "__main__":
    main()
