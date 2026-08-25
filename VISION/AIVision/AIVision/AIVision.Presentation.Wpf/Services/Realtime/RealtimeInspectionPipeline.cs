using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.MoldCode;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;
using AIVision.Infrastructure.MoldCode;
using AIVision.MoldCode.Onnx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>一片處理完的通知（給畫面用）。</summary>
public sealed class PieceCompletedEventArgs : EventArgs
{
    public required PieceRecord Record { get; init; }

    /// <summary>命中那一幀的原圖（JPEG）。畫面對照用；設定關掉存原圖時為 null。</summary>
    public byte[]? RawJpeg { get; init; }

    /// <summary>前處理後的條圖（PNG，就是實際送父端那張）。看前處理有沒有裁對。</summary>
    public byte[]? StripPng { get; init; }
}

/// <summary>擷取失誤（整個窗都沒有一幀過閘門）。</summary>
public sealed class CaptureFaultEventArgs : EventArgs
{
    public required string PieceId { get; init; }
    public required string Reason { get; init; }
    public required int ProbedFrames { get; init; }
}

/// <summary>
/// 模號穴號**實時檢測管線**（子端）。
///
/// <para>結構移植自 <c>D:\模號檢驗\相機版</c> —— 那一版已在現場驗證，
/// 目前手上所有模號穴號訓練圖都是它拍的。中間插入一次父子傳球。</para>
///
/// <code>
/// 相機每一幀 → FrameRing（保留 2500ms）
/// IO 觸發    → 只記 { pieceId, trigTick, deadline }，不拍照
/// 檢測迴圈   → 在窗內逐幀回頭找：
///                ③a 快速閘門（只 Hough）→ 完整鏡片在 ROI 內？否則換下一幀
///                ③b 命中 → 前處理 → 送父端（預算 100ms）→ 逾時改本機
///                ④  拿讀值 vs 工單判定（判定權在站端）
///                ⑤  存三件套 → ⑥ 吹氣（MISMATCH/NG）
///              窗到期都沒命中 → 擷取失誤（不吹、不存，多半是重複觸發）
/// </code>
///
/// <para><b>鐵律</b>：父端掛掉不停線（本機接管）；存圖失敗不停線；吹氣送不出去不停線。</para>
/// </summary>
public sealed class RealtimeInspectionPipeline : IAsyncDisposable
{
    private readonly ICameraPort _camera;
    private readonly CrnnInferClient _central;
    private readonly IMoldCodePairRecognizerPort _local;
    private readonly IBlowDispatcherPort? _blow;
    private readonly PieceRecordStore _store;
    private readonly RealtimeEventLog _eventLog;
    private readonly DiskSpaceMonitor _disk;
    private readonly PieceIdFactory _ids;
    /// <summary>ROI（存比例、與解析度無關）。畫面畫的框與這裡用的必須是同一份。</summary>
    private readonly RoiSettings _roi;
    private int _roiFrameW, _roiFrameH;
    private readonly ILogger<RealtimeInspectionPipeline>? _logger;

    private readonly FrameRing _ring = new();

    /// <summary>
    /// 前處理參數（ROI／Hough／RInner／Imgsz）。
    /// <para>⚠ **一定要從 appsettings 的 <c>MoldCodeWarpPolar:Preprocess</c> 取**，
    /// 不可以用 <c>new WarpPolarParams()</c> 的預設值：預設 <c>RoiW=RoiH=0</c> 等於**不裁 ROI**，
    /// 而現場設定是 <c>RoiX=240, RoiY=0, RoiW=700, RoiH=680</c>。
    /// 用預設值的話會拿整幅相機影像去找圓 —— 抓到背景/治具的圓、裁到錯的區域，讀值全錯，
    /// 而且畫面上看起來一切正常。這種東西上機才發現就是半天。</para>
    /// <para>同時保證閘門、前處理、本機辨識器三者看同一組參數，不會各用一套而漂移。</para>
    /// </summary>
    private readonly WarpPolarParams _pre;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastConsumedTick;
    private long _serverDownUntil;   // TickCount64；期間直接走本機不再空等
    private int _consecutiveServerFailures;

