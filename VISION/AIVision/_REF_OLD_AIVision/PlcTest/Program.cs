using AIVision.Domain.Plc;
using AIVision.Infrastructure.Devices.Plc.Communication;
using AIVision.Infrastructure.Devices.Plc.Handshake;
using AIVision.Infrastructure.Devices.Plc.SignalMapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

Console.WriteLine("=== PLC 通訊測試 (Phase 1 + 2 + 3) ===");
Console.WriteLine("請確認 Modbus Slave 已啟動於 127.0.0.1:502");
Console.WriteLine();
Console.WriteLine("Modbus Slave 設定:");
Console.WriteLine("  - Function: 01 Coils, Address: 0, Quantity: 10");
Console.WriteLine("  - Function: 01 Coils, Address: 10000, Quantity: 1");
Console.WriteLine();
Console.WriteLine("按 Enter 開始測試...");
Console.ReadLine();

// 建立 Logger
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// 建立連線設定
var connectionOptions = Options.Create(new PlcConnectionOptions
{
    Ip = "127.0.0.1",
    Port = 502,
    UnitId = 1,
    PollIntervalMs = 50,
    ReadTimeoutMs = 1000,
    WriteTimeoutMs = 1000
});

// 建立訊號映射設定
var signalOptions = Options.Create(PlcSignalMapOptions.CreateDefault());

// 建立交握設定
var handshakeOptions = Options.Create(new PlcHandshakeOptions
{
    TimeoutMs = 10000,
    MinTriggerIntervalMs = 200,
    AutoClearByPc = true,
    ErrorPolicy = "TreatAsNg"
});

// 建立 Adapter
var adapterLogger = loggerFactory.CreateLogger<ModbusTcpPlcAdapter>();
await using var adapter = new ModbusTcpPlcAdapter(connectionOptions, adapterLogger);

// 建立 SignalMapper
var mapperLogger = loggerFactory.CreateLogger<PlcSignalMapper>();
var signalMapper = new PlcSignalMapper(adapter, signalOptions, mapperLogger);

// 建立 HandshakeService
var handshakeLogger = loggerFactory.CreateLogger<PlcHandshakeService>();
var handshakeService = new PlcHandshakeService(
    adapter, signalMapper, handshakeOptions, connectionOptions, handshakeLogger);

// 監聽事件
adapter.ConnectionStateChanged += (s, connected) =>
{
    Console.WriteLine($"[連線] {(connected ? "已連線" : "已斷線")}");
};

handshakeService.StateChanged += (s, e) =>
{
    Console.WriteLine($"[狀態] {e.OldState} → {e.NewState}");
};

handshakeService.TriggerReceived += async (s, e) =>
{
    Console.WriteLine($"[觸發] 第 {e.TriggerCount} 次觸發");

    // 模擬 AI 推論延遲
    Console.WriteLine("[推論] 模擬 AI 推論中 (1秒)...");
    await Task.Delay(1000);

    // 隨機產生結果
    var result = Random.Shared.Next(2) == 0 ? PlcInspectionResult.Ok : PlcInspectionResult.Ng;
    Console.WriteLine($"[推論] 推論完成，結果: {result}");

    await handshakeService.ReportResultAsync(result);
};

try
{
    // 連線
    Console.WriteLine("\n>>> 連線到 PLC");
    await adapter.ConnectAsync();

    // 啟動交握服務
    Console.WriteLine("\n>>> 啟動交握服務");
    await handshakeService.StartAsync();

    Console.WriteLine();
    Console.WriteLine("========================================");
    Console.WriteLine("交握服務已啟動！");
    Console.WriteLine();
    Console.WriteLine("請在 Modbus Slave 中操作:");
    Console.WriteLine("  1. 將 Address 10000 設為 1 (觸發)");
    Console.WriteLine("  2. 觀察 Address 0~3 的變化");
    Console.WriteLine("  3. 將 Address 10000 設回 0 (重置)");
    Console.WriteLine("  4. 重複步驟 1~3 測試多次觸發");
    Console.WriteLine();
    Console.WriteLine("按 Q + Enter 停止服務並結束");
    Console.WriteLine("========================================");
    Console.WriteLine();

    // 等待使用者輸入
    while (true)
    {
        var input = Console.ReadLine();
        if (input?.ToUpper() == "Q")
        {
            break;
        }
    }

    // 停止服務
    Console.WriteLine("\n>>> 停止交握服務");
    await handshakeService.StopAsync();

    // 斷線
    Console.WriteLine(">>> 斷開連線");
    await adapter.DisconnectAsync();

    Console.WriteLine("\n=== 測試完成 ===");
}
catch (Exception ex)
{
    Console.WriteLine($"\n!!! 測試失敗: {ex.Message}");
    Console.WriteLine($"{ex.StackTrace}");
}

Console.WriteLine("\n按 Enter 結束...");
Console.ReadLine();
