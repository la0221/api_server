using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIVision.MoldCode.Onnx;
using OpenCvSharp;

namespace AIVision.MoldCode.Harness;

/// <summary>
/// Route A（站端前處理下放）端到端煙測——驗證「主程式整併版」走得通。
/// <para>
/// 走的是與站端 App 完全相同的程式碼路徑：
/// <c>WarpPolarPreprocessor.Preprocess</c>（找圓→裁圓→極座標展開→640 白底）→ PNG → 送
/// <c>POST /api/infer/ocr_crnn</c>（multipart，同 CrnnInferClient 契約）→ 比對讀值與檔名正解。
/// </para>
/// <para>
/// 用法：<c>Harness routea [imagesDir] [apiBase] [maxCount]</c><br/>
/// 例：<c>Harness routea "D:\新增資料夾\父子節點POC\dist\sample_images" http://localhost:5030 10</c>
/// </para>
/// <para>對照基準（POC python 版 2026-08-14 實測）：讀值 30/30、傳輸量 −68.6%、e2e p50 83.7ms。</para>
/// </summary>
public static class RouteASmokeTest
{
    private static readonly Regex ExpectPattern = new(@"(M\d+)-(\d+)", RegexOptions.Compiled);

    public static async Task<int> Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var imagesDir = args.Length > 1 ? args[1] : @"D:\新增資料夾\父子節點POC\dist\sample_images";
        var apiBase = (args.Length > 2 ? args[2] : "http://localhost:5030").TrimEnd('/');
        var maxCount = args.Length > 3 && int.TryParse(args[3], out var n) ? n : 10;
        // strip=完整前處理下放（需 server 支援 is_strip）／crop=只裁圓（server 照舊）／raw=原圖直送（對照組）
        var mode = (args.Length > 4 ? args[4] : "strip").Trim().ToLowerInvariant();

        if (!Directory.Exists(imagesDir))
        {
            Console.WriteLine($"[FAIL] 找不到影像資料夾：{imagesDir}");
            return 2;
        }

        var pars = new WarpPolarParams();
        Console.WriteLine("=== Route A 端到端煙測（站端前處理下放 → 中央推論）===");
        Console.WriteLine($"影像來源 : {imagesDir}");
        Console.WriteLine($"中央推論 : {apiBase}");
        Console.WriteLine($"前處理   : RInner={pars.RInner} Imgsz={pars.Imgsz} PadValue={pars.PadValue} " +
                          $"MinRadius={pars.HoughMinRadius}   (POC python: 0.6 / 640 / 255 / 200)");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // 先健檢，讓「冷啟」與「服務沒開」兩種情況能分辨。
        try
        {
            var h = await http.GetStringAsync($"{apiBase}/api/infer/ocr_crnn/health");
            Console.WriteLine($"健檢 : {h}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] 健檢失敗（推論服務沒起？）：{ex.Message}");
            return 3;
        }
        Console.WriteLine();

        var files = Directory.EnumerateFiles(imagesDir, "*.*")
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();

        Console.WriteLine($"{"#",-3} {"影像",-42} {"期望",-9} {"讀值",-9} {"原圖KB",7} {"送出KB",7} {"縮減",6} {"ms",8}  結果");
        Console.WriteLine(new string('-', 116));

        int ok = 0, mismatch = 0, edge = 0, noCircle = 0, err = 0;
        long rawTotal = 0, sentTotal = 0;
        var latencies = new List<double>();

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var name = Path.GetFileName(file);
            var rawBytes = await File.ReadAllBytesAsync(file);
            rawTotal += rawBytes.Length;

            // ── 前處理（依 mode）
            byte[] sent;
            string dim;
            using (var bgr = Cv2.ImDecode(rawBytes, ImreadModes.Color))
            {
                if (bgr.Empty()) { Console.WriteLine($"{i + 1,-3} {name,-42} 解碼失敗"); err++; continue; }

                if (mode == "raw")
                {
                    sent = rawBytes; dim = "原圖直送";
                }
                else if (mode == "crop")
                {
                    // 站端只做「找圓＋裁到工件」，展開交給 server（server 仍找得到圓 → 不必改 python）
                    var cropped = CropToCircle(bgr, pars);
                    if (cropped is null) { sent = rawBytes; dim = "無圓→送原圖"; noCircle++; }
                    else
                    {
                        using (cropped) { Cv2.ImEncode(".png", cropped, out sent); dim = $"{cropped.Width}x{cropped.Height}"; }
                    }
                }
                else // strip（完整前處理下放；需 server 支援 is_strip）
                {
                    using var strip = WarpPolarPreprocessor.Preprocess(bgr, 0.0, pars);
                    if (strip is null) { sent = rawBytes; dim = "無圓→送原圖"; noCircle++; }
                    else { Cv2.ImEncode(".png", strip, out sent); dim = $"{strip.Width}x{strip.Height}"; }
                }
            }
            sentTotal += sent.Length;

            // ── 送中央推論（multipart，同 CrnnInferClient 契約）
            var sw = Stopwatch.StartNew();
            string reading;
            try
            {
                using var form = new MultipartFormDataContent();
                var content = new ByteArrayContent(sent);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
                form.Add(content, "image", "frame.png");
                form.Add(new StringContent("png"), "format");
                form.Add(new StringContent("SMOKE"), "stationId");
                // strip 模式＝站端已完成前處理，告訴父端「只做辨識」
                if (mode == "strip" && dim != "無圓→送原圖")
                    form.Add(new StringContent("true"), "isStrip");

                using var resp = await http.PostAsync($"{apiBase}/api/infer/ocr_crnn", form);
                if (!resp.IsSuccessStatusCode)
                {
                    reading = $"HTTP {(int)resp.StatusCode}"; err++;
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var mohao = Get(root, "mohao");
                    var xuehao = Get(root, "xuehao");
                    reading = string.IsNullOrWhiteSpace(mohao) ? "(無讀值)" : $"{mohao}/{xuehao}";
                    // 第一張無讀值時把完整回應印出來，才查得到原因（例如 server 又做了一次前處理）
                    if (string.IsNullOrWhiteSpace(mohao) && i == 0)
                        Console.WriteLine($"    ↳ server 完整回應：{body}");
                }
            }
            catch (Exception ex)
            {
                reading = "ERR:" + ex.Message[..Math.Min(28, ex.Message.Length)];
                err++;
            }
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);

