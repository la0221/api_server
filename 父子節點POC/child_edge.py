# -*- coding: utf-8 -*-
"""子節點（edge/站）— 送圖到父節點 → 收驗證結果 JSON → 顯示。

兩種用法：
  1) GUI（簡易）：
       python child_edge.py
       填父端 host:port / 站號 → 選圖或資料夾 → 送出 → 看回傳 JSON + 端到端來回 ms

  2) 壓測（量端到端延遲，含 HTTP 傳輸）：
       python child_edge.py --bench --host 127.0.0.1 --port 8770 --dir <圖資料夾> \
              --n 300 --concurrency 1,2,4 --station ST-01 --task ocr_pair [--sim-net-ms 0]
     --sim-net-ms：人為在每次來回加入模擬網路延遲(ms)，用來估「真網路下大概怎樣」。
"""
import argparse
import sys
import time
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor

try:
    sys.stdout.reconfigure(errors="replace")   # 跟隨主控台編碼(950)，遇罕見字以?取代不崩
except Exception:
    pass

sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import Client, event_log, log_path_for

IMG_EXT = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}

LOG_PATH = None   # 子端中央事件 log（append-only）；與 route_a_edge 共用同一份 child_events.jsonl
def elog(event, **fields):
    event_log(LOG_PATH, event, **fields)


def read_bytes(p):
    with open(p, "rb") as f:
        return f.read()


def pctl(xs):
    if not xs: return {}
    s = sorted(xs); g = lambda q: s[min(len(s) - 1, int(len(s) * q))]
    return {"n": len(xs), "mean": round(sum(xs) / len(xs), 1),
            "p50": round(g(.5), 1), "p90": round(g(.9), 1), "p99": round(g(.99), 1), "max": round(max(xs), 1)}


# ----------------------------- 壓測模式 -----------------------------
def run_bench(args):
    files = [p for p in sorted(Path(args.dir).rglob("*")) if p.suffix.lower() in IMG_EXT]
    if args.n and len(files) > args.n:
        step = len(files) / args.n
        files = [files[int(i * step)] for i in range(args.n)]
    if not files:
        print("No images in:", args.dir); return
    # 預讀 bytes（把讀檔成本排除在傳輸量測外）
    blobs = [read_bytes(p) for p in files]
    avg_kb = sum(len(b) for b in blobs) / len(blobs) / 1000
    print(f"# parent {args.host}:{args.port}  task={args.task}  images {len(blobs)}  avg {avg_kb:.0f} KB"
          + (f"  sim-net +{args.sim_net_ms}ms/rt" if args.sim_net_ms else ""))
    print(f"{'conc':>4} {'ok':>6} {'err':>5} {'rps':>8} {'p50':>7} {'p90':>7} {'p99':>7} {'max':>7}  server-ms(p50)")
    print("-" * 78)
    elog("bench_start", host=args.host, port=args.port, task=args.task, images=len(blobs),
         avgKB=round(avg_kb, 1), concurrency=args.concurrency, simNetMs=args.sim_net_ms)

    for k in [int(x) for x in args.concurrency.split(",") if x.strip()]:
        idx = {"i": 0}; rtt = []; srv = []; ok = {"n": 0}; err = {"n": 0}
        import threading; lock = threading.Lock()
        t_start = time.perf_counter()

        def worker():
            cli = Client(args.host, args.port)
            while True:
                with lock:
                    if idx["i"] >= len(blobs): break
                    j = idx["i"]; idx["i"] += 1
                st, obj, r = cli.infer(blobs[j], station=args.station, model="poc", task=args.task)
                if args.sim_net_ms:
                    time.sleep(args.sim_net_ms / 1000.0)   # 模擬網路來回
                    r += args.sim_net_ms
                with lock:
                    if st == 200:
                        ok["n"] += 1; rtt.append(r)
                        if isinstance(obj.get("elapsedMs"), (int, float)): srv.append(obj["elapsedMs"])
                    else:
                        err["n"] += 1
            cli.close()

        with ThreadPoolExecutor(max_workers=k) as ex:
            for _ in range(k): ex.submit(worker)
        wall = time.perf_counter() - t_start
        P = pctl(rtt); sp = pctl(srv)
        print(f"{k:>4} {ok['n']:>6} {err['n']:>5} {ok['n']/wall:>8.1f} "
              f"{P.get('p50',0):>7.1f} {P.get('p90',0):>7.1f} {P.get('p99',0):>7.1f} {P.get('max',0):>7.1f}"
              f"   {sp.get('p50',0):>7.1f}")
        elog("bench_result", conc=k, ok=ok["n"], err=err["n"], rps=round(ok["n"]/wall, 1),
             p50=P.get("p50", 0), p90=P.get("p90", 0), p99=P.get("p99", 0), maxMs=P.get("max", 0),
             serverMsP50=sp.get("p50", 0))
    print("\nNote: p50 = end-to-end round-trip (HTTP+inference); server-ms = parent pure inference;"
          "\n      (p50 - server-ms) ~= transport + queueing.")


