using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.MoldCode;
using AIVision.Application.Ports.MoldCode;
using AIVision.MoldCode.Onnx;
using Microsoft.Extensions.Options;

namespace AIVision.MoldCode.Harness;

/// <summary>
/// 雙軸（warpPolar V6.7.1）核對週期 demo：用 FakePlc + FolderBurstCamera 跑
/// VerifyMoldCodePairCycleCommandHandler，驗證 相符放行 / 模號混料氣吹 / 穴號混料氣吹 的完整熱迴圈邏輯。
///
/// 用法：Harness paircycle &lt;mohaoOnnx&gt; &lt;xuehaoOnnx&gt; &lt;datasetRoot&gt;
/// </summary>
internal static class PairCycleDemo
{
    public static async Task<int> Run(string[] args)
    {
        string mohaoOnnx = args.Length > 1 ? args[1] : @"D:\AIVisionModels\v671\mohao.onnx";
        string xuehaoOnnx = args.Length > 2 ? args[2] : @"D:\AIVisionModels\v671\xuehao.onnx";
        string root = args.Length > 3 ? args[3] : @"D:\Toro_Project\VISION\AIVision\隱眼專案";

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== MODE 3: 雙軸核對週期 demo（warpPolar V6.7.1）===");
        Console.WriteLine($"mohao = {mohaoOnnx}");
        Console.WriteLine($"xuehao= {xuehaoOnnx}\n");

        using var rec = new WarpPolarTwoHeadRecognizer(mohaoOnnx, xuehaoOnnx, new WarpPolarParams(), passes: 2);
        IMoldCodePairRecognizerPort port = rec;

        var opts = Options.Create(new MoldCodePairCycleOptions
        {
            MoldThreshold = 0.60,
            CavityThreshold = 0.85,
            NgClassName = "NG",
            MaxFrames = 5,
            TimeBudgetMs = 2000,
            MinConsensusVotes = 3,
            MinConsensusMargin = 2,
        });

        async Task RunCycle(string label, string expM, string expX, string srcMold, string srcCav)
        {
            var dir = Path.Combine(root, srcMold, srcCav);
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"[{label}] 略過：找不到料源資料夾 {dir}\n");
                return;
            }
            var frames = Directory.GetFiles(dir, "*.jpg").OrderBy(x => x, StringComparer.Ordinal).ToList();
            var plc = new FakePlcPort();
            var cam = new FolderBurstCamera(frames);
            var handler = new VerifyMoldCodePairCycleCommandHandler(plc, cam, port, opts);

            var r = await handler.Handle(new VerifyMoldCodePairCycleCommand(expM, expX), CancellationToken.None);
            string io = string.Join(",", plc.Writes.Select(w =>
                w.CaptureStart ? "CaptureStart" : w.AirBlow ? "AirBlow(氣吹)" : w.ResultOn ? "Result(放行)" : "?"));

            Console.WriteLine($"[{label}] 預期={expM}/{expX} 料源={srcMold}/{srcCav}");
            Console.WriteLine($"   → {r.Outcome}  讀={r.ReadMohao}/{r.ReadXuehao}  " +
                              $"conf={r.ConfMohao:F2}/{r.ConfXuehao:F2}  幀={r.Frames} 票={r.WinnerVotes}  " +
                              $"氣吹={(r.AirBlown ? "是" : "否")}  {r.ElapsedMs}ms");
            Console.WriteLine($"   IO: [{io}]");
            Console.WriteLine($"   理由: {r.Reason}\n");
        }

        await RunCycle("相符", "M101", "08", "M101", "08");        // → Match，放行
        await RunCycle("模號混料", "M101", "08", "M60", "08");     // → 模號高信心不符 → MixedAlarm 氣吹
        await RunCycle("穴號混料", "M101", "08", "M101", "03");    // → 穴號高信心不符 → MixedAlarm 氣吹

        Console.WriteLine("（NG→Reject 路徑由單元測試 MoldCodePairVerifierTests 涵蓋；資料集無 NG 樣本）");
        Console.WriteLine("\nDONE.");
        return 0;
    }
}