            // ── 比對正解（檔名 M101-01 → M101/01）
            var m = ExpectPattern.Match(name);
            var expect = m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : "?";
            string mark;
            if (name.StartsWith("exp_", StringComparison.OrdinalIgnoreCase)) { mark = "(刻意異常樣本)"; edge++; }
            else if (expect == reading) { mark = "OK"; ok++; }
            else { mark = $"✗ 期望 {expect}"; mismatch++; }

            var cut = rawBytes.Length > 0 ? $"{(1 - (double)sent.Length / rawBytes.Length) * 100:F0}%" : "-";
            Console.WriteLine($"{i + 1,-3} {Trunc(name, 42),-42} {expect,-9} {reading,-9} " +
                              $"{rawBytes.Length / 1024.0,7:F0} {sent.Length / 1024.0,7:F0} {cut,6} " +
                              $"{sw.Elapsed.TotalMilliseconds,8:F0}  {mark} [{dim}]");
        }

        Console.WriteLine(new string('-', 116));
        Console.WriteLine($"讀值：正確 {ok} / 不符 {mismatch} / 刻意異常樣本 {edge} / 錯誤 {err}   找不到圓(退回原圖) {noCircle}");
        if (rawTotal > 0)
            Console.WriteLine($"傳輸量：{rawTotal / 1024.0:F0} KB → {sentTotal / 1024.0:F0} KB " +
                              $"（縮減 {(1 - (double)sentTotal / rawTotal) * 100:F1}%，POC 基準 −68.6%）");
        if (latencies.Count > 0)
        {
            var sorted = latencies.OrderBy(x => x).ToList();
            Console.WriteLine($"延遲：首張 {latencies[0]:F0} ms（含冷啟） 中位數 {sorted[sorted.Count / 2]:F0} ms " +
                              $"最大 {sorted[^1]:F0} ms");
        }

        var pass = mismatch == 0 && err == 0;
        Console.WriteLine();
        Console.WriteLine(pass ? "★ 通過：路線打通，讀值全數相符。" : "✗ 未通過：見上方不符/錯誤列。");
        return pass ? 0 : 1;

        static string Get(JsonElement root, string prop)
            => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

        /// <summary>只做「找圓＋裁到工件」（不展開）。server 端仍能找到圓，故不需改 python sidecar。</summary>
        static Mat? CropToCircle(Mat bgr, WarpPolarParams p)
        {
            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            var circle = MoldCodePreprocessor.LocateCircle(gray);
            if (circle is null) return null;

            var (cx, cy, r) = circle.Value;
            var pad = (int)Math.Round(r * 1.10);                 // 留一點邊，避免切到字環
            var x0 = Math.Max(0, (int)cx - pad);
            var y0 = Math.Max(0, (int)cy - pad);
            var x1 = Math.Min(bgr.Width, (int)cx + pad);
            var y1 = Math.Min(bgr.Height, (int)cy + pad);
            if (x1 - x0 < 10 || y1 - y0 < 10) return null;
            return new Mat(bgr, new Rect(x0, y0, x1 - x0, y1 - y0)).Clone();
        }
    }
}
