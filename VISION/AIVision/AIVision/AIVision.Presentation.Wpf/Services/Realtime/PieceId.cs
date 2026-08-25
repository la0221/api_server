using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>
/// 單片識別碼產生器：<c>{站號}_{yyyyMMdd}_{當日流水}</c>，例如 <c>ST-01_20260824_000123</c>。
///
/// <para><b>為什麼要這個</b>：原本的 <c>T000001</c> 是記憶體流水號，程式一重開就歸零，
/// 隔天／重啟後的檔名會互撞，而且**沒有送給父端**，兩邊 log 對不起來。
/// pieceId 要一路貫穿：原圖檔名、前處理圖檔名、結果 json、送父端的欄位、
/// 父端的最近辨識紀錄、吹氣去重、事件 log —— 沒有它，後面全部對不了帳。</para>
///
/// <para><b>重啟自我修復</b>：跨日自動換號段；同一天重啟時會去掃當天資料夾裡已存在的最大流水，
/// 從那裡往下接，所以**不必額外存狀態檔**，也不會覆蓋既有紀錄。</para>
/// </summary>
public sealed class PieceIdFactory
{
    private static readonly Regex SeqPattern =
        new(@"_(\d{6})(?:_|\.)", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private string _day = "";
    private int _seq;

    /// <summary>紀錄根目錄（用來在重啟時掃當天已用到的流水）。null＝不掃，從 0 開始。</summary>
    public string? RecordRoot { get; set; }

    public PieceIdFactory(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    /// <summary>配一個新的 pieceId。執行緒安全。</summary>
    public string Next(string stationId)
    {
        var t = _now();
        var day = t.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            if (day != _day)
            {
                _day = day;
                // 跨日或首次：接續當天已存在的最大流水，重啟不撞號
                _seq = ScanExistingMax(day);
            }
            _seq++;
            return $"{Sanitize(stationId)}_{day}_{_seq:000000}";
        }
    }

    /// <summary>掃當天資料夾裡已用到的最大流水；掃不到（沒資料夾/沒權限）回 0。</summary>
    private int ScanExistingMax(string day)
    {
        var root = RecordRoot;
        if (string.IsNullOrWhiteSpace(root)) return 0;
        try
        {
            var dir = Path.Combine(root, day);
            if (!Directory.Exists(dir)) return 0;
            var max = 0;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var m = SeqPattern.Match(Path.GetFileName(f));
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
            }
            return max;
        }
        catch
        {
            // 掃不到就從 0 開始——檔名重複時存檔端會自己加尾碼，不會覆蓋
            return 0;
        }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "ST";
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '_' }).ToArray();
        var t = new string(s.Trim().Select(c => bad.Contains(c) ? '-' : c).ToArray());
        return t.Length == 0 ? "ST" : t;
    }
}