# ----------------------------- 示範模式（一張一張、有節奏，給觀眾看）-----------------------------
def run_demo(args):
    files = [p for p in sorted(Path(args.dir).rglob("*")) if p.suffix.lower() in IMG_EXT]
    if args.n and len(files) > args.n:
        step = len(files) / args.n
        files = [files[int(i * step)] for i in range(args.n)]
    if not files:
        print("No images in:", args.dir); return
    print(f"# DEMO  parent {args.host}:{args.port}  task={args.task}  "
          f"{len(files)} images  interval={args.interval}s  loop={args.loop}")
    print(f"{'#':>4}  {'image':<44}  {'result':<12}  {'e2e':>7}  {'server':>7}  status")
    print("-" * 86)
    elog("demo_start", host=args.host, port=args.port, task=args.task, images=len(files),
         interval=args.interval, loop=args.loop)
    cli = Client(args.host, args.port)
    i = 0
    try:
        while True:
            for p in files:
                i += 1
                st, obj, rtt = cli.infer(read_bytes(p), station=args.station, model="poc", task=args.task)
                res = obj.get("result", {}) if isinstance(obj, dict) else {}
                if res.get("mohao"):
                    reading = f"{res.get('mohao')}/{res.get('xuehao', '')}"
                elif res.get("verdict"):
                    reading = str(res.get("verdict"))
                else:
                    reading = str(res.get("error", "-"))
                srv = obj.get("elapsedMs", "?") if isinstance(obj, dict) else "?"
                status = obj.get("status", "err") if isinstance(obj, dict) else "err"
                name = p.name if len(p.name) <= 44 else p.name[:41] + "..."
                print(f"{i:>4}  {name:<44}  {reading:<12}  {rtt:>6.0f}m  {str(srv):>6}m  {status}", flush=True)
                elog("demo_send", idx=i, image=p.name, reading=str(reading), e2eMs=round(rtt, 1),
                     serverMs=srv, status=status, serverTask=(obj.get("task") if isinstance(obj, dict) else None))
                time.sleep(max(0.0, args.interval))
            if not args.loop:
                break
    except KeyboardInterrupt:
        pass
    finally:
        cli.close()
    print(f"\n送出 {i} 張。（demo 結束）")
    elog("demo_end", sent=i)