    public RealtimeInspectionPipeline(
        ICameraPort camera,
        CrnnInferClient central,
        IMoldCodePairRecognizerPort local,
        PieceRecordStore store,
        RealtimeEventLog eventLog,
        DiskSpaceMonitor disk,
        PieceIdFactory ids,
        IOptions<MoldCodeWarpPolarOptions> preprocess,
        RoiSettings roi,
        IBlowDispatcherPort? blow = null,
        ILogger<RealtimeInspectionPipeline>? logger = null)
    {
        _pre = preprocess.Value.Preprocess ?? new WarpPolarParams();
        _roi = roi;
        // 第一次執行時把 appsettings 的像素 ROI 搬成比例（之後以 configs/roi.json 為準）。
        // 幀尺寸先用相機設定的目標值，等真的收到影像再依實際尺寸校正一次。
        _roi.Load();
        // ROI 可能從**任何一個畫面**被改（主頁、實時檢測頁）。訂在這裡，管線就一定跟得上，
        // 不必靠各個 View 記得回來呼叫 RefreshRoi —— 少一個「畫的框≠判定的區」的機會。
        _roi.Changed += OnRoiSettingsChanged;
        _camera = camera;
        _central = central;
        _local = local;
        _store = store;
        _eventLog = eventLog;
        _disk = disk;
        _ids = ids;
        _blow = blow;
        _logger = logger;
        _ids.RecordRoot = store.Root;
    }

    /// <summary>連續幾次「對方不可用」才進降級冷卻。單次瞬斷不該讓整段產線降級。</summary>
    private const int ConsecutiveFailuresBeforeCooldown = 3;

    public RealtimeInspectionOptions Options => _store.Options;

    /// <summary>觸發佇列與帳目。畫面直接綁它的計數。</summary>
    public TriggerQueue Triggers { get; } = new();

    public bool IsRunning { get; private set; }

    /// <summary>目前是否走中央（false＝本機接管中）。畫面的 SRV 燈綁它。</summary>
    public bool CentralOnline { get; private set; } = true;

    /// <summary>
    /// 相機的 IO 觸發線讀不讀得到。false＝**現場按開關不會有反應**，只能用畫面的手動觸發鈕
    /// —— 這件事一定要在畫面上講出來，不然現場會以為是感測器壞了。
    /// </summary>
    public bool TriggerLineReady =>
        _camera is ICameraTriggerLinePort tl && tl.IsTriggerLineReady;

    /// <summary>觸發線名稱（Line0…），顯示用。</summary>
    public string TriggerLineName =>
        _camera is ICameraTriggerLinePort tl ? tl.TriggerLineName : "-";

    /// <summary>實際生效的前處理參數（畫面要用同一份去畫 ROI 框，才不會畫的跟判定的不一樣）。</summary>
    public WarpPolarParams PreprocessParams => _pre;

    /// <summary>ROI 被改過（畫面框選/重設）→ 立刻換算成目前幀尺寸的像素並套用。</summary>
    public void RefreshRoi(int frameW, int frameH)
    {
        if (frameW <= 0 || frameH <= 0) { frameW = _roiFrameW; frameH = _roiFrameH; }
        if (frameW <= 0 || frameH <= 0) return;
        _roi.ApplyTo(_pre, frameW, frameH);
        _roiFrameW = frameW; _roiFrameH = frameH;
        Emit($"ROI 已更新：{_roi.Describe(frameW, frameH)}（立即生效）");
    }

    /// <summary>ROI 設定被改動（哪個畫面改的都算）→ 立刻換算成目前幀尺寸並套用。</summary>
    private void OnRoiSettingsChanged(object? sender, EventArgs e)
    {
        // 還沒收到第一幀就不知道要乘上多少像素；OnFrame 拿到尺寸時會自己套一次。
        if (_roiFrameW <= 0 || _roiFrameH <= 0) return;
        _roi.ApplyTo(_pre, _roiFrameW, _roiFrameH);
        Emit($"ROI 已更新：{_roi.Describe(_roiFrameW, _roiFrameH)}（立即生效）");
    }

    /// <summary>目前 ROI 的說明文字。</summary>
    public string RoiDescription(int frameW, int frameH) =>
        _roi.Describe(frameW > 0 ? frameW : _roiFrameW, frameH > 0 ? frameH : _roiFrameH);

