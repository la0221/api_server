using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIVision.MoldCode.Onnx;

namespace AIVision.MoldCode.Harness;

/// <summary>
/// Golden test：用 C# <see cref="WarpPolarTwoHeadRecognizer"/> 對樣本影像逐張辨識，
/// 輸出 JSON（檔案路徑 → 模號/穴號/信心），供與 Python engine 結果逐張比對，
/// 驗證 C# 前處理 + 推論與訓練端 bit 對齊（不過此關不算整合完成）。
///
/// 用法：Harness golden &lt;mohaoOnnx&gt; &lt;xuehaoOnnx&gt; &lt;imagesRoot&gt; &lt;outJson&gt; [maxPerCavity]
/// </summary>
internal static class GoldenDump
{
    private sealed class Entry
    {
        public string mold_truth { get; set; } = "";
        public string cav_truth { get; set; } = "";
        public bool present { get; set; }
        public bool hough { get; set; }
        public string mohao { get; set; } = "";
        public double conf_m { get; set; }
        public string xuehao { get; set; } = "";
        public double conf_x { get; set; }
    }

    public static int Run(string[] args)
    {
        // args[0] == "golden"
        string mohaoOnnx = args.Length > 1 ? args[1] : @"G:\隱眼專案\weights\mohao\best.onnx";
        string xuehaoOnnx = args.Length > 2 ? args[2] : @"G:\隱眼專案\weights\xuehao\weights\best.onnx";
        string root = args.Length > 3 ? args[3] : @"D:\Toro_Project\VISION\AIVision\隱眼專案";
        string outJson = args.Length > 4 ? args[4] : @"D:\Toro_Project\VISION\AIVision\tools\golden-test\cs_golden.json";
        int maxPerCavity = args.Length > 5 && int.TryParse(args[5], out var mpc) ? mpc : 3;

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"[golden] mohao  = {mohaoOnnx}");
        Console.WriteLine($"[golden] xuehao = {xuehaoOnnx}");
        Console.WriteLine($"[golden] root   = {root}");
        Console.WriteLine($"[golden] out    = {outJson}");
        Console.WriteLine($"[golden] maxPerCavity = {maxPerCavity}\n");

        using var rec = new WarpPolarTwoHeadRecognizer(mohaoOnnx, xuehaoOnnx, new WarpPolarParams(), passes: 2);
        Console.WriteLine($"[golden] mohao classes ({rec.MohaoNames.Count}): {string.Join(",", rec.MohaoNames)}");
        Console.WriteLine($"[golden] xuehao classes ({rec.XuehaoNames.Count}): {string.Join(",", rec.XuehaoNames)}\n");

        var moldRe = new Regex(@"^M\d+$", RegexOptions.IgnoreCase);
        var cavRe = new Regex(@"^\d{2}$");
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        int total = 0, moldOk = 0, cavOk = 0, bothOk = 0, noLens = 0;

        foreach (var moldDir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var mold = Path.GetFileName(moldDir);
            if (!moldRe.IsMatch(mold))
                continue;

            foreach (var cavDir in Directory.GetDirectories(moldDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var cav = Path.GetFileName(cavDir);
                if (!cavRe.IsMatch(cav))
                    continue;

                var files = Directory.GetFiles(cavDir, "*.jpg")
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .Take(maxPerCavity);

                foreach (var f in files)
                {
                    var img = ImageLoad.Load(f);
                    var r = rec.Recognize(img);
                    result[f] = new Entry
                    {
                        mold_truth = mold,
                        cav_truth = cav,
                        present = r.Present,
                        hough = r.HoughUsed,
                        mohao = r.Mohao,
                        conf_m = Math.Round(r.ConfMohao, 6),
                        xuehao = r.Xuehao,
                        conf_x = Math.Round(r.ConfXuehao, 6),
                    };

                    total++;
                    if (!r.Present) { noLens++; continue; }
                    bool mOk = string.Equals(r.Mohao, mold, StringComparison.OrdinalIgnoreCase);
                    bool cOk = string.Equals(r.Xuehao, cav, StringComparison.OrdinalIgnoreCase);
                    if (mOk) moldOk++;
                    if (cOk) cavOk++;
                    if (mOk && cOk) bothOk++;
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outJson)!);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(outJson, json, new UTF8Encoding(false));

        string Pct(int n) => total > 0 ? ((double)n / total).ToString("P2", CultureInfo.InvariantCulture) : "0";
        Console.WriteLine($"[golden] tested={total}  no-lens={noLens}");
        Console.WriteLine($"[golden] mohao acc={Pct(moldOk)}  xuehao acc={Pct(cavOk)}  both acc={Pct(bothOk)}");
        Console.WriteLine($"[golden] wrote {outJson}");
        return 0;
    }
}
