using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIVision.Infrastructure.MoldCode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 「模型發布」視窗（**工程師以上**；選單可見性由 Shell 的 IsEngineerOrAbove 把關）。
/// 流程：選**用途**（OCR/公母模/瑕疵）→ 挑訓練產出的 .onnx（用途決定要幾顆）→ 填版本號 →
/// 上傳到 server（<c>POST /api/models/{task}</c>：server 端對版檔案組成、算 md5、原子落地、寫 _publish.json 溯源）。
/// <para>發完的建議動線：批量頁「指定版本」隔離試模驗收 → 通過才讓各 edge 下載採用。</para>
/// <para>取代 PowerShell 腳本 <c>publish_pair_model.ps1</c> 的 UI 形式；走 HTTP＝跨機也能發布。</para>
/// </summary>
public partial class ModelPublishViewModel : ObservableObject
{
    private readonly ModelHubClient _modelHub;
    private readonly InferenceServerOptions _serverOptions;
    private readonly ILogger<ModelPublishViewModel>? _logger;

    public ModelPublishViewModel(
        ModelHubClient modelHub,
        IOptions<InferenceServerOptions> serverOptions,
        ILogger<ModelPublishViewModel>? logger = null)
    {
        _modelHub = modelHub;
        _serverOptions = serverOptions.Value;
        _logger = logger;
        ServerBaseUrl = _serverOptions.BaseUrl;
        SelectedTask = TaskOptions[0];
    }

    /// <summary>用途選項（決定要選幾顆檔案）。</summary>
    public ObservableCollection<ModelTaskOption> TaskOptions { get; } = new()
    {
        new("模號穴號 OCR（雙 head）", "ocr_pair"),
        new("模號穴號 OCR（CRNN 字元式）", "ocr_crnn"),
        new("公母模", "gongmu"),
        new("瑕疵檢查", "defect"),
    };

    /// <summary>用途 → 需要的檔案組成（目標檔名；上傳時自動用這些名字，來源檔名隨意）。
    /// ⚠ CRNN 收的是 .pt 原檔（sidecar 直接跑 torch）——目標副檔名決定內容檢查與是否轉檔。</summary>
    private static string[] FilesForTask(string task) => task switch
    {
        "ocr_pair" => new[] { "mohao.onnx", "xuehao.onnx" },
        "ocr_crnn" => new[] { "detector.pt", "nonar.pt" },
        _ => new[] { "model.onnx" },
    };

    [ObservableProperty]
    private ModelTaskOption? selectedTask;

    partial void OnSelectedTaskChanged(ModelTaskOption? value)
    {
        FileSlots.Clear();
        if (value is null) return;
        foreach (var name in FilesForTask(value.Task))
            FileSlots.Add(new PublishFileSlot(name));
    }

    /// <summary>要上傳的檔案欄位（用途決定數量：OCR 兩顆、其餘一顆）。</summary>
    public ObservableCollection<PublishFileSlot> FileSlots { get; } = new();

    [ObservableProperty]
    private string? version;

    [ObservableProperty]
    private string? sourceNote;

    /// <summary>選填：模號複檢/判定信心門檻（0~1；隨模型版控進 _publish.json judge 段）。</summary>
    [ObservableProperty]
    private string? judgeConfMohao;

    /// <summary>選填：穴號複檢/判定信心門檻。</summary>
    [ObservableProperty]
    private string? judgeConfXuehao;

    /// <summary>選填（進階）：前處理參數 JSON（鍵=WarpPolarParams 欄位；隨模型版控進 preprocess 段）。</summary>
    [ObservableProperty]
    private string? preprocessJson;

    [ObservableProperty]
    private string? serverBaseUrl;

    [ObservableProperty]
    private string? statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool isBusy;

    public bool NotBusy => !IsBusy;

