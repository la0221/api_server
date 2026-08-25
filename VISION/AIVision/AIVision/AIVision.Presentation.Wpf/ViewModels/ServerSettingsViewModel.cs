using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AIVision.Infrastructure.MoldCode;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 「API 伺服器設定」視窗：選擇/手填/自建中央推論 server 清單 → 套用並測試連線。
/// <para>
/// 清單與「最後套用位址」持久化於使用者設定檔（<see cref="InferenceServerListStore"/>，
/// %LocalAppData%\AIVision\inference_servers.json）→ **重啟仍生效**（App 啟動時還原）。
/// appsettings 的 KnownServers 僅首次種子。套用＝改寫 DI 共用 <see cref="InferenceServerOptions"/>
/// 實例（IOptions 單例快取）→ 驗收按鈕/中央批量立即改打新位址。
/// </para>
/// </summary>
public partial class ServerSettingsViewModel : ObservableObject
{
    private readonly InferenceServerOptions _options;
    private readonly RemotePairRecognizer _remote;
    private readonly ModelHubClient _modelHub;
    private readonly InferenceServerListStore _store;
    private readonly ILogger<ServerSettingsViewModel>? _logger;

    public ServerSettingsViewModel(
        IOptions<InferenceServerOptions> options,
        RemotePairRecognizer remote,
        ModelHubClient modelHub,
        InferenceServerListStore store,
        ILogger<ServerSettingsViewModel>? logger = null)
    {
        _options = options.Value;
        _remote = remote;
        _modelHub = modelHub;
        _store = store;
        _logger = logger;

        // 清單來源：使用者檔案優先；無檔案 → appsettings KnownServers 當種子。
        var saved = _store.Load();
        KnownServers = new ObservableCollection<string>(
            saved?.Servers is { Count: > 0 } s ? s : _options.KnownServers);

        ServerUrl = _options.BaseUrl;      // App 啟動時已還原過使用者最後套用值
        CurrentBaseUrl = _options.BaseUrl;
        StoreFilePath = _store.FilePath;
    }

    /// <summary>server 候選清單（可自建：加入/移除，存使用者設定檔）。</summary>
    public ObservableCollection<string> KnownServers { get; }

    /// <summary>清單檔實際位置（顯示用）。</summary>
    public string StoreFilePath { get; }

    [ObservableProperty]
    private string? serverUrl;

    [ObservableProperty]
    private string? currentBaseUrl;