    /// <summary>本工單的預期模號／穴號。沒有工單就判不了混料——由畫面設定。</summary>
    public string? ExpectedMohao { get; set; }
    public string? ExpectedXuehao { get; set; }
    public string? WorkOrder { get; set; }

    public event EventHandler<PieceCompletedEventArgs>? PieceCompleted;
    public event EventHandler<CaptureFaultEventArgs>? CaptureFault;
    public event EventHandler<DiskStatus>? DiskAnnouncement;
    public event EventHandler<string>? Log;

    /// <summary>每一幀（給畫面做即時預覽用）。⚠ 在擷取執行緒上，UI 端務必節流。</summary>
    public event EventHandler<ImageData>? LatestFrame;

    // ── 啟停 ────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        Options.Normalize();

        // ⚠ 假相機要**擋下來並講原因**。不擋的話畫面顯示「運行中、等待觸發」，
        //    但永遠不會有幀進環形緩衝 → 每一次觸發都變成「擷取失誤」，
        //    現場會以為是觸發訊號有問題，實際上是相機根本沒接（8/19 已經踩過一次同類的坑）。
        var camName = _camera.GetType().Name;
        if (camName.Contains("Fake", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"目前使用的是模擬相機（{camName}），實時檢測需要真實相機。" +
                "請確認 Devices:Camera:Type 不是 Fake，且沒有載入 appsettings.Development.json。");

        if (!_camera.IsOpen)
            await _camera.OpenAsync("", ct).ConfigureAwait(false);

        _ring.Clear();
        Triggers.Reset();
        _lastConsumedTick = 0;
        _serverDownUntil = 0;
        _consecutiveServerFailures = 0;
        CentralOnline = true;
        _disk.ResetAnnouncement();

        _camera.FrameReceived += OnFrame;

        // ★ 接上相機的 IO 觸發線（現場的開關接在這裡，不是接 PLC）。
        //   沒有這條線的相機（假相機/webcam）就只剩手動觸發，畫面會標明。
        if (_camera is ICameraTriggerLinePort trigger)
            trigger.TriggerLineRose += OnTriggerLineRose;

        await _camera.StartPreviewAsync(ct).ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);

        IsRunning = true;
        _eventLog.SessionStart(Options.StationId, WorkOrder, ExpectedMohao, ExpectedXuehao,
            Options.CaptureWindowMs, Options.ServerBudgetMs);
        // 啟動時就把「實際生效的參數」印出來——現場最怕的是設定沒吃到卻沒人知道
        var roi = _pre.RoiW > 0 && _pre.RoiH > 0
            ? $"ROI({_pre.RoiX},{_pre.RoiY},{_pre.RoiW},{_pre.RoiH})"
            : "⚠ ROI 未設定（會用整張影像找圓，可能抓到背景的圓）";
        var trig = TriggerLineReady
            ? $"IO 觸發線 {TriggerLineName} 就緒（按開關即觸發）"
            : "⚠ 讀不到相機 IO 觸發線 → **現場按開關不會有反應**，只能用「手動觸發」鈕";
        Emit($"實時檢測已啟動。擷取窗 {Options.CaptureWindowMs}ms、父端預算 {Options.ServerBudgetMs}ms、"
           + $"{roi}、Hough r={_pre.HoughMinRadius}~{_pre.HoughMaxRadius}。{trig}。");
        AnnounceDisk(force: true);
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _camera.FrameReceived -= OnFrame;
        if (_camera is ICameraTriggerLinePort trigger)
            trigger.TriggerLineRose -= OnTriggerLineRose;
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;

        _eventLog.SessionEnd(Options.StationId, Triggers.Triggered, Triggers.Central,
            Triggers.Local, Triggers.CaptureFault, Triggers.Dropped, Triggers.Pending, Triggers.Balanced);

        // 帳不平就一定要講出來——那代表有一條路沒記帳
        Emit("實時檢測已停止。" + Triggers.Ledger);
        if (!Triggers.Balanced)
            _logger?.LogWarning("[Realtime] 帳不平：{Ledger}", Triggers.Ledger);

