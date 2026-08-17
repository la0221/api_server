using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Services;

/// <summary>
/// 中央推論 server 清單的使用者級持久化（「API 伺服器設定」的自建接口）。
/// <para>
/// 存於 <c>%LocalAppData%\AIVision\inference_servers.json</c>——**刻意不寫 bin 的 appsettings**：
/// bin 檔會在下次 build 被原始檔覆蓋（AINAVI 線 models.online.json 踩過的坑，見 07-16 bug_notes）。
/// appsettings 的 <c>InferenceServer:KnownServers</c> 只作為**首次種子**；檔案存在後以檔案為準。
/// </para>
/// </summary>
public sealed class InferenceServerListStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ILogger<InferenceServerListStore>? _logger;
    private readonly object _gate = new();

    public InferenceServerListStore(ILogger<InferenceServerListStore>? logger = null)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIVision");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "inference_servers.json");
    }

    /// <summary>持久化內容：伺服器清單 + 最後套用的位址。</summary>
    public sealed class Data
    {
        public List<string> Servers { get; set; } = new();
        public string? ActiveBaseUrl { get; set; }
    }

    /// <summary>讀取；檔案不存在或壞檔回 null（呼叫端退回 appsettings 種子）。</summary>
    public Data? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return null;
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path));
                return data?.Servers is { } ? data : null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ServerListStore] 讀取失敗，退回預設: {Path}", _path);
                return null;
            }
        }
    }

    /// <summary>寫入（整份覆蓋）。失敗只記錄不拋（清單功能失效不應影響推論）。</summary>
    public void Save(IEnumerable<string> servers, string? activeBaseUrl)
    {
        lock (_gate)
        {
            try
            {
                var data = new Data
                {
                    Servers = servers.Where(s => !string.IsNullOrWhiteSpace(s))
                                     .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    ActiveBaseUrl = activeBaseUrl,
                };
                File.WriteAllText(_path, JsonSerializer.Serialize(data, JsonOpts));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[ServerListStore] 寫入失敗: {Path}", _path);
            }
        }
    }

    /// <summary>檔案實際位置（顯示給使用者）。</summary>
    public string FilePath => _path;
}
