# -*- coding: utf-8 -*-
"""父節點（server）— 收子端送來的圖 → 推論 → 回驗證結果 JSON。

啟動：
  python parent_server.py --engine stub            # 純傳輸測試（不載模型，回假結果）
  python parent_server.py --engine ocr             # 真 CRNN 模號穴號（需 lens-gpu 環境）
  python parent_server.py --engine lens            # 真 母模鏡片偵測
  可選：--host 0.0.0.0 --port 8770 --proc-ms 40     （stub 用 proc-ms 模擬推論耗時）

介面：
  POST /api/infer/pair   收圖(raw body)+headers → 回 JSON 信封（見 common.py）
  GET  /                 簡易狀態頁（最近 N 筆結果，自動刷新）
  GET  /health           {status:ok, engine, count}
"""
import argparse
import base64
import json
import os
import sys
import time
import threading
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ENDPOINT_PATH = "/api/infer/pair"  # 對齊 common.ENDPOINT
RECENT = deque(maxlen=30)          # 狀態頁用
COUNT = {"n": 0}
LOCK = threading.Lock()
SAVE_LOCK = threading.Lock()       # 存圖寫檔用
ENGINE = None
ARGS = None

# ----------------------------- 中央事件 log（append-only JSONL；父端專屬）-----------------------------
# 自足（不 import common）：Linux 父端複本旁無 common.py，且 exe 亦不依賴。任何操作都記，供事後回填驗證。
LOG_PATH = None
_LOG_LOCK = threading.Lock()


def _default_log_path():
    if getattr(sys, "frozen", False):
        base = os.path.dirname(os.path.abspath(sys.executable))
    else:
        base = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base, "_logs", "parent_events.jsonl")


def elog(event, **fields):
    """對父端中央 log append 一行 JSON（ts/pid/event + 欄位）。永不拋例外、絕不中斷主流程。"""
    if not LOG_PATH:
        return
    try:
        rec = {"ts": time.strftime("%Y-%m-%dT%H:%M:%S"), "pid": os.getpid(), "event": str(event)}
        rec.update(fields)
        d = os.path.dirname(LOG_PATH)
        if d:
            os.makedirs(d, exist_ok=True)
        line = json.dumps(rec, ensure_ascii=False)
        with _LOG_LOCK:
            with open(LOG_PATH, "a", encoding="utf-8") as f:
                f.write(line + "\n")
    except Exception:
        pass


# ----------------------------- 父端存圖（--save-recv） -----------------------------
def _recv_ext(body):
    if body[:4] == b"\x89PNG": return ".png"
    if body[:2] == b"\xff\xd8": return ".jpg"
    if body[:2] == b"BM": return ".bmp"
    return ".bin"


def _safe_name(s):
    return "".join(c if (c.isalnum() or c in "-_.") else "_" for c in (s or "img"))[:120]


def save_received(save_dir, name, body, meta):
    """把父端收到的圖 + 結果 json 寫到 save_dir/recv、save_dir/json。"""
    with SAVE_LOCK:
        with open(os.path.join(save_dir, "recv", name), "wb") as f:
            f.write(body)
        with open(os.path.join(save_dir, "json", os.path.splitext(name)[0] + ".json"),
                  "w", encoding="utf-8") as f:
            json.dump(meta, f, ensure_ascii=False, indent=2)


# ----------------------------- 推論引擎 -----------------------------
class StubEngine:
    """不載模型：可選睡 proc_ms 模擬推論耗時，回固定假結果。用來單獨量『傳輸』。"""
    task = "stub"
    def __init__(self, proc_ms=0.0):
        self.proc_ms = proc_ms
    def infer(self, body, is_strip=False):
        if self.proc_ms > 0:
            time.sleep(self.proc_ms / 1000.0)
        return "ok", {"mohao": "M101", "confMohao": 0.95, "xuehao": "02",
                      "confXuehao": 0.93, "hasReading": True, "needsReview": False}


