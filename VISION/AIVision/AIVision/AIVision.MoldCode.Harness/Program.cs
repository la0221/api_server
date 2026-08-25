using System.Diagnostics;
using AIVision.Application.MoldCode;
using AIVision.Domain.MoldCode;
using AIVision.MoldCode.Harness;
using AIVision.MoldCode.Onnx;
using Microsoft.Extensions.Options;

// ── Golden test 模式：Harness golden <mohaoOnnx> <xuehaoOnnx> <imagesRoot> <outJson> [maxPerCavity] ──
if (args.Length > 0 && args[0] == "golden")
    return GoldenDump.Run(args);

// ── 雙軸核對週期 demo：Harness paircycle <mohaoOnnx> <xuehaoOnnx> <datasetRoot> ──
if (args.Length > 0 && args[0] == "paircycle")
    return await PairCycleDemo.Run(args);

// ── Route A 端到端煙測：Harness routea [imagesDir] [apiBase] [maxCount] ──
//    站端前處理下放 → 中央推論，比對讀值與檔名正解（需 AIVision.Api 已啟動）
if (args.Length > 0 && args[0] == "routea")
    return await RouteASmokeTest.Run(args);

// ── 預設路徑(可由 args 覆寫:Program <modelPath> <imagesDir>)──
string modelPath = args.Length > 0
    ? args[0]
    : @"D:\Toro_Project\VISION\AIVision\AIVision\AIVision.MoldCode.Onnx\models\moldcode_v3_18cls.onnx";
string imagesDir = args.Length > 1
    ? args[1]
    : @"D:\Toro_Project\VISION\OpenCV_Vision\Project\6_4模號M101測試_含信心度log\M101";

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"model = {modelPath}");
Console.WriteLine($"images= {imagesDir}\n");

var onnxOpts = new MoldCodeOnnxOptions
{
    ModelPath = modelPath, Imgsz = 320, UseBlackhat = true, UseLocator = true, OuterFactor = 1.5
};
using var recognizer = new OnnxMoldCodeRecognizer(onnxOpts);
var classSet = onnxOpts.ClassNames.Select(c => $"{onnxOpts.CodePrefix}/{c}").ToList();

// ════════ MODE 1:單張混淆矩陣 + 端到端 ms(C# 端,對照 Python 98.84%)════════
Console.WriteLine("=== MODE 1: 單張辨識混淆矩陣(C# OnnxMoldCodeRecognizer)===");
int total = 0, correct = 0;
var confusion = new Dictionary<string, int>();
var times = new List<double>();
var sw = new Stopwatch();

foreach (var dir in Directory.GetDirectories(imagesDir).OrderBy(d => d, StringComparer.Ordinal))
{
    var cav = Path.GetFileName(dir);
    var truth = $"{onnxOpts.CodePrefix}/{cav}";
    foreach (var f in Directory.GetFiles(dir, "*.jpg"))
    {
        var img = ImageLoad.Load(f);
        sw.Restart();
        var obs = recognizer.Recognize(img, classSet);
        sw.Stop();
        times.Add(sw.Elapsed.TotalMilliseconds);

        total++;
        var pred = obs.HasCode ? obs.Code! : "(none)";
        bool ok = obs.HasCode && MarkingVerifier.NormalizeCode(pred) == MarkingVerifier.NormalizeCode(truth);
        if (ok) correct++;
        if (!ok)
        {
            var key = $"{truth} -> {pred}";
            confusion[key] = confusion.GetValueOrDefault(key) + 1;
        }
    }
}

double Pct(double p)
{
    var s = times.OrderBy(x => x).ToList();
    return s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)(s.Count * p))];
}

Console.WriteLine($"tested={total}  correct={correct}  accuracy={(total > 0 ? (double)correct / total : 0):P2}");
Console.WriteLine($"  (對照: classical NCC 6.4% / YOLO baseline 97.6% / Python 重訓 val 98.84%)");
Console.WriteLine($"端到端(前處理+ONNX,單張)  p50={Pct(0.5):F1}ms  p95={Pct(0.95):F1}ms  (CPU OnnxRuntime)");
if (confusion.Count > 0)
{
    Console.WriteLine("誤判:");
    foreach (var kv in confusion.OrderByDescending(k => k.Value))
        Console.WriteLine($"   {kv.Key}  x{kv.Value}");
}
else Console.WriteLine("誤判: 無");

// ════════ MODE 2:檢測週期 demo(多幀投票 + 三態 + 氣吹,FakePlc/假相機)════════
Console.WriteLine("\n=== MODE 2: 檢測週期 demo(多幀投票 → 三態決策 → IO)===");
var cycleOpts = Options.Create(new MoldCodeCycleOptions
{
    ClassSet = classSet,
    MixedAlarmConfThreshold = 0.85,
    MaxFrames = 7,
    TimeBudgetMs = 120,
    MinConsensusVotes = 3,
    MinConsensusMargin = 2,
});

async Task RunCycle(string label, string expected, string sourceCavity)
{
    var frames = Directory.GetFiles(Path.Combine(imagesDir, sourceCavity), "*.jpg").OrderBy(x => x).ToList();
    var plc = new FakePlcPort();
    var cam = new FolderBurstCamera(frames);
    var handler = new VerifyMoldCodeCycleCommandHandler(plc, cam, recognizer, cycleOpts);

    var r = await handler.Handle(new VerifyMoldCodeCycleCommand(expected), CancellationToken.None);
    string io = string.Join(",", plc.Writes.Select(w =>
        w.CaptureStart ? "CaptureStart" : w.AirBlow ? "AirBlow(氣吹)" : w.ResultOn ? "Result(放行)" : "?"));
    Console.WriteLine($"[{label}] 預期={expected} 料源={sourceCavity}");
    Console.WriteLine($"   → {r.Outcome}  讀={r.ReadCode}  conf={r.Confidence:F2}  幀={r.Frames} 票={r.WinnerVotes}  " +
                      $"氣吹={(r.AirBlown ? "是" : "否")}  {r.ElapsedMs}ms");
    Console.WriteLine($"   IO: [{io}]");
    Console.WriteLine($"   理由: {r.Reason}");
}

await RunCycle("相符", "M101/14", "14");      // 預期=料源 → Match,放行
await RunCycle("混料", "M101/14", "03");      // 預期14 但來03 → 高信心讀到03 → MixedAlarm 氣吹

Console.WriteLine("\nDONE.");
return 0;
