using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AIVision.EdgeSimulator;

/// <summary>
/// Edge 模擬器：模擬一台上位機用純 HTTP 接中央推論 server。
/// 刻意零依賴（不引用任何 AIVision 專案）——它能跑通，就證明任何第三方 edge 都能照契約接上。
/// 動作規則（門檻 0.60/0.85）僅為「edge 依 JSON 做設定動作」的示意，非生產決策。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private CancellationTokenSource? _cts;

    public MainWindow() => InitializeComponent();

    private string BaseUrl => TxtServer.Text.Trim().TrimEnd('/');
    private string StationId => TxtStation.Text.Trim();

    // ===== 健康檢查 =====
    private async void BtnHealth_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        TxtStatus.Text = $"健康檢查中… {BaseUrl}";
        try
        {
            var sw = Stopwatch.StartNew();
            using var resp = await Http.GetAsync($"{BaseUrl}/api/infer/health");
            var body = await resp.Content.ReadAsStringAsync();
            sw.Stop();

            TxtJson.Text = Pretty(body);
            TxtStatus.Text = $"健康檢查：HTTP {(int)resp.StatusCode}｜{sw.ElapsedMilliseconds}ms";
            TxtSummary.Text = resp.IsSuccessStatusCode
                ? "server 可達。看右側 JSON 的 status/modelLoaded/modelVersion。"
                : "server 回非 2xx。";
            TxtAction.Text = "—";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"❌ 連不上：{ex.Message}";
            TxtJson.Text = "";
            TxtSummary.Text = "連不上 server —— edge 此時應 fail-closed（預設剔除）或降級本機。";
            TxtAction.Text = "⛔ fail-closed（示意）";
        }
        finally { SetBusy(false); }
    }

    // ===== 單張 =====
    private async void BtnSingle_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "選一張測試影像",
            Filter = "影像檔|*.jpg;*.jpeg;*.png;*.bmp",
        };
        if (dlg.ShowDialog() != true) return;

        SetBusy(true);
        TxtBatchStats.Text = "";
        try
        {
            await SendOneAsync(dlg.FileName, CancellationToken.None);
        }
        finally { SetBusy(false); }
    }

    // ===== 資料夾批量（線下模式）=====
    private async void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "選測試影像資料夾（含子資料夾）" };
        if (dlg.ShowDialog() != true) return;

        var files = Directory.EnumerateFiles(dlg.FolderName, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            TxtStatus.Text = "資料夾內沒有影像檔。";
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var wall = new List<double>();
        var server = new List<double>();
        int ok = 0, reads = 0;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                TxtStatus.Text = $"批量中…（{i + 1}/{files.Count}）{Path.GetFileName(files[i])}";
                var r = await SendOneAsync(files[i], _cts.Token);
                if (r.httpOk) { ok++; wall.Add(r.wallMs); if (r.serverMs > 0) server.Add(r.serverMs); }
                if (r.hasReading) reads++;
            }
            TxtStatus.Text = $"批量完成：{files.Count} 張。";
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = $"已停止（跑了 {wall.Count} 張）。";
        }
        finally
        {
            TxtBatchStats.Text =
                $"批量統計：張數={files.Count}　HTTP 成功={ok}　有讀值={reads}\n" +
                $"來回 p50={P(wall, .5):F0}ms p95={P(wall, .95):F0}ms｜" +
                $"server 推論 p50={P(server, .5):F0}ms p95={P(server, .95):F0}ms";
            _cts?.Dispose(); _cts = null;
            SetBusy(false);
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ===== 核心：送一張圖（jpg/bmp 先無損轉 PNG——契約禁 JPEG）=====
    private async Task<(bool httpOk, bool hasReading, double wallMs, int serverMs)> SendOneAsync(
        string file, CancellationToken ct)
    {
        try
        {
            var png = ToPng(File.ReadAllBytes(file), file);

            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(png);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(content, "image", "frame.png");
            form.Add(new StringContent("png"), "format");
            if (StationId.Length > 0)
                form.Add(new StringContent(StationId), "stationId");

            var sw = Stopwatch.StartNew();
            using var resp = await Http.PostAsync($"{BaseUrl}/api/infer/pair", form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            TxtJson.Text = Pretty(body);

            if (!resp.IsSuccessStatusCode)
            {
                TxtStatus.Text = $"HTTP {(int)resp.StatusCode}｜{sw.ElapsedMilliseconds}ms｜{Path.GetFileName(file)}";
                TxtSummary.Text = "請求被拒（看右側 ProblemDetails）——請求壞掉屬 4xx，非觀測。";
                TxtAction.Text = "⛔ fail-closed（示意）";
                return (false, false, sw.Elapsed.TotalMilliseconds, 0);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? mohao = root.TryGetProperty("mohao", out var m) ? m.GetString() : null;
            string? xuehao = root.TryGetProperty("xuehao", out var x) ? x.GetString() : null;
            double confM = root.TryGetProperty("confMohao", out var cm) ? cm.GetDouble() : 0;
            double confX = root.TryGetProperty("confXuehao", out var cx) ? cx.GetDouble() : 0;
            bool hasReading = root.TryGetProperty("hasReading", out var hr) && hr.GetBoolean();
            int serverMs = root.TryGetProperty("elapsedMs", out var el) ? el.GetInt32() : 0;
            string? ver = root.TryGetProperty("modelVersion", out var mv) ? mv.GetString() : null;
            string? echo = root.TryGetProperty("stationId", out var st) ? st.GetString() : null;
            string? reason = root.TryGetProperty("failureReason", out var fr) ? fr.GetString() : null;

            TxtStatus.Text = $"HTTP 200｜來回 {sw.ElapsedMilliseconds}ms｜server {serverMs}ms" +
                             $"｜版本 {ver}｜站點回聲 {echo ?? "—"}｜{Path.GetFileName(file)}";
            TxtSummary.Text = hasReading
                ? $"模號 {mohao}（{confM:F3}）\n穴號 {xuehao}（{confX:F3}）"
                : $"無讀值：{reason ?? "NO OBJECT"}（這是有效觀測，非故障）";

            // edge 動作規則示意：門檻同生產設定（模號 0.60 / 穴號 0.85）
            bool pass = hasReading && confM >= 0.60 && confX >= 0.85;
            TxtAction.Text = pass ? "✅ 放行（示意）" : "⛔ 剔除/複判（示意）";
            TxtAction.Foreground = pass
                ? System.Windows.Media.Brushes.LimeGreen
                : System.Windows.Media.Brushes.OrangeRed;

            return (true, hasReading, sw.Elapsed.TotalMilliseconds, serverMs);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TxtStatus.Text = $"❌ {Path.GetFileName(file)}：{ex.Message}";
            TxtSummary.Text = "傳輸失敗 —— edge 此時應 fail-closed 或降級本機。";
            TxtAction.Text = "⛔ fail-closed（示意）";
            return (false, false, 0, 0);
        }
    }

    /// <summary>任意格式（jpg/bmp/png）→ PNG bytes；已是 PNG 直接用（WPF 內建解編碼，零外部套件）。</summary>
    private static byte[] ToPng(byte[] raw, string fileName)
    {
        if (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return raw;
        using var input = new MemoryStream(raw);
        var frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(frame);
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJson);
        }
        catch { return json; }
    }

    private static double P(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(v => v).ToList();
        return s[Math.Min(s.Count - 1, (int)(s.Count * p))];
    }

    private void SetBusy(bool busy)
    {
        BtnHealth.IsEnabled = !busy;
        BtnSingle.IsEnabled = !busy;
        BtnFolder.IsEnabled = !busy;
        BtnStop.IsEnabled = busy;
    }
}