# ----------------------------- GUI 模式 -----------------------------
def run_gui():
    import tkinter as tk
    from tkinter import ttk, filedialog
    import json, threading

    root = tk.Tk(); root.title("子節點（edge）— 送圖給父節點"); root.geometry("640x460")
    frm = ttk.Frame(root, padding=10); frm.pack(fill="both", expand=True)
    v_host = tk.StringVar(value="127.0.0.1"); v_port = tk.StringVar(value="8770")
    v_station = tk.StringVar(value="ST-01"); v_task = tk.StringVar(value="ocr_pair")
    v_src = tk.StringVar(value="")

    row = 0
    ttk.Label(frm, text="父端 host:").grid(row=row, column=0, sticky="w")
    ttk.Entry(frm, textvariable=v_host, width=16).grid(row=row, column=1, sticky="w")
    ttk.Label(frm, text="port:").grid(row=row, column=2, sticky="e")
    ttk.Entry(frm, textvariable=v_port, width=8).grid(row=row, column=3, sticky="w"); row += 1
    ttk.Label(frm, text="站號:").grid(row=row, column=0, sticky="w")
    ttk.Entry(frm, textvariable=v_station, width=16).grid(row=row, column=1, sticky="w")
    ttk.Label(frm, text="task:").grid(row=row, column=2, sticky="e")
    ttk.Combobox(frm, textvariable=v_task, values=["ocr_pair", "lens", "stub"], width=8).grid(row=row, column=3, sticky="w"); row += 1
    ttk.Entry(frm, textvariable=v_src, width=54).grid(row=row, column=0, columnspan=3, sticky="we", pady=4)
    def pick():
        fps = filedialog.askopenfilenames(title="選圖", filetypes=[("影像", "*.jpg *.jpeg *.png *.bmp *.tif")])
        if fps: v_src.set(";".join(fps))
    def pickdir():
        d = filedialog.askdirectory(title="選資料夾")
        if d: v_src.set(d)
    ttk.Button(frm, text="選圖", command=pick).grid(row=row, column=3, sticky="w"); row += 1

    txt = tk.Text(frm, height=18, width=80); txt.grid(row=row, column=0, columnspan=4, sticky="nsew")
    frm.rowconfigure(row, weight=1); frm.columnconfigure(0, weight=1); row += 1

    def log(s): txt.insert("end", s + "\n"); txt.see("end")

    def send():
        src = v_src.get().strip()
        if not src: log("請先選圖或資料夾"); return
        paths = []
        for s in src.split(";"):
            p = Path(s)
            if p.is_dir():
                paths += [q for q in sorted(p.rglob("*")) if q.suffix.lower() in IMG_EXT][:50]
            elif p.suffix.lower() in IMG_EXT:
                paths.append(p)
        if not paths: log("沒有有效影像"); return
        def worker():
            cli = Client(v_host.get(), int(v_port.get()))
            for p in paths:
                st, obj, rtt = cli.infer(read_bytes(p), station=v_station.get(), model="poc", task=v_task.get())
                root.after(0, log, f"[{p.name}] {st}  來回 {rtt:.1f}ms  →  {json.dumps(obj, ensure_ascii=False)}")
                elog("gui_send", image=p.name, httpStatus=st, e2eMs=round(rtt, 1),
                     station=v_station.get(), task=v_task.get(),
                     serverTask=(obj.get("task") if isinstance(obj, dict) else None))
            cli.close()
        threading.Thread(target=worker, daemon=True).start()

    ttk.Button(frm, text="送出到父節點", command=send).grid(row=1, column=3, sticky="e")
    ttk.Button(frm, text="選資料夾", command=pickdir).grid(row=2, column=3, sticky="w")
    log("填父端 host:port，選圖/資料夾，按『送出到父節點』。")
    log("父端狀態頁：用瀏覽器開 http://<父端host>:<port>/ 可即時看收到的結果。")
    root.mainloop()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bench", action="store_true")
    ap.add_argument("--host", default="127.0.0.1"); ap.add_argument("--port", type=int, default=8770)
    ap.add_argument("--dir", default=r"D:/模號穴號-穩定圖片區/M101"); ap.add_argument("--n", type=int, default=300)
    ap.add_argument("--concurrency", default="1,2,4")
    ap.add_argument("--station", default="ST-01"); ap.add_argument("--task", default="ocr_pair")
    ap.add_argument("--sim-net-ms", type=float, default=0.0)
    ap.add_argument("--demo", action="store_true", help="示範模式：一張一張送、即時顯示")
    ap.add_argument("--interval", type=float, default=0.5, help="示範模式每張間隔秒")
    ap.add_argument("--loop", action="store_true", help="示範模式跑完整批後從頭再跑")
    ap.add_argument("--log", default=None,
                    help="子端中央事件 log（append-only JSONL）；不給=<程式旁>/_logs/child_events.jsonl；'off'=關閉")
    args = ap.parse_args()
    global LOG_PATH
    if args.log != "off":
        LOG_PATH = str(Path(args.log).resolve()) if args.log else log_path_for("child", __file__)
        print(f"[child] 中央事件 log：{LOG_PATH}  (append-only)")
    if args.demo:
        run_demo(args)
    elif args.bench:
        run_bench(args)
    else:
        try:
            run_gui()
        except Exception as e:
            print("GUI 無法啟動:", type(e).__name__, e)
            print("請改用壓測模式(雙擊 2_子_壓測.bat)，或命令列：")
            print("  child_edge.exe --bench --host <父端IP> --port 8770")
            try:
                input("按 Enter 關閉...")
            except Exception:
                pass


if __name__ == "__main__":
    main()