    /// <summary>幫某個檔案欄位挑本機 .onnx。</summary>
    [RelayCommand]
    private void Browse(PublishFileSlot? slot)
    {
        if (slot is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"選擇 {slot.TargetName} 的來源模型檔（.onnx 或 .pt——.pt 會於發布時自動轉檔）",
            Filter = "模型檔 (*.onnx;*.pt)|*.onnx;*.pt|ONNX (*.onnx)|*.onnx|PyTorch (*.pt)|*.pt|所有檔案 (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            slot.SourcePath = dlg.FileName;
    }

    /// <summary>發布：上傳到 server 登錄夾（server 端算 md5、原子落地、寫溯源）。</summary>
    [RelayCommand]
    private async Task PublishAsync()
    {
        var task = SelectedTask?.Task;
        var version = (Version ?? "").Trim();
        if (string.IsNullOrWhiteSpace(task))
        {
            StatusText = "請先選擇用途。";
            return;
        }
        if (version.Length == 0)
        {
            StatusText = "請填版本號（如 v6.8）。";
            return;
        }
        var missing = FileSlots.Where(s => string.IsNullOrWhiteSpace(s.SourcePath) || !File.Exists(s.SourcePath)).ToList();
        if (missing.Count > 0)
        {
            StatusText = $"請先選好檔案：{string.Join("、", missing.Select(s => s.TargetName))}（檔案必須存在）。";
            return;
        }

        // 內容規則依「目標副檔名」分流：
        //   目標 .onnx＋來源是 .pt → 自動轉檔（export_pt_to_onnx.py）再上傳（2026-07-31 實案）。
        //   目標 .pt（CRNN）→ 不轉檔，但來源必須真的是 torch zip 容器，選錯（如 onnx）就擋。
        foreach (var s in FileSlots)
        {
            bool srcIsPt = LooksLikePt(s.SourcePath!);
            if (s.TargetName.EndsWith(".pt", StringComparison.OrdinalIgnoreCase))
            {
                if (!srcIsPt)
                {
                    StatusText = $"❌ {s.TargetName}：此用途要的是 .pt 訓練原檔（torch 權重），選到的檔案不是——勿轉檔、直接選 best.pt。";
                    return;
                }
                continue;
            }
            if (!srcIsPt) continue;

            IsBusy = true;
            try
            {
                var onnx = await ConvertPtToOnnxAsync(s);
                if (onnx is null) return;   // 失敗原因已寫進 StatusText
                s.SourcePath = onnx;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ②判定門檻（選填）：兩欄有填才組 judge JSON；格式錯在本地就擋。
        string? judgeJson = null;
        {
            var parts = new List<string>();
            foreach (var (label, key, raw) in new[]
                     { ("模號門檻", "confMohao", JudgeConfMohao), ("穴號門檻", "confXuehao", JudgeConfXuehao) })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!double.TryParse(raw.Trim(), out var v) || v is < 0 or > 1)
                {
                    StatusText = $"❌ {label} 需為 0~1 的數字（現值 '{raw}'）。";
                    return;
                }
                parts.Add($"\"{key}\":{v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (parts.Count > 0) judgeJson = "{" + string.Join(",", parts) + "}";
        }

        IsBusy = true;
        StatusText = $"上傳中：{task}/{version} → {_serverOptions.BaseUrl} …";
        try
        {
            var files = FileSlots.Select(s => (s.TargetName, s.SourcePath!)).ToList();
            var sw = Stopwatch.StartNew();
            var result = await _modelHub.PublishAsync(task!, version, files, SourceNote,
                judgeJson: judgeJson,
                preprocessJson: string.IsNullOrWhiteSpace(PreprocessJson) ? null : PreprocessJson!.Trim());
            sw.Stop();

            if (!result.Success)
            {
                StatusText = result.StatusCode == 409
                    ? $"❌ {result.Error}\n   （版本不可變：內容有改就換新版本號，如 {version}b）"
                    : $"❌ {result.Error}";
                return;
            }

            var md5Lines = string.Join("\n", (result.Entry?.Files ?? new()).Select(
                f => $"   {f.Name}  md5={f.Md5}"));
            StatusText =
                $"✅ 已發布 {task}/{version}（{sw.ElapsedMilliseconds}ms）\n{md5Lines}\n" +
                (task == "ocr_pair"
                    ? "下一步（建議）：到雙head頁 → 來源=中央伺服器 → 查伺服器版本 → 指定此版本跑整批＝隔離試模驗收；" +
                      "通過後各 edge 才用「API 伺服器設定 → 下載到本地」採用。"
                    : "此用途的推論端點尚未開通（模型接入後提供）；目前已可列版本/下載同步。");
            _logger?.LogInformation("[ModelPublish] 已發布 {Task}/{Version}", task, version);
        }
        catch (Exception ex)
        {
            StatusText = $"❌ 發布例外：{ex.Message}";
            _logger?.LogWarning(ex, "[ModelPublish] 發布失敗: {Task}/{Version}", task, version);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>轉檔工具（與命令列同一支：md5/類別/imgsz 對齊規則都在裡面，勿另寫一份）。</summary>
    private const string ExportScriptPath = @"D:\AIVisionModels\export_pt_to_onnx.py";

    /// <summary>檔案是否為 .pt（PyTorch zip 容器，"PK" 開頭）。讀不到一律當不是，交後續流程報錯。</summary>
    private static bool LooksLikePt(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var magic = new byte[2];
            return fs.Read(magic, 0, 2) == 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 就地把 .pt 轉成 .onnx（呼叫 <see cref="ExportScriptPath"/>；產出落在 .pt 同資料夾）。
    /// 成功回 .onnx 路徑；失敗回 null 並把原因（含 python 輸出尾段）寫進 <see cref="StatusText"/>。
    /// </summary>
    private async Task<string?> ConvertPtToOnnxAsync(PublishFileSlot slot)
    {
        var pt = slot.SourcePath!;
        var expected = Path.ChangeExtension(pt, ".onnx");
        StatusText = $"偵測到 {slot.TargetName} 是 .pt → 自動轉檔中（首次約 10-30 秒，產出落在同資料夾）…";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"-X utf8 \"{ExportScriptPath}\" \"{pt}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                StatusText = "❌ 無法啟動 python（自動轉檔需要裝有 python + ultralytics 的環境）。";
                return null;
            }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0 || !File.Exists(expected) || LooksLikePt(expected))
            {
                var tail = string.Join("\n", (stderr + "\n" + stdout)
                    .Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(4));
                StatusText =
                    $"❌ {slot.TargetName} 轉檔失敗（exit={proc.ExitCode}）：\n{tail}\n" +
                    $"   可手動重試：python {ExportScriptPath} \"{pt}\"";
                return null;
            }

            _logger?.LogInformation("[ModelPublish] 已自動轉檔 {Pt} → {Onnx}", pt, expected);
            StatusText = $"✅ {slot.TargetName} 已轉檔：{expected}，繼續上傳…";
            return expected;
        }
        catch (Exception ex)
        {
            StatusText = $"❌ {slot.TargetName} 轉檔例外：{ex.Message}（本機需要 python + ultralytics 環境）";
            _logger?.LogWarning(ex, "[ModelPublish] .pt 轉檔失敗: {Pt}", pt);
            return null;
        }
    }
}

/// <summary>發布頁的單一檔案欄位（目標檔名固定，來源路徑由使用者挑）。</summary>
public partial class PublishFileSlot : ObservableObject
{
    public PublishFileSlot(string targetName) => TargetName = targetName;

    /// <summary>上傳後在登錄夾中的檔名（用途組成決定，如 mohao.onnx）。</summary>
    public string TargetName { get; }

    [ObservableProperty]
    private string? sourcePath;
}