class OcrEngine:
    """真 CRNN：import 使用者確認可用的 crnn_infer（不改其檔）。"""
    task = "ocr_pair"
    def __init__(self):
        import sys
        from pathlib import Path
        sys.path.insert(0, r"D:/OCR_demo/models/crnn")
        import numpy as np, cv2, crnn_infer
        v3 = Path(r"D:/OCR_demo/models/crnn/runs/nonar_v931_fix_v3/best.pt")
        if v3.exists():
            crnn_infer.NONAR_W = v3
        self.np, self.cv2 = np, cv2
        self.reader = crnn_infer.CrnnReader(device="0")
        self._lock = threading.Lock()   # 序列化推論：多執行緒共用同一 torch 模型不安全，且併發對單卡吞吐零助益（見角色對調報告 §7）
    def infer(self, body, is_strip=False):
        arr = self.np.frombuffer(body, self.np.uint8)
        img = self.cv2.imdecode(arr, self.cv2.IMREAD_COLOR)
        if img is None:
            return "failed", {"error": "decode_failed"}
        with self._lock:            # 一次只讓一個請求進 GPU 推論
            # is_strip=True：子端已做完 to_strip，這裡只做辨識（Route A real 模式）
            r = self.reader.read(img, is_strip=is_strip)
        status = "ok" if r.get("mohao") not in (None, "?", "") else "no_object"
        return status, {"mohao": r["mohao"], "confMohao": r["conf_mohao"],
                        "xuehao": r["xuehao"], "confXuehao": r["conf_xuehao"],
                        "hasReading": (r["mohao"] not in ("?", "") and r["xuehao"] not in ("?", "")),
                        "needsReview": r["needs_review"]}


class LensEngine:
    """真 母模鏡片偵測：import judge_core（不改其檔）。"""
    task = "lens"
    def __init__(self):
        import sys
        from pathlib import Path
        tool = Path(r"D:/Content_lens_OCR/母模小程式/母模判定工具")
        sys.path.insert(0, str(tool))
        import numpy as np, cv2, judge_core as core
        from ultralytics import YOLO
        self.np, self.cv2, self.core = np, cv2, core
        self.model = YOLO(str(tool / "weights" / "diffnet_full_v2.pt"))
        self.Bc = core.load_background(str(tool / "weights" / "通用背景板.npz"))
        self._lock = threading.Lock()   # 同 OcrEngine：序列化推論
    def infer(self, body, is_strip=False):   # lens 無 strip 概念，忽略 is_strip
        import tempfile, os
        # judge_core.preprocess 需要路徑；寫暫存檔（POC 權宜，跑完刪）
        arr = self.np.frombuffer(body, self.np.uint8)
        img = self.cv2.imdecode(arr, self.cv2.IMREAD_COLOR)
        if img is None:
            return "failed", {"error": "decode_failed"}
        fd, tmp = tempfile.mkstemp(suffix=".bmp"); os.close(fd)
        try:
            self.cv2.imwrite(tmp, img)
            with self._lock:            # 一次只讓一個請求進 GPU 推論
                r = self.core.preprocess(tmp, self.Bc)
                if not r["ok"]:
                    return "failed", {"error": r["msg"]}
                pred = self.model.predict(r["diff_tile"], imgsz=224, verbose=False)[0]
                cls = pred.names[int(pred.probs.top1)]; conf = float(pred.probs.top1conf)
            return "ok", {"verdict": cls, "conf": round(conf, 4)}
        finally:
            try: os.remove(tmp)
            except Exception: pass


def build_engine(name, proc_ms):
    if name == "stub": return StubEngine(proc_ms)
    if name == "ocr":  return OcrEngine()
    if name == "lens": return LensEngine()
    raise ValueError(name)


