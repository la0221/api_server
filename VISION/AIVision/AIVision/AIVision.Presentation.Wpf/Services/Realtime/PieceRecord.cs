using System;
using System.Text.Json.Serialization;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>
/// 一片的完整結果。**這就是稽核帳與訓練資料的單位**。
///
/// <para>為什麼走 json 不塞檔名：要記的東西太多（工單預期、兩軸信心、套用門檻、模型版本、
/// 來源是中央還本機、耗時、判定、吹沒吹、實際觸發→吹氣間隔）。檔名只保留 pieceId 供人眼掃。</para>
///
/// <para>附帶好處：自我強化訓練直接有料吃 —— 每片都有「圖＋正解＋模型當時怎麼判」。</para>
/// </summary>
public sealed class PieceRecord
{
    // ── 身分 ──
    public string PieceId { get; set; } = "";
    public string StationId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    /// <summary>觸發時刻（TickCount64）。跨機對帳與算真實延遲用。</summary>
    public long TrigTick { get; set; }
    public string TriggerSource { get; set; } = "";

    // ── 工單（判定的依據，只有站端知道）──
    public string? WorkOrder { get; set; }
    public string? ExpectedMohao { get; set; }
    public string? ExpectedXuehao { get; set; }

    // ── 讀值 ──
    public bool ObjectPresent { get; set; }
    public string? Mohao { get; set; }
    public string? Xuehao { get; set; }
    public double ConfMohao { get; set; }
    public double ConfXuehao { get; set; }
    public bool HasReading { get; set; }
    public bool NeedsReview { get; set; }
    /// <summary>父端回聲的判定門檻（隨模型版本走）。null＝沿用內建。</summary>
    public double? ReviewThresholdMohao { get; set; }
    public double? ReviewThresholdXuehao { get; set; }

    // ── 來源與模型（溯源）──
    /// <summary><c>central</c>＝父端讀的；<c>local</c>＝本機接管。</summary>
    public string Source { get; set; } = "";
    public string? SourceReason { get; set; }
    public string? ModelVersion { get; set; }
    public string? Engine { get; set; }
    public int ServerMs { get; set; }
    public double ElapsedMs { get; set; }

    // ── 判定（站端做）──
    /// <summary>CONFIRM／ACCEPT／MISMATCH／NG／SKIP。</summary>
    public string Outcome { get; set; } = "";
    public string? OutcomeReason { get; set; }

    // ── 吹氣 ──
    public bool Blown { get; set; }
    public int BlowDelayMs { get; set; }
    /// <summary>實際「觸發 → 送出吹氣請求」的間隔（ms）。現場調延遲時看這個，不必靠猜。</summary>
    public long? BlowElapsedFromTriggerMs { get; set; }

    // ── 影像（相對於紀錄根目錄）──
    public string? RawPath { get; set; }
    public string? StripPath { get; set; }
    public long RawBytes { get; set; }
    public long StripBytes { get; set; }

    [JsonIgnore]
    public string ReadingText => HasReading ? $"{Mohao}/{Xuehao}" : (ObjectPresent ? "(讀不到)" : "(無鏡片)");
}
