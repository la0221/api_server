using System;
using System.IO;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>磁碟餘量的三個等級。</summary>
public enum DiskLevel
{
    /// <summary>還很寬裕。</summary>
    Ok,
    /// <summary>快滿了，提醒一次。</summary>
    Warning,
    /// <summary>非常低，每批都提醒。</summary>
    Critical,
}

/// <summary>磁碟餘量快照。</summary>
/// <param name="Level">等級。</param>
/// <param name="FreeBytes">可用位元組。</param>
/// <param name="TotalBytes">總容量。</param>
/// <param name="Text">給畫面看的一行字。</param>
public readonly record struct DiskStatus(DiskLevel Level, long FreeBytes, long TotalBytes, string Text)
{
    public double FreePercent => TotalBytes > 0 ? (double)FreeBytes / TotalBytes * 100 : 0;
}

/// <summary>
/// 存圖磁碟的餘量監看。
///
/// <para><b>策略（2026-08-24 使用者拍板）</b>：<b>不自動刪檔、不設保留天數</b>——只管存，
/// <b>快滿的時候通知一下</b>就好。</para>
///
/// <para><b>鐵律</b>：磁碟快滿**絕不能停線、也絕不自動刪檔**。
/// 存不進去只記 log，判定與吹氣照常跑完 —— 存圖是稽核與訓練用途，不是產線判定的一部分。</para>
/// </summary>
public sealed class DiskSpaceMonitor
{
    /// <summary>提醒門檻：低於 20 GB 或 10%（先到者）。</summary>
    public long WarnBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public double WarnPercent { get; set; } = 10.0;

    /// <summary>嚴重門檻：低於 5 GB 或 3%。</summary>
    public long CriticalBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public double CriticalPercent { get; set; } = 3.0;

    private DiskLevel _lastReported = DiskLevel.Ok;

    /// <summary>查一次。路徑不存在／查不到時回 Ok（不要因為查不到就嚇人）。</summary>
    public DiskStatus Check(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return Unknown();

            var d = new DriveInfo(root);
            if (!d.IsReady) return Unknown();

            long free = d.AvailableFreeSpace, total = d.TotalSize;
            double pct = total > 0 ? (double)free / total * 100 : 100;

            var level =
                free <= CriticalBytes || pct <= CriticalPercent ? DiskLevel.Critical
                : free <= WarnBytes || pct <= WarnPercent ? DiskLevel.Warning
                : DiskLevel.Ok;

            var text = level switch
            {
                DiskLevel.Critical =>
                    $"🔴 磁碟快滿了：{root} 只剩 {Gb(free)} GB（{pct:0.0}%）。"
                    + "存圖可能失敗——請盡快搬走舊資料。（產線不會因此停止）",
                DiskLevel.Warning =>
                    $"🟠 磁碟餘量偏低：{root} 剩 {Gb(free)} GB（{pct:0.0}%）。建議找時間搬走舊資料。",
                _ => $"磁碟 {root} 剩 {Gb(free)} GB（{pct:0.0}%）",
            };
            return new DiskStatus(level, free, total, text);
        }
        catch
        {
            return Unknown();
        }
    }

    /// <summary>
    /// 只在「等級變糟」時回 true，避免每片都洗版。
    /// Critical 的話由呼叫端自行決定要不要每批再提醒一次。
    /// </summary>
    public bool ShouldAnnounce(DiskLevel level)
    {
        if (level <= _lastReported) { _lastReported = level; return false; }
        _lastReported = level;
        return true;
    }

    public void ResetAnnouncement() => _lastReported = DiskLevel.Ok;

    private static DiskStatus Unknown() =>
        new(DiskLevel.Ok, 0, 0, "磁碟餘量：查不到（不影響產線）");

    private static string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.0");
}