# ----------------------------- HTTP handler -----------------------------
class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"       # 支援 keep-alive
    disable_nagle_algorithm = True      # 關 Nagle：否則每筆回應被 delayed-ACK 卡 ~40ms（跨機才顯現，loopback 測不出）

    def log_message(self, *a):          # 靜音預設 log
        pass

    def _json(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _bytes(self, code, ctype, data):
        self.send_response(code); self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)

    def _recv_gallery(self):
        import glob
        jdir = os.path.join(ARGS.save_recv, "json")
        files = sorted(glob.glob(os.path.join(jdir, "*.json")), key=os.path.getmtime, reverse=True)[:300]
        rows = []
        for jp in files:
            try:
                d = json.load(open(jp, encoding="utf-8"))
            except Exception:
                continue
            rows.append(
                "<tr><td>" + str(d.get("t", "")) + "</td><td>" + str(d.get("station", "")) + "</td>"
                "<td><img src='/recv/" + str(d.get("recvFile", "")) + "' style='height:52px;background:#fff;padding:2px;border-radius:4px'></td>"
                "<td style='font-size:20px;color:#8ff0b4;font-weight:700'>" + str(d.get("reading", "")) + "</td>"
                "<td>" + str(d.get("status", "")) + "</td>"
                "<td style='font-size:11px;opacity:.7'>" + str(d.get("recvBytes", "")) + "B</td>"
                "<td style='font-size:11px;opacity:.7'>" + str(d.get("edgeRawPath") or "-") + "</td></tr>")
        html = ("<!doctype html><meta charset=utf-8><title>父端收圖</title>"
                "<style>body{font-family:'Microsoft JhengHei',sans-serif;background:#0e2a47;color:#e8eef6;padding:20px}"
                "table{border-collapse:collapse;background:#12345a;border-radius:8px;overflow:hidden}"
                "th,td{padding:7px 11px;border-bottom:1px solid #24507e;text-align:left;vertical-align:middle}"
                "th{background:#1b3f6b}img{display:block}a{color:#7fd0ff}</style>"
                "<h2>父端（server）收到的圖　共 " + str(len(files)) + " 筆（存於 " + str(ARGS.save_recv) + "）</h2>"
                "<p style='opacity:.7'>每列＝父端實際收到的一張圖（Route A real 下＝前處理 strip）＋辨識結果＋子端原圖位置。"
                "<a href='/'>← 回狀態頁</a></p>"
                "<table><tr><th>時間</th><th>站</th><th>收到的圖</th><th>讀值</th><th>狀態</th><th>bytes</th><th>子端原圖位置</th></tr>"
                + "".join(rows) + "</table>")
        self._bytes(200, "text/html; charset=utf-8", html.encode("utf-8"))

    def do_GET(self):
        if self.path.startswith("/health"):
            self._json(200, {"status": "ok", "engine": ARGS.engine, "count": COUNT["n"]})
            elog("health", client=self.client_address[0])
            return
        if ARGS.save_recv and self.path.startswith("/recv/"):     # 服務已存的收圖檔
            fn = os.path.basename(self.path[len("/recv/"):].split("?")[0])
            fp = os.path.join(ARGS.save_recv, "recv", fn)
            if os.path.isfile(fp):
                data = open(fp, "rb").read()
                ct = ("image/png" if fn.endswith(".png") else
                      "image/jpeg" if fn.endswith((".jpg", ".jpeg")) else "application/octet-stream")
                self._bytes(200, ct, data)
            else:
                self._json(404, {"error": "not_found"})
            return
        if ARGS.save_recv and self.path.startswith("/recv"):      # 收圖檢視頁
            elog("view_recv", client=self.client_address[0])
            self._recv_gallery(); return
        # 狀態頁（用字串串接，避免 CSS 的 % 與字串格式化衝突）
        rows = []
        for it in list(RECENT)[::-1]:
            rows.append(
                "<tr><td>{t}</td><td>{st}</td><td>{tk}</td><td>{rd}</td>"
                "<td style='text-align:right'>{ms:.1f}</td><td>{stt}</td>"
                "<td style='font-size:11px;opacity:.75'>{rp}</td></tr>".format(
                    t=it["t"], st=it["station"], tk=it["task"], rd=it["reading"],
                    ms=it["elapsedMs"], stt=it["status"], rp=(it.get("rawPath") or "-")))
        css = ("<style>body{font-family:'Microsoft JhengHei',sans-serif;background:#0e2a47;color:#dfe9f5;padding:20px}"
               "h2{margin:0 0 4px}table{border-collapse:collapse;width:100%;background:#12345a;border-radius:8px;overflow:hidden}"
               "th,td{padding:6px 10px;border-bottom:1px solid #24507e;font-size:13px}th{background:#1b3f6b}</style>")
        # 最新結果大字橫幅（給觀眾看）
        if RECENT:
            it = list(RECENT)[-1]
            banner = ("<div style='background:#12345a;border-radius:12px;padding:18px 24px;margin:10px 0'>"
                      "<div style='font-size:12px;opacity:.6'>最新結果</div>"
                      "<div style='font-size:40px;font-weight:700;color:#7fe0a0;line-height:1.1'>"
                      + str(it["reading"]) + "</div>"
                      "<div style='font-size:13px;opacity:.8;margin-top:4px'>station=" + str(it["station"])
                      + "　" + f"{it['elapsedMs']:.0f}" + "ms　" + str(it["status"]) + "　" + str(it["t"]) + "</div></div>")
        else:
            banner = ("<div style='background:#12345a;border-radius:12px;padding:18px 24px;margin:10px 0;"
                      "font-size:20px;opacity:.6'>等待子端送圖…</div>")
        # Route A 檢視：父端只拿到「前處理圖」，原圖留在子端 → 顯示前處理圖 + 子端原圖位置
        ra = next((x for x in list(RECENT)[::-1] if x.get("preprocB64")), None)
        if ra:
            ra_panel = ("<div style='background:#12345a;border-radius:12px;padding:16px 20px;margin:10px 0'>"
                        "<div style='font-size:12px;opacity:.6;margin-bottom:8px'>Route A ─ 父端收到的前處理圖（server 只拿到這張小圖）</div>"
                        "<img src='data:image/png;base64," + ra["preprocB64"] + "' "
                        "style='background:#fff;padding:4px;border-radius:6px;height:64px;image-rendering:pixelated'>"
                        "<div style='font-size:13px;opacity:.85;margin-top:10px'>前處理版本："
                        + str(ra.get("preprocVer") or "-") + "</div>"
                        "<div style='font-size:13px;opacity:.85;margin-top:4px'>子端原圖位置："
                        "<span style='color:#ffd479'>" + str(ra.get("rawPath") or "-") + "</span></div></div>")
        else:
            ra_panel = ""
        save_note = (("<p style='font-size:12px'>💾 收圖存於 <span style='color:#ffd479'>" + str(ARGS.save_recv)
                      + "</span>　<a style='color:#7fd0ff' href='/recv'>看父端收圖 →</a></p>")
                     if ARGS.save_recv else
                     "<p style='opacity:.5;font-size:12px'>（父端未存圖；啟動加 --save-recv 才會存到硬碟）</p>")
        html = ("<!doctype html><meta charset=utf-8><meta http-equiv=refresh content=1>"
                "<title>父節點 狀態</title>" + css
                + "<h2>父節點（server） engine=" + str(ARGS.engine) + "  已處理 " + str(COUNT["n"]) + " 筆</h2>"
                + banner + ra_panel + save_note
                + "<p style='opacity:.7;font-size:12px'>子端送圖→本頁即時顯示回傳的驗證結果（每秒刷新）</p>"
                + "<table><tr><th>時間</th><th>站</th><th>task</th><th>讀值/判定</th><th>推論ms</th><th>狀態</th><th>子端原圖位置</th></tr>"
                + "".join(rows) + "</table>")
        b = html.encode("utf-8")
        self.send_response(200); self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)

    def do_POST(self):
        if not self.path.startswith(ENDPOINT_PATH):
            self._json(404, {"error": "not_found"}); return
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n) if n else b""
        station = self.headers.get("X-Station-Id", "?")
        model = self.headers.get("X-Model-Version", "?")
        raw_id = self.headers.get("X-Raw-Id")            # Route A 溯源：原圖 id
        preproc = self.headers.get("X-Preproc-Version")  # Route A：前處理版本標籤
        # Route A：原圖在子端(edge)的位置。子端會把非 latin-1（如中文路徑）percent-encode，這裡還原。
        raw_path = self.headers.get("X-Raw-Path")
        if raw_path:
            try:
                from urllib.parse import unquote
                raw_path = unquote(raw_path)
            except Exception:
                pass
        is_strip = self.headers.get("X-Is-Strip") == "1"  # Route A real：收到的已是前處理 strip，只做辨識
        t0 = time.perf_counter()
        try:
            status, result = ENGINE.infer(body, is_strip)
        except Exception as e:
            status, result = "failed", {"error": type(e).__name__ + ": " + str(e)}
            elog("error", where="infer", station=station, rawId=raw_id, err=type(e).__name__ + ": " + str(e))
        elapsed = (time.perf_counter() - t0) * 1000
        envelope = {"stationId": station, "task": ENGINE.task, "modelVersion": model,
                    "elapsedMs": round(elapsed, 1), "status": status, "result": result}
        # Route A：把溯源標籤原樣回拋，讓 edge 能把「server 結果」綁回「本機原圖」
        if raw_id:
            envelope["rawId"] = raw_id
        if preproc:
            envelope["preprocVersion"] = preproc
        # Route A：若收到的是前處理小圖，嵌 base64 供狀態頁顯示（限小圖，避免拖垮狀態頁）
        preproc_b64 = ""
        if preproc and 0 < n <= 80000:
            try:
                preproc_b64 = base64.b64encode(body).decode("ascii")
            except Exception:
                preproc_b64 = ""
        with LOCK:
            COUNT["n"] += 1
            cnt = COUNT["n"]
            reading = (result.get("mohao", "") and (str(result.get("mohao")) + "/" + str(result.get("xuehao", "")))) \
                      or result.get("verdict", "") or result.get("error", "-")
            RECENT.append({"t": time.strftime("%H:%M:%S"), "station": station, "task": ENGINE.task,
                           "reading": reading, "elapsedMs": elapsed, "status": status,
                           "rawPath": raw_path or "", "preprocVer": preproc or "", "preprocB64": preproc_b64})
        # 父端存圖（--save-recv）：把「實際收到的圖」+ 結果寫檔
        if ARGS.save_recv and body:
            try:
                name = _safe_name(raw_id or f"{station}_{cnt:06d}") + _recv_ext(body)
                save_received(ARGS.save_recv, name, body, {
                    "t": time.strftime("%Y-%m-%d %H:%M:%S"), "seq": cnt, "rawId": raw_id,
                    "station": station, "task": ENGINE.task, "reading": reading, "status": status,
                    "elapsedMs": round(elapsed, 1), "recvBytes": n, "isStrip": is_strip,
                    "preprocVersion": preproc, "edgeRawPath": raw_path, "recvFile": name})
                elog("save_recv", seq=cnt, file=name, bytes=n)
            except Exception as ex:
                print("[save-recv warn]", ex, flush=True)
                elog("error", where="save_recv", err=type(ex).__name__ + ": " + str(ex))
        # 中央 log：每筆請求/推論都記（任何操作都進中央 log）
        elog("request", seq=cnt, client=self.client_address[0], station=station, task=ENGINE.task,
             isStrip=is_strip, preprocVersion=preproc, rawId=raw_id, edgeRawPath=raw_path,
             recvBytes=n, reading=reading, status=status, elapsedMs=round(elapsed, 1))
        # 每收到一張就在 console 印一行（讓父端視窗看得到活動）
        try:
            tag = ("  rawId=" + raw_id) if raw_id else ""
            print(f"[{time.strftime('%H:%M:%S')}] #{cnt}  station={station}  {ENGINE.task}  "
                  f"read={reading}  recv={n}B  {elapsed:.1f}ms  {status}{tag}", flush=True)
        except Exception:
            pass
        self._json(200, envelope)


