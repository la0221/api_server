# -*- coding: utf-8 -*-
"""父子節點 POC — 共用協定 + HTTP 客戶端（只用標準庫）

協定（對齊我方既有契約，方便日後合併）：
  端點：POST /api/infer/pair
  請求：body = 影像原始 bytes（jpg/png/bmp 皆可，parent 自行 imdecode）
        headers: X-Station-Id / X-Model-Version / X-Task
  回應：JSON 信封（同 2026-07-24_multi_model_server_architecture §3.1）
        {
          "stationId": "...", "task": "ocr_pair|lens|stub", "modelVersion": "...",
          "elapsedMs": <server 純推論耗時>, "status": "ok|no_object|failed",
          "result": { ...依 task 不同... }
        }
  ※ POC 用「raw body + headers」簡化；正式契約用 multipart（欄位語意相同）。
"""
import http.client
import json
import os
import sys
import threading
import time
from urllib.parse import quote, unquote

ENDPOINT = "/api/infer/pair"


def header_safe(value):
    """HTTP header 只能用 latin-1；含中文的值（如中文路徑）會讓 http.client 直接拋
    UnicodeEncodeError、封包根本送不出去。非 latin-1 的字元一律 percent-encode。
    ★2026-08-14 實戰踩到：子端 X-Raw-Path 帶中文路徑 -> 每張都 UnicodeEncodeError
      -> 現場看到的是「送不到父端」，極易誤判成網路/防火牆問題。"""
    s = "" if value is None else str(value)
    try:
        s.encode("latin-1")
        return s
    except UnicodeEncodeError:
        return quote(s, safe="/:\\_-.=,()[] ")


def header_decode(value):
    """還原 header_safe 編過的值（父端顯示用）。"""
    s = "" if value is None else str(value)
    try:
        return unquote(s)
    except Exception:
        return s

# ------------------------- 中央事件 log（append-only, JSONL）-------------------------
# 每個節點（父/子）一份屬於自己的中央 log，任何操作都記進去，供事後回填驗證紀錄。
# 設計原則：append 不覆寫、thread-safe、絕不因記錄失敗而中斷主流程。
_LOG_LOCK = threading.Lock()


def log_path_for(role, script_file=None):
    """回傳該節點中央事件 log 的預設路徑：<base>/_logs/<role>_events.jsonl。
    base：凍結成 exe 時取 exe 所在夾，否則取呼叫端 script_file 所在夾（皆＝dist 或程式旁）。"""
    if getattr(sys, "frozen", False):
        base = os.path.dirname(os.path.abspath(sys.executable))
    elif script_file:
        base = os.path.dirname(os.path.abspath(script_file))
    else:
        base = os.getcwd()
    return os.path.join(base, "_logs", str(role) + "_events.jsonl")


def event_log(path, event, **fields):
    """對中央事件 log append 一行 JSON（含 ts / pid / event + 任意欄位）。永不拋例外。"""
    if not path:
        return
    try:
        rec = {"ts": time.strftime("%Y-%m-%dT%H:%M:%S"), "pid": os.getpid(), "event": str(event)}
        rec.update(fields)
        d = os.path.dirname(path)
        if d:
            os.makedirs(d, exist_ok=True)
        line = json.dumps(rec, ensure_ascii=False)
        with _LOG_LOCK:
            with open(path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
    except Exception:
        pass  # 記錄失敗絕不影響主流程


class Client:
    """keep-alive 連線，重複送圖（對齊「連線重用、關 Nagle」的 HTTP 注意事項）。"""

    def __init__(self, host, port, timeout=15.0):
        self.host, self.port, self.timeout = host, port, timeout
        self.conn = None

    def _connect(self):
        import socket
        self.conn = http.client.HTTPConnection(self.host, self.port, timeout=self.timeout)
        self.conn.connect()
        try:
            self.conn.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)  # 關 Nagle
        except Exception:
            pass

    def infer(self, img_bytes, station="ST-01", model="poc", task="ocr_pair", extra_headers=None):
        """回傳 (status_code, result_dict, rtt_ms)。rtt = 子端量到的端到端來回時間。

        extra_headers：Route A 用來帶 X-Raw-Id / X-Preproc-Version / X-Raw-Sha 等溯源標籤
                       （不傳＝走原本的 raw-send 行為，向後相容）。
        """
        t0 = time.perf_counter()
        try:
            if self.conn is None:
                self._connect()
            headers = {
                "Content-Type": "application/octet-stream",
                "Content-Length": str(len(img_bytes)),
                "X-Station-Id": header_safe(station), "X-Model-Version": header_safe(model),
                "X-Task": header_safe(task),
                "Connection": "keep-alive",
            }
            if extra_headers:
                # 一律做 latin-1 安全處理（中文路徑/檔名會讓整個請求送不出去）
                headers.update({k: header_safe(v) for k, v in extra_headers.items()})
            self.conn.request("POST", ENDPOINT, body=img_bytes, headers=headers)
            resp = self.conn.getresponse()
            data = resp.read()
            rtt = (time.perf_counter() - t0) * 1000
            obj = json.loads(data.decode("utf-8")) if data else {}
            return resp.status, obj, rtt
        except Exception as e:
            try:
                if self.conn:
                    self.conn.close()
            except Exception:
                pass
            self.conn = None
            return 0, {"error": type(e).__name__ + ": " + str(e)}, (time.perf_counter() - t0) * 1000

    def close(self):
        try:
            if self.conn:
                self.conn.close()
        except Exception:
            pass
        self.conn = None