        _ring.Clear();
    }

    /// <summary>觸發一次檢測（IO 上升緣或手動）。可從任意執行緒呼叫，不會阻塞。</summary>
    public void FireTrigger(string source)
    {
        if (!IsRunning) return;

        var tick = Environment.TickCount64;
        var pieceId = _ids.Next(Options.StationId);
        var job = Triggers.Enqueue(pieceId, tick, Options.CaptureWindowMs, source);
        if (job is null)
        {
            Emit($"[{source}] ⚠ 待補積壓過多（≥{TriggerQueue.MaxPending}）→ 本次觸發丟棄"
               + $"（累計 {Triggers.Dropped}）。產線可能遠快於辨識。");
            _eventLog.TriggerDropped(Options.StationId, pieceId, source, Triggers.Dropped);
        }
    }

    // ── 相機每一幀 ──────────────────────────────────────────────────────

    private void OnFrame(object? sender, ImageData image)
    {
        if (!IsRunning) return;

        // 幀尺寸確定後才套 ROI —— ROI 存的是比例，要乘上**實際**幀尺寸才知道是哪幾個像素。
        // 尺寸變了（換相機/改解析度）也會重算，這正是存比例的好處。
        if (image.Width > 0 && (image.Width != _roiFrameW || image.Height != _roiFrameH))
        {
            // 第一次執行：把 appsettings 既有的像素 ROI 搬成比例並存檔（之後以 roi.json 為準）
            _roi.SeedFromPixels(_pre, image.Width, image.Height);
            _roi.ApplyTo(_pre, image.Width, image.Height);
            _roiFrameW = image.Width;
            _roiFrameH = image.Height;
            Emit($"影像 {image.Width}×{image.Height} → {_roi.Describe(image.Width, image.Height)}");
        }

        _ring.Add(image, Environment.TickCount64);
        LatestFrame?.Invoke(this, image);   // 畫面即時預覽（丟給 UI 節流顯示）
    }

    /// <summary>相機 IO 線上升緣＝現場開關按下。在擷取執行緒上，要短。</summary>
    private void OnTriggerLineRose(object? sender, EventArgs e) => FireTrigger("IO");

    // ── 檢測迴圈 ────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Triggers.TryPeek(out var job))
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
                continue;
            }

            var now = Environment.TickCount64;

            // 這筆的可用幀：窗內、且比「已被別筆用掉的游標」新（→ 一片一張）
            var after = Math.Max(job.Cursor, Math.Max(_lastConsumedTick, job.TrigTick - 1));
            var frames = _ring.Window(after, Math.Min(now, job.Deadline));

            var hit = false;
            var probed = 0;
            var lastWhy = "窗內沒有任何影像";

            foreach (var f in frames)
            {
                if (ct.IsCancellationRequested) return;
                job.Cursor = f.Tick;
                probed++;

                using var bgr = WarpPolarPreprocessor.BgrFromImageData(f.Image);
                if (bgr.Empty()) { lastWhy = "影像解碼失敗"; continue; }

                // ③a 快速閘門：只跑 Hough，便宜。沒過就換下一幀（**不是不良品**）
                var probe = LensProbe.Probe(bgr, _pre, Options.LensEdgeTolerance);
                if (!probe.Ok) { lastWhy = probe.Why; continue; }

                // ③b 命中：這一幀就是「工件完整進框」的那一瞬間。
                // ★ 先出隊再處理：處理途中若被停止打斷，計數與佇列才不會對不起來
                //   （否則會在停止時報「帳不平」而其實只是被中斷 —— 狼來了會讓真正的不平沒人理）。
                Triggers.TryDequeue(out _);
                hit = true;
                try
                {
                    await ProcessHitAsync(job, f, bgr, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 只有「還沒記過帳」才算中斷；已經記成中央/本機的不能再記一次，否則帳會多一筆
                    if (!job.Counted) Triggers.MarkInterrupted();
                    return;
                }
                _lastConsumedTick = f.Tick;
                break;
            }

            if (hit) continue;

            if (now >= job.Deadline)
            {
                // 窗到期還沒命中 → 擷取失誤。**不吹、不存**。
                Triggers.TryDequeue(out _);
                Triggers.MarkCaptureFault();
                _lastConsumedTick = Math.Max(_lastConsumedTick, job.Deadline);
                _eventLog.CaptureFault(Options.StationId, job.PieceId, job.Source, lastWhy, probed);
                CaptureFault?.Invoke(this, new CaptureFaultEventArgs
                {
                    PieceId = job.PieceId,
                    Reason = lastWhy,
                    ProbedFrames = probed,
                });
                Emit($"⚠ 擷取失誤 {job.PieceId}：{lastWhy}（窗內探了 {probed} 幀）"
                   + "　→ 產線上多半是**重複觸發拍照**，請查觸發訊號。不吹氣。");
                continue;
            }

            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    // ── 命中之後：前處理 → 父端/本機 → 判定 → 存檔 → 吹氣 ──────────────

    private async Task ProcessHitAsync(PendingCapture job, RingFrame frame, Mat bgr, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var record = new PieceRecord
        {
            PieceId = job.PieceId,
            StationId = Options.StationId,
            Timestamp = DateTime.Now,
            TrigTick = job.TrigTick,
            TriggerSource = job.Source,
            WorkOrder = WorkOrder,
            ExpectedMohao = ExpectedMohao,
            ExpectedXuehao = ExpectedXuehao,
            BlowDelayMs = 0,
        };

        // 前處理：與模型同一套（找圓→裁圓→極座標展開→640 白底）
        using var roi = WarpPolarPreprocessor.CropRoi(bgr, _pre);
        Mat? strip = WarpPolarPreprocessor.Preprocess(roi, 0.0, _pre);

        byte[]? stripPng = null;
        if (strip is not null) Cv2.ImEncode(".png", strip, out stripPng);

        // ★ 原圖**先落地**，才有真實路徑可以隨請求送給父端（父端的「站端原圖位置」靠它溯源）。
        //   放最後存的話，送出去時 RawPath 還是 null，父端就查不回這張圖在站端哪裡。
        byte[]? rawJpeg = null;
        // 只存 ROI 區域，不存全幅（2026-08-25 現場要求）：
        //   全幅 1280x1024 一張約 350KB，但 ROI 外全是機構背景、對事後追查毫無用處，
        //   產線跑整天就是幾 GB 的廢空間。ROI 內已含完整鏡片與字樣，追查夠用。
        //   直接沿用上面前處理已裁好的 roi，不重算（同一份 ROI 設定，保證與辨識範圍一致）。
        if (Options.SaveRawImage) Cv2.ImEncode(".jpg", roi.Empty() ? bgr : roi, out rawJpeg);
        await _store.SaveRawAsync(record, rawJpeg, ct).ConfigureAwait(false);

        // ── 讀值：優先父端，逾時／不可用改本機（鐵律：父端掛掉不停線）
        PairObservation obs;
        if (stripPng is { Length: > 0 } && Environment.TickCount64 >= _serverDownUntil)
        {
            var (o, ok, reason) = await ReadFromCentralAsync(stripPng, record, ct).ConfigureAwait(false);
            if (ok)
            {
                _consecutiveServerFailures = 0;   // 成功就把連續失敗歸零
                obs = o;
                record.Source = "central";
                Triggers.MarkCentral();
                job.Counted = true;
                SetCentralOnline(true);
            }
            else
            {
                obs = ReadFromLocal(frame.Image, record);
                record.Source = "local";
                record.SourceReason = reason;
                Triggers.MarkLocal();
                job.Counted = true;
                SetCentralOnline(false);
            }
        }
        else
        {
            obs = ReadFromLocal(frame.Image, record);
            record.Source = "local";
            record.SourceReason = stripPng is null
                ? "前處理沒產出條圖"
                : "父端降級冷卻中";
            Triggers.MarkLocal();
            job.Counted = true;
        }

        record.ObjectPresent = obs.ObjectPresent;
        record.Mohao = obs.Mohao;
        record.Xuehao = obs.Xuehao;
        record.ConfMohao = obs.ConfMohao;
        record.ConfXuehao = obs.ConfXuehao;
        record.HasReading = obs.HasReading;

        // ── 判定：**判定權在站端**，因為工單只有站端知道
        var decision = MoldCodePairVerifier.Decide(
            ExpectedMohao, ExpectedXuehao, obs,
            Options.MoldThreshold, Options.CavityThreshold, Options.NgClassName);
        record.Outcome = decision.Outcome.ToString();
        record.OutcomeReason = decision.Reason;
        record.ElapsedMs = sw.Elapsed.TotalMilliseconds;

        // ── 吹氣（MISMATCH / NG）
        if (decision.ShouldReject) EnqueueBlow(job, record, decision);

        // ── 剩下兩件（原圖已在送父端之前落地）。存檔失敗不影響上面任何一步。
        await _store.SaveStripAsync(record, stripPng, ct).ConfigureAwait(false);
        await _store.SaveJsonAsync(record, ct).ConfigureAwait(false);

        _eventLog.Piece(record);
        PieceCompleted?.Invoke(this, new PieceCompletedEventArgs
        {
            Record = record,
            RawJpeg = rawJpeg,
            StripPng = stripPng,
        });
        strip?.Dispose();

        AnnounceDisk(force: false);
    }

    /// <summary>送父端。回 (觀測, 成功?, 失敗原因)。**超過預算就放棄等待**。</summary>
    private async Task<(PairObservation Obs, bool Ok, string? Reason)> ReadFromCentralAsync(
        byte[] stripPng, PieceRecord record, CancellationToken ct)
    {
        var budget = Options.ServerBudgetMs;
        var sw = Stopwatch.StartNew();
        try
        {
            // ★★ 預算到了只「**停止等待**」，**絕不中止對方的請求**。
            //
            // 踩過的坑（2026-08-24 實測）：原本用 CancelAfter(budget) 直接取消 HTTP 請求，
            // 結果父端的 CRNN sidecar 走的是 stdin/stdout 一行一問一答，
            // 請求被中途砍掉時 python 還在處理上一筆，而我方 finally 已經把暫存圖刪了
            // → 下一筆進來時 python 讀到的是**上一筆已被刪除的路徑**：
            //   `[Errno 2] No such file or directory`，一次取消污染後面連續好幾筆（實測 15 次）。
            //   協定一旦失去同步，父端就等於被我們弄壞了。
            //
            // 正解：讓那個呼叫自己跑完（結果丟掉就好），我方改用本機接管。
            // 代價只是父端多算一張沒人要的圖——還順便讓行程池保持熱。
            var call = _central.RecognizeAsync(
                stripPng, Options.StationId, ct,
                modelVersion: null, isStrip: true,
                // rawPath 要放**原圖在站端的真實路徑**（父端「站端原圖位置」欄靠它溯源）；
                // 之前誤把 pieceId 塞在這裡，父端就查不回這張圖存在站端哪裡。
                rawPath: record.RawPath,
                pieceId: record.PieceId,
                trigTick: record.TrigTick);

            var finished = await Task.WhenAny(call, Task.Delay(budget, ct)).ConfigureAwait(false);
            sw.Stop();

            if (finished != call)
            {
                // 不 await，但要觀察例外，免得變成 UnobservedTaskException
                _ = call.ContinueWith(static t => { _ = t.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                return (PairObservation.Failed("timeout"), false,
                    $"父端逾時（>{budget}ms）——只停止等待、沒有中止對方，不進降級冷卻");
            }

            var dto = await call.ConfigureAwait(false);

            if (dto is null)
            {

                // 4xx＝我方請求有問題，不是對方掛了 → 不進降級冷卻，否則一片壞圖會連坐後面一整批
                if (_central.LastFailureWasServerSide)
                {
                    // ⚠ **一次失敗不進冷卻**。父端的 CRNN sidecar 有已知的瞬斷
                    //   （暫存檔被防毒短暫鎖住 → 503，實測約 8%）。
                    //   單次就冷卻 30 秒的話，8% 的瞬斷會變成整段產線走本機舊模型 ——
                    //   跟今早在 Route A 修掉的「一件事連坐後面一整批」是同一種坑。
                    //   連續失敗才代表對方真的掛了。
                    var n = ++_consecutiveServerFailures;
                    if (n >= ConsecutiveFailuresBeforeCooldown)
                    {
                        _serverDownUntil = Environment.TickCount64 + Options.ServerDownCooldownMs;
                        return (PairObservation.Failed("central down"), false,
                            $"父端連續 {n} 次不可用（{_central.LastStatusCode?.ToString() ?? "連不上"}），進降級冷卻");
                    }
                    return (PairObservation.Failed("central error"), false,
                        $"父端這次失敗（{_central.LastStatusCode?.ToString() ?? "連不上"}），"
                        + $"第 {n}/{ConsecutiveFailuresBeforeCooldown} 次，尚未進冷卻");
                }
                return (PairObservation.Failed("central 4xx"), false,
                    $"父端拒收（HTTP {_central.LastStatusCode}）——我方請求有問題，不視為掉線");
            }

            record.ModelVersion = dto.ModelVersion;
            record.Engine = dto.Engine;
            record.ServerMs = dto.ElapsedMs;
            record.NeedsReview = dto.NeedsReview;
            record.ReviewThresholdMohao = dto.ReviewThresholdMohao;
            record.ReviewThresholdXuehao = dto.ReviewThresholdXuehao;

            var obs = dto.HasReading
                ? PairObservation.Read(dto.Mohao ?? "", dto.ConfMohao, dto.Xuehao ?? "", dto.ConfXuehao)
                : (dto.ObjectPresent
                    ? PairObservation.Failed(dto.FailureReason ?? "父端讀不到")
                    : PairObservation.NoObject(dto.FailureReason));
            return (obs, true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            // ⚠ 逾時**不進降級冷卻**：父端只是這次慢，不代表掛了。
            //   進冷卻會讓後面 30 秒全部走本機（舊模型），代價太大。
            return (PairObservation.Failed("timeout"), false,
                $"父端逾時（>{budget}ms，實測 {sw.ElapsedMilliseconds}ms）");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _serverDownUntil = Environment.TickCount64 + Options.ServerDownCooldownMs;
            return (PairObservation.Failed(ex.Message), false, $"父端呼叫失敗：{ex.Message}");
        }
    }

    private PairObservation ReadFromLocal(ImageData image, PieceRecord record)
    {
        try
        {
            var obs = _local.Recognize(image);
            record.Engine = "local-twohead";
            return obs;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Realtime] 本機辨識失敗 {PieceId}", record.PieceId);
            return PairObservation.Failed($"本機辨識失敗：{ex.Message}");
        }
    }

    private void EnqueueBlow(PendingCapture job, PieceRecord record, PairDecision decision)
    {
        if (_blow is not { Enabled: true }) return;
        try
        {
            var reason = decision.Outcome == PairVerifyOutcome.MixedAlarm
                ? BlowRequest.ReasonMismatch
                : BlowRequest.ReasonNg;

            var queued = _blow.Enqueue(new BlowRequest(
                Id: record.PieceId,                       // ★ 用 pieceId 去重：同一片只吹一次
                CreatedAt: DateTime.Now,
                Reason: reason,
                ExpectedMohao: ExpectedMohao ?? "-",
                ExpectedXuehao: ExpectedXuehao ?? "-",
                DetectedMohao: record.Mohao ?? "-",
                DetectedXuehao: record.Xuehao ?? "-",
                ConfMohao: record.ConfMohao,
                ConfXuehao: record.ConfXuehao,
                DelayMs: 0)                               // 0 = 用設定檔的 Devices:Blow:DelayMs
            {
                TriggerTick = job.TrigTick,
            });

            record.Blown = queued;
            // 現場調延遲時看這個數字：從觸發到「我方送出吹氣請求」實際花了多久。
            record.BlowElapsedFromTriggerMs = Environment.TickCount64 - job.TrigTick;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Realtime] 吹氣排入失敗（不影響判定）{PieceId}", record.PieceId);
        }
    }

    private void SetCentralOnline(bool online)
    {
        if (CentralOnline == online) return;
        CentralOnline = online;
        Emit(online
            ? "✓ 中央推論已恢復，切回中央辨識。"
            : "⚠ 改由本機模型接管（產線不停）。本機是較舊的雙 head，準確率可能較低。");
    }

    private void AnnounceDisk(bool force)
    {
        var st = _disk.Check(_store.Root);
        if (force || _disk.ShouldAnnounce(st.Level) || st.Level == DiskLevel.Critical)
        {
            DiskAnnouncement?.Invoke(this, st);
            if (st.Level != DiskLevel.Ok) Emit(st.Text);
        }
    }

    private void Emit(string msg)
    {
        Log?.Invoke(this, msg);
        _logger?.LogInformation("[Realtime] {Message}", msg);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
