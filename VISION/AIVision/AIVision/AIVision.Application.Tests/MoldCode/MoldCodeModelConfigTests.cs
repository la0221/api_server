using System;
using System.IO;
using AIVision.MoldCode.Onnx;

namespace AIVision.Application.Tests.MoldCode;

/// <summary>
/// 驗證模型旁置設定 <see cref="MoldCodeModelConfig"/> 的讀寫往返(round-trip),
/// 以及「明確參數 > 旁置設定 > baseline」的合併優先序(以隔離的合併輔助函式驗證,
/// 不需載入真實 ONNX 模型;<see cref="SwitchableMoldCodeRecognizer.LoadModel"/> 內採用相同規則)。
/// </summary>
public class MoldCodeModelConfigTests
{
    /// <summary>建立臨時 .onnx 假路徑(不需真實檔案,旁置檔以其推得)。</summary>
    private static string NewTempModelPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "moldcode-cfg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "model.onnx");
    }

    [Fact]
    public void GetConfigPath_DerivesSidecarFromModelPath()
    {
        var modelPath = Path.Combine("X:", "models", "foo.onnx");

        var configPath = MoldCodeModelConfig.GetConfigPath(modelPath);

        Assert.Equal(Path.Combine("X:", "models", "foo.config.json"), configPath);
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        var modelPath = NewTempModelPath(); // 旁置檔不存在

        var result = MoldCodeModelConfig.TryRead(modelPath);

        Assert.Null(result);
    }

    [Fact]
    public void TryRead_CorruptJson_ReturnsNullDoesNotThrow()
    {
        var modelPath = NewTempModelPath();
        File.WriteAllText(MoldCodeModelConfig.GetConfigPath(modelPath), "{ this is not valid json ");

        var result = MoldCodeModelConfig.TryRead(modelPath);

        Assert.Null(result);
    }

    [Fact]
    public void WriteThenTryRead_RoundTrips_AllFields()
    {
        var modelPath = NewTempModelPath();
        var cfg = new MoldCodeModelConfig
        {
            DisplayName = "我的模型 v9",
            Description = "準確率 99.9%",
            CodePrefix = "M222",
            Imgsz = 480,
            UseBlackhat = false,
            UseLocator = false,
            OuterFactor = 2.0
        };

        MoldCodeModelConfig.Write(modelPath, cfg);
        var read = MoldCodeModelConfig.TryRead(modelPath);

        Assert.NotNull(read);
        Assert.Equal("我的模型 v9", read!.DisplayName);
        Assert.Equal("準確率 99.9%", read.Description);
        Assert.Equal("M222", read.CodePrefix);
        Assert.Equal(480, read.Imgsz);
        Assert.False(read.UseBlackhat);
        Assert.False(read.UseLocator);
        Assert.Equal(2.0, read.OuterFactor);
    }

    [Fact]
    public void WriteThenTryRead_OptionalFieldsNull_StayNull()
    {
        var modelPath = NewTempModelPath();
        // 只設 DisplayName,其餘留 null(代表「未設定 → 退回 baseline」)。
        var cfg = new MoldCodeModelConfig { DisplayName = "只改名" };

        MoldCodeModelConfig.Write(modelPath, cfg);
        var read = MoldCodeModelConfig.TryRead(modelPath);

        Assert.NotNull(read);
        Assert.Equal("只改名", read!.DisplayName);
        Assert.Null(read.Imgsz);
        Assert.Null(read.UseBlackhat);
        Assert.Null(read.UseLocator);
        Assert.Null(read.OuterFactor);
        Assert.Null(read.CodePrefix);
    }

    // ===== 合併優先序:明確參數 > 旁置設定 > baseline =====
    // 以與 SwitchableMoldCodeRecognizer.LoadModel 相同的規則於此隔離驗證
    // (不需載入真實 ONNX session)。

    private static MoldCodeOnnxOptions Baseline() => new()
    {
        CodePrefix = "M101",
        Imgsz = 320,
        UseBlackhat = true,
        UseLocator = true,
        OuterFactor = 1.5
    };

    private static (string prefix, int imgsz, bool blackhat, bool locator, double outer) Merge(
        string? methodPrefix, MoldCodeModelConfig? sidecar, MoldCodeOnnxOptions baseline)
    {
        var prefix = !string.IsNullOrWhiteSpace(methodPrefix)
            ? methodPrefix!
            : (!string.IsNullOrWhiteSpace(sidecar?.CodePrefix) ? sidecar!.CodePrefix! : baseline.CodePrefix);
        var imgsz = sidecar?.Imgsz ?? baseline.Imgsz;
        var blackhat = sidecar?.UseBlackhat ?? baseline.UseBlackhat;
        var locator = sidecar?.UseLocator ?? baseline.UseLocator;
        var outer = sidecar?.OuterFactor ?? baseline.OuterFactor;
        return (prefix, imgsz, blackhat, locator, outer);
    }

    [Fact]
    public void Merge_NoSidecar_UsesBaseline()
    {
        var b = Baseline();

        var m = Merge(methodPrefix: null, sidecar: null, baseline: b);

        Assert.Equal("M101", m.prefix);
        Assert.Equal(320, m.imgsz);
        Assert.True(m.blackhat);
        Assert.True(m.locator);
        Assert.Equal(1.5, m.outer);
    }

    [Fact]
    public void Merge_Sidecar_OverridesBaseline()
    {
        var b = Baseline();
        var sidecar = new MoldCodeModelConfig
        {
            CodePrefix = "M999",
            Imgsz = 640,
            UseBlackhat = false,
            UseLocator = false,
            OuterFactor = 2.2
        };

        var m = Merge(methodPrefix: null, sidecar: sidecar, baseline: b);

        Assert.Equal("M999", m.prefix);
        Assert.Equal(640, m.imgsz);
        Assert.False(m.blackhat);
        Assert.False(m.locator);
        Assert.Equal(2.2, m.outer);
    }

    [Fact]
    public void Merge_ExplicitPrefix_BeatsSidecar()
    {
        var b = Baseline();
        var sidecar = new MoldCodeModelConfig { CodePrefix = "M999", Imgsz = 640 };

        var m = Merge(methodPrefix: "M555", sidecar: sidecar, baseline: b);

        // 前綴採明確參數;其餘無明確參數欄位仍取旁置設定。
        Assert.Equal("M555", m.prefix);
        Assert.Equal(640, m.imgsz);
    }

    [Fact]
    public void Merge_PartialSidecar_FallsBackPerField()
    {
        var b = Baseline();
        // 旁置只設 Imgsz 與 UseLocator,其餘退回 baseline。
        var sidecar = new MoldCodeModelConfig { Imgsz = 416, UseLocator = false };

        var m = Merge(methodPrefix: null, sidecar: sidecar, baseline: b);

        Assert.Equal("M101", m.prefix);     // baseline
        Assert.Equal(416, m.imgsz);         // sidecar
        Assert.True(m.blackhat);            // baseline
        Assert.False(m.locator);            // sidecar
        Assert.Equal(1.5, m.outer);         // baseline
    }
}
