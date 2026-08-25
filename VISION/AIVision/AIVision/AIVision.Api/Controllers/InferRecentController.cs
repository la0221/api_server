using System;
using System.Collections.Generic;
using System.Linq;
using AIVision.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AIVision.Api.Controllers;

/// <summary>
/// 最近辨識紀錄查詢（2026-08-19）——父端監控畫面「最近辨識紀錄」的資料來源。
///
/// <para><b>為什麼要有</b>：父端原本只答得出「服務活著／模型載入了」，
/// 就算真的收到圖也照不出來，現場<b>無法確認父端有沒有收到</b>。
/// 這支端點把 server 記憶體裡的收件流水吐出來，等同 POC 父端狀態頁的功能。</para>
///
/// <para>資料只在記憶體（重啟即清空），刻意如此：這是監看不是稽核帳，
/// 要留存的驗收紀錄在站端的 <c>routeA_events_*.jsonl</c>。</para>
/// </summary>
[ApiController]
[Route("api/infer")]
public sealed class InferRecentController : ControllerBase
{
    private readonly RecentInferenceStore _recent;
    private readonly ReceivedImageStore _images;

    public InferRecentController(RecentInferenceStore recent, ReceivedImageStore images)
    {
        _recent = recent;
        _images = images;
    }

    /// <summary>最近 N 筆（新→舊）。<paramref name="take"/> 預設 50，上限 300。</summary>
    [HttpGet("recent")]
    [ProducesResponseType(typeof(RecentInferenceResponse), StatusCodes.Status200OK)]
    public IActionResult Recent([FromQuery] int take = 50)
    {
        var items = _recent.Take(take <= 0 ? 50 : take);
        return Ok(new RecentInferenceResponse
        {
            TotalReceived = _recent.TotalReceived,
            Items = items.Select(e => new RecentInferenceItemDto
            {
                Seq = e.Seq,
                Time = e.Timestamp.ToString("HH:mm:ss"),
                Timestamp = e.Timestamp,
                Task = e.Task,
                StationId = e.StationId,
                Reading = e.Reading,
                HasReading = e.HasReading,
                NeedsReview = e.NeedsReview,
                ReceivedBytes = e.ReceivedBytes,
                IsStrip = e.IsStrip,
                ModelVersion = e.ModelVersion,
                ElapsedMs = e.ElapsedMs,
                EngineMs = e.EngineMs,
                EdgeRawPath = e.EdgeRawPath,
                SavedImagePath = e.SavedImagePath,
                HasImage = !string.IsNullOrEmpty(e.SavedImagePath),
                Ok = e.Ok,
                Error = e.Error,
            }).ToList(),
        });
    }