    [ObservableProperty]
    private string? testResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotTesting))]
    private bool isTesting;

    public bool NotTesting => !IsTesting;

    /// <summary>把目前輸入的位址加入清單並存檔（自建接口）。</summary>
    [RelayCommand]
    private void AddServer()
    {
        var url = NormalizeUrl(ServerUrl);
        if (url is null)
        {
            TestResult = "❌ 位址格式不對，無法加入：請輸入如 http://192.168.1.50:5030";
            return;
        }
        if (KnownServers.Any(s => string.Equals(s, url, StringComparison.OrdinalIgnoreCase)))
        {
            TestResult = $"ℹ 已在清單中：{url}";
            return;
        }
        KnownServers.Add(url);
        ServerUrl = url;
        _store.Save(KnownServers, CurrentBaseUrl);
        TestResult = $"✅ 已加入清單並保存：{url}（重啟仍在）";
        _logger?.LogInformation("[ServerSettings] 清單加入: {Url}", url);
    }

    /// <summary>把目前輸入/選中的位址從清單移除並存檔（不影響目前生效位址）。</summary>
    [RelayCommand]
    private void RemoveServer()
    {
        var url = NormalizeUrl(ServerUrl);
        var hit = url is null ? null
            : KnownServers.FirstOrDefault(s => string.Equals(s, url, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            TestResult = "ℹ 清單中沒有這個位址，無可移除。";
            return;
        }
        KnownServers.Remove(hit);
        _store.Save(KnownServers, CurrentBaseUrl);
        TestResult = $"✅ 已從清單移除：{hit}" +
                     (string.Equals(hit, CurrentBaseUrl, StringComparison.OrdinalIgnoreCase)
                         ? "（目前生效位址不變，直到你套用別的）" : "");
        _logger?.LogInformation("[ServerSettings] 清單移除: {Url}", hit);
    }

    /// <summary>套用輸入的位址（全域生效 + 持久化）並打健康檢查驗證。</summary>
    [RelayCommand]
    private async Task ApplyAndTestAsync()
    {
        var url = NormalizeUrl(ServerUrl);
        if (url is null)
        {
            TestResult = "❌ 位址格式不對：請輸入如 http://192.168.1.50:5030";
            return;
        }

        IsTesting = true;
        TestResult = $"套用並測試中… {url}";
        try
        {
            _options.BaseUrl = url;          // 執行期全域生效（同一 options 實例）
            CurrentBaseUrl = url;
            ServerUrl = url;
            _store.Save(KnownServers, url);  // 持久化：重啟後 App 啟動時還原

            var sw = Stopwatch.StartNew();
            var health = await _remote.CheckHealthAsync();
            sw.Stop();

            if (health is null)
            {
                TestResult =
                    $"❌ 連不上：{url}（{sw.ElapsedMilliseconds}ms）\n" +
                    "   已套用並保存此位址，但目前不可達——請確認 server 已啟動、IP/埠正確、防火牆放行。";
                return;
            }

            TestResult = health.ModelLoaded
                ? $"✅ 連線成功（{sw.ElapsedMilliseconds}ms）｜狀態 {health.Status}｜模型 {health.ModelVersion}" +
                  $"｜類別 {health.MohaoClassCount}/{health.XuehaoClassCount}\n" +
                  "   已套用並保存：驗收按鈕/中央批量現在都打這台，重啟仍記得。"
                : $"⚠ 連上了但 server 未載入模型（{health.Status}）——可連線，但推論會 fail-closed；" +
                  "請在 server 端 appsettings 配 MoldCodeWarpPolar 模型。";

            _logger?.LogInformation("[ServerSettings] 已切換中央推論位址: {Url}（health={Status}）",
                url, health?.Status ?? "unreachable");
        }
        catch (Exception ex)
        {
            TestResult = $"❌ 測試發生例外：{ex.Message}";
            _logger?.LogWarning(ex, "[ServerSettings] 套用/測試失敗: {Url}", url);
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ===== 伺服器模型：清單 + 下載同步（ROADMAP 主項1「edge 拉同步」；按用途 task 分家）=====

    /// <summary>用途 → 本地登錄夾（下載落地處；ocr_pair 與雙head頁掃描的目錄一致）。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> LocalTaskRoots =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ocr_pair"] = @"D:\AIVisionModels\pairs",
            ["ocr_crnn"] = @"D:\AIVisionModels\ocr_crnn",
            ["gongmu"] = @"D:\AIVisionModels\gongmu",
            ["defect"] = @"D:\AIVisionModels\defect",
        };

    /// <summary>用途選項（顯示名, task 代號）。</summary>
    public ObservableCollection<ModelTaskOption> TaskOptions { get; } = new()
    {
        new("模號穴號 OCR（雙 head）", "ocr_pair"),
        new("模號穴號 OCR（CRNN 字元式）", "ocr_crnn"),
        new("公母模", "gongmu"),
        new("瑕疵檢查", "defect"),
    };

    [ObservableProperty]
    private ModelTaskOption? selectedTask;

    /// <summary>server 登錄夾版本清單（按「取得模型清單」載入）。</summary>
    public ObservableCollection<RemoteModelRow> RemoteModels { get; } = new();

    [ObservableProperty]
    private RemoteModelRow? selectedRemoteModel;

    [ObservableProperty]
    private string? modelSyncStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotSyncing))]
    private bool isSyncing;

    public bool NotSyncing => !IsSyncing;

    /// <summary>向目前生效的 server 要「選中用途」的版本清單（GET /api/models/{task}）。</summary>
    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        var task = (SelectedTask ?? TaskOptions[0]).Task;
        IsSyncing = true;
        ModelSyncStatus = $"取得模型清單中…（用途 {task}）{CurrentBaseUrl}";
        try
        {
            var list = await _modelHub.ListAsync(task);
            if (list is null)
            {
                ModelSyncStatus = $"❌ 拿不到清單：{CurrentBaseUrl}（server 未啟動、位址錯誤，或 server 版本過舊沒有 /api/models/{task}）。";
                return;
            }

            var localRoot = LocalTaskRoots.TryGetValue(task, out var r) ? r : null;
            RemoteModels.Clear();
            foreach (var v in list.Versions)
            {
                if (string.IsNullOrWhiteSpace(v.Version)) continue;
                bool localExists = localRoot is not null && v.Files.Count > 0 &&
                    v.Files.All(f => !string.IsNullOrWhiteSpace(f.Name) &&
                        System.IO.File.Exists(System.IO.Path.Combine(localRoot, v.Version!, f.Name!)));
                var marks = (v.IsServerCurrent ? "★server現用 " : "") + (localExists ? "✓本地已有" : "");
                var sizeMb = v.Files.Sum(f => f.Bytes) / 1024.0 / 1024.0;
                RemoteModels.Add(new RemoteModelRow(v,
                    $"{v.Version}　{sizeMb:F1}MB　{v.Published ?? "（無發布紀錄）"}　{marks}".TrimEnd()));
            }
            SelectedRemoteModel = RemoteModels.FirstOrDefault(r2 => !r2.Entry.IsServerCurrent) ?? RemoteModels.FirstOrDefault();
            ModelSyncStatus = $"✅ server「{task}」有 {RemoteModels.Count} 個版本" +
                              (task == "ocr_pair" ? $"（現用 {list.ServerCurrentVersion ?? "—"}）" : "") +
                              "。選一個按「下載到本地」即同步（下載後自動 md5 複驗）。";
        }
        catch (Exception ex)
        {
            ModelSyncStatus = $"❌ 取得清單例外：{ex.Message}";
            _logger?.LogWarning(ex, "[ServerSettings] 模型清單失敗");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>下載選中的版本到本地登錄夾（.tmp 串流 → md5 複驗 → 原子落地 → 溯源/同步紀錄）。</summary>
    [RelayCommand]
    private async Task DownloadSelectedModelAsync()
    {
        var row = SelectedRemoteModel;
        var task = (SelectedTask ?? TaskOptions[0]).Task;
        if (row is null)
        {
            ModelSyncStatus = "請先「取得模型清單」並選一個版本。";
            return;
        }
        if (!LocalTaskRoots.TryGetValue(task, out var localRoot))
        {
            ModelSyncStatus = $"❌ 未知用途 '{task}'，沒有對應的本地登錄夾。";
            return;
        }

        IsSyncing = true;
        ModelSyncStatus = $"下載中：{task}/{row.Entry.Version} …（下載完會自動 md5 複驗）";
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await _modelHub.DownloadVersionAsync(task, row.Entry, localRoot);
            sw.Stop();

            ModelSyncStatus = result.Success
                ? $"✅ 已下載並 md5 複驗通過：{row.Entry.Version} → {result.DestDir}（{sw.ElapsedMilliseconds}ms）\n" +
                  (task == "ocr_pair" ? "   雙head頁按「重新整理」即可在版本下拉看到並載入。" : "   已進本地登錄夾（此用途的載入頁待模型接入後提供）。")
                : $"❌ {result.Error}";

            if (result.Success)
                await FetchModelsAsync();   // 重整清單，讓「✓本地已有」標記即時更新
        }
        catch (Exception ex)
        {
            ModelSyncStatus = $"❌ 下載例外：{ex.Message}";
            _logger?.LogWarning(ex, "[ServerSettings] 模型下載失敗: {Version}", row.Entry.Version);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// 連接埠可用性檢查（AINavi 借鏡④，ROADMAP checklist）：對輸入位址的 host:port 做純 TCP 連線探測。
    /// 與「套用並測試連線」的差別：這裡不打 HTTP、不改任何設定——單純回答「這個埠有沒有東西在聽」，
    /// 用來區分「服務沒開」vs「服務開了但 API 壞了」，多站佈署排錯用。
    /// </summary>
    [RelayCommand]
    private async Task CheckPortAsync()
    {
        var url = NormalizeUrl(ServerUrl);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            TestResult = "❌ 位址格式不對，無法測埠：請輸入如 http://192.168.1.50:5030";
            return;
        }

        IsTesting = true;
        TestResult = $"TCP 探測中… {uri.Host}:{uri.Port}";
        try
        {
            // ⚠ 不可以只做 ConnectAsync(host, port) 就下結論。
            // 主機名可能同時解析到 IPv6 與 IPv4（Windows 的 localhost = ::1 + 127.0.0.1，且 ::1 排前面）。
            // 對方若只綁 IPv4，.NET 會先試 ::1 卡住約 2 秒才退回 —— 剛好吃掉這裡 2 秒的預算，
            // 於是按鈕回報「埠關閉/不可達 → 去查防火牆」，但埠其實是通的。
            // 這比無聲失敗更糟：它給的是明確而錯誤的指示（2026-08-24 UI 實測）。
            // 對策：把解析出來的位址**逐一**試，並講出是哪一個位址通的。
            var sw = Stopwatch.StartNew();
            var addresses = await ResolveAsync(uri.Host).ConfigureAwait(true);
            var failures = new List<string>();
            string? okAddr = null;
            long okMs = 0;

            foreach (var addr in addresses)
            {
                var one = Stopwatch.StartNew();
                using var tcp = new System.Net.Sockets.TcpClient();
                var connectTask = tcp.ConnectAsync(addr, uri.Port);
                var done = await Task.WhenAny(connectTask, Task.Delay(2000)).ConfigureAwait(true);
                one.Stop();
                if (done == connectTask && tcp.Connected)
                {
                    okAddr = addr.ToString();
                    okMs = one.ElapsedMilliseconds;
                    break;
                }
                if (done != connectTask) _ = connectTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                failures.Add($"{addr}（{one.ElapsedMilliseconds}ms）");
            }
            sw.Stop();

            if (okAddr is not null)
            {
                var others = failures.Count > 0
                    ? $"\n   ⚠ 但 {string.Join("、", failures)} 不通——對方可能只綁了其中一種 IP 版本。" +
                      "\n   　 若送檢偶爾很慢，把位址改用**能通的那個 IP** 可以省掉先試不通那邊的等待。"
                    : "";
                TestResult = $"✅ 埠開啟：{uri.Host}:{uri.Port} 有服務在聽（{okAddr}，TCP {okMs}ms）。\n" +
                             "   若 API 仍連不上，問題在服務本身（HTTP 層）而非網路/防火牆。" + others;
            }
            else
            {
                TestResult = $"❌ 埠關閉/不可達：{uri.Host}:{uri.Port}（試過 {string.Join("、", failures)}）。\n" +
                             "   → 該機器沒開 server、埠號錯，或防火牆未放行 —— 先解這個再談 API。";
            }
        }
        catch (Exception ex)
        {
            TestResult = $"❌ 埠探測失敗：{ex.Message}（host 名稱可能解析不了）";
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>把 host 解析成要逐一嘗試的位址；本身就是 IP 就直接用。解析不到回空。</summary>
    private static async Task<System.Net.IPAddress[]> ResolveAsync(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var literal)) return new[] { literal };
        try { return await System.Net.Dns.GetHostAddressesAsync(host).ConfigureAwait(false); }
        catch { return Array.Empty<System.Net.IPAddress>(); }
    }

    /// <summary>trim + 去尾斜線；缺 scheme 自動補 http://；非法回 null。</summary>
    private static string? NormalizeUrl(string? input)
    {
        var s = (input ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return null;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "http://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
            ? s : null;
    }
}

/// <summary>模型清單一列（<see cref="Display"/> 供下拉顯示：版本/大小/發布時刻/標記）。</summary>
public sealed record RemoteModelRow(ModelListEntryDto Entry, string Display)
{
    public override string ToString() => Display;
}

/// <summary>模型用途選項（顯示名 + task 代號）。</summary>
public sealed record ModelTaskOption(string Display, string Task)
{
    public override string ToString() => Display;
}