def main():
    global ENGINE, ARGS, LOG_PATH
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", default="stub", choices=["stub", "ocr", "lens"])
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8770)
    ap.add_argument("--proc-ms", type=float, default=40.0, help="stub 模擬推論耗時(ms)")
    _default_recv = str((__import__("pathlib").Path(__file__).resolve().parent / "_recv_out"))
    ap.add_argument("--save-recv", nargs="?", const=_default_recv, default=None,
                    help=f"存父端收到的圖到硬碟；不帶值=存到 {_default_recv}；可指定資料夾")
    ap.add_argument("--log", default=None,
                    help="父端中央事件 log（append-only JSONL）路徑；不給=<程式旁>/_logs/parent_events.jsonl；'off'=關閉")
    ARGS = ap.parse_args()
    if ARGS.save_recv:
        ARGS.save_recv = os.path.abspath(ARGS.save_recv)
        os.makedirs(os.path.join(ARGS.save_recv, "recv"), exist_ok=True)
        os.makedirs(os.path.join(ARGS.save_recv, "json"), exist_ok=True)
        print(f"[parent] 收圖存放：{ARGS.save_recv}  (recv/ + json/；瀏覽器開 /recv 可看)")
    # 中央事件 log（任何操作都記，供事後回填驗證紀錄）
    if ARGS.log != "off":
        LOG_PATH = os.path.abspath(ARGS.log) if ARGS.log else _default_log_path()
        print(f"[parent] 中央事件 log：{LOG_PATH}  (append-only；每筆請求/存圖/health/錯誤都記)")
    py = sys.version.split()[0]
    elog("start", engine=ARGS.engine, host=ARGS.host, port=ARGS.port, procMs=ARGS.proc_ms,
         saveRecv=ARGS.save_recv, python=py, argv=sys.argv[1:])
    print(f"[parent] 載入 engine={ARGS.engine} ...")
    ENGINE = build_engine(ARGS.engine, ARGS.proc_ms)
    device = None
    if ARGS.engine in ("ocr", "lens"):
        try:
            import torch
            device = "cuda:" + torch.cuda.get_device_name(0) if torch.cuda.is_available() else "cpu"
        except Exception:
            device = "unknown"
    print(f"[parent] engine ready. 監聽 http://{ARGS.host}:{ARGS.port}  (POST {ENDPOINT_PATH} / GET /)")
    elog("engine_ready", engine=ARGS.engine, device=device, host=ARGS.host, port=ARGS.port)
    srv = ThreadingHTTPServer((ARGS.host, ARGS.port), Handler)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\n[parent] 關閉")
        elog("shutdown", count=COUNT["n"])


if __name__ == "__main__":
    main()