    /// <summary>單筆詳細（父端「點進去看細節」用）。</summary>
    [HttpGet("recent/{seq:long}")]
    [ProducesResponseType(typeof(RecentInferenceItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult One(long seq)
    {
        var e = _recent.TryGet(seq);
        if (e is null)
            return Problem($"找不到流水號 {seq}（可能已被較新的紀錄擠出保留範圍，或 server 重啟過）。",
                statusCode: StatusCodes.Status404NotFound);
        return Ok(new RecentInferenceItemDto
        {
            Seq = e.Seq,
            Time = e.Timestamp.ToString("HH:mm:ss"),
            Timestamp = e.Timestamp,
            Task = e.Task,
            StationId = e.StationId,
            Reading = e.Reading,
            HasReading = e.HasReading,
            NeedsReview = e.NeedsReview,
            ReceivedBytes = e.ReceivedBytes,
            IsStrip = e.IsStrip,
            ModelVersion = e.ModelVersion,
            ElapsedMs = e.ElapsedMs,
            EngineMs = e.EngineMs,
            EdgeRawPath = e.EdgeRawPath,
            SavedImagePath = e.SavedImagePath,
            HasImage = !string.IsNullOrEmpty(e.SavedImagePath),
            Ok = e.Ok,
            Error = e.Error,
        });
    }

    /// <summary>取這筆留存下來的影像（沒開留存或檔案已被清掉 → 404）。</summary>
    [HttpGet("recent/{seq:long}/image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Image(long seq)
    {
        var e = _recent.TryGet(seq);
        if (e is null)
            return Problem($"找不到流水號 {seq}。", statusCode: StatusCodes.Status404NotFound);
        if (string.IsNullOrEmpty(e.SavedImagePath))
            return Problem("這筆沒有留存影像（父端的「留存收到的影像」預設是關閉的）。",
                statusCode: StatusCodes.Status404NotFound);
        if (!System.IO.File.Exists(e.SavedImagePath))
            return Problem($"留存檔案已不存在：{e.SavedImagePath}（可能已超過保留張數被清掉）。",
                statusCode: StatusCodes.Status404NotFound);
        return PhysicalFile(e.SavedImagePath, "image/png");
    }

    /// <summary>影像留存設定與現況（父端畫面用）。</summary>
    [HttpGet("recent/images")]
    [ProducesResponseType(typeof(ReceivedImageSettingsDto), StatusCodes.Status200OK)]
    public IActionResult ImageSettings()
    {
        var (count, bytes) = _images.Stat();
        return Ok(new ReceivedImageSettingsDto
        {
            Save = _images.Save,
            Folder = _images.Folder,
            MaxFiles = _images.MaxFiles,
            SavedCount = count,
            SavedBytes = bytes,
        });
    }

    /// <summary>
    /// 開／關「留存收到的影像」。⚠ 只影響本次執行；永久生效請改 appsettings 的 <c>ReceivedImages:Save</c>。
    /// </summary>
    [HttpPost("recent/images")]
    [ProducesResponseType(typeof(ReceivedImageSettingsDto), StatusCodes.Status200OK)]
    public IActionResult SetImageSettings([FromBody] SetReceivedImageRequest request)
    {
        _images.SetSave(request?.Save ?? false);
        return ImageSettings();
    }

    /// <summary>清空紀錄（現場想從乾淨畫面開始觀察）。</summary>
    [HttpPost("recent/clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Clear()
    {
        _recent.Clear();
        return Ok(new { Cleared = true });
    }
}

/// <summary><c>GET /api/infer/recent</c> 的回應。</summary>
public sealed class RecentInferenceResponse
{
    /// <summary>server 啟動以來累計收到的筆數（不受保留上限影響）。</summary>
    public long TotalReceived { get; set; }

    public List<RecentInferenceItemDto> Items { get; set; } = new();
}

/// <summary>一筆收件紀錄。</summary>
public sealed class RecentInferenceItemDto
{
    public long Seq { get; set; }
    public string Time { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Task { get; set; } = "";
    public string StationId { get; set; } = "";
    public string Reading { get; set; } = "";
    public bool HasReading { get; set; }
    public bool NeedsReview { get; set; }
    public long ReceivedBytes { get; set; }
    public bool IsStrip { get; set; }
    public string? ModelVersion { get; set; }
    public int ElapsedMs { get; set; }
    public int EngineMs { get; set; }
    public string? EdgeRawPath { get; set; }

    /// <summary>本機留存這張影像的路徑（沒開留存就是 null）。</summary>
    public string? SavedImagePath { get; set; }

    /// <summary>是否有留存影像可看（前端用它決定要不要顯示「看圖」）。</summary>
    public bool HasImage { get; set; }

    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>影像留存設定與現況。</summary>
public sealed class ReceivedImageSettingsDto
{
    /// <summary>目前是否留存收到的影像。</summary>
    public bool Save { get; set; }

    /// <summary>存放資料夾（絕對路徑）。</summary>
    public string Folder { get; set; } = "";

    /// <summary>保留張數上限（超過刪最舊）。</summary>
    public int MaxFiles { get; set; }

    /// <summary>目前已留存張數。</summary>
    public int SavedCount { get; set; }

    /// <summary>目前已留存總位元組。</summary>
    public long SavedBytes { get; set; }
}

/// <summary><c>POST /api/infer/recent/images</c> 的請求。</summary>
public sealed class SetReceivedImageRequest
{
    public bool Save { get; set; }
}
