using System.Net.Sockets;

/// <summary>
/// PLC 測試模擬器 - 模擬 PC 端的 Modbus Master 行為
/// 主動連線到 PLC，輪詢觸發訊號並回應結果
///
/// Modbus 位址對應：
/// - 10001 (Discrete Input 0): PLC 觸發訊號 (PC 讀取)
/// - 00001 (Coil 0): CaptureStatus (PC 寫入)
/// - 00002 (Coil 1): InspectDone (PC 寫入)
/// - 00003 (Coil 2): ResultOk (PC 寫入)
/// - 00004 (Coil 3): ResultNg (PC 寫入)
/// </summary>

Console.WriteLine("=== PLC 測試模擬器 (Master 模式) ===");
Console.WriteLine("模擬 PC 端 Modbus Master，主動連線 PLC 並測試交握流程");
Console.WriteLine();

// 設定 - 可透過命令列參數覆寫
string plcIp = "192.168.250.1";
int port = 502;
int pollIntervalMs = 50;
int simulatedCaptureTimeMs = 1500;
int simulatedInferenceTimeMs = 700;

// 解析命令列參數
for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLower())
    {
        case "--ip" or "-i":
            if (i + 1 < args.Length) plcIp = args[++i];
            break;
        case "--port" or "-p":
            if (i + 1 < args.Length) int.TryParse(args[++i], out port);
            break;
        case "--poll" or "-l":
            if (i + 1 < args.Length) int.TryParse(args[++i], out pollIntervalMs);
            break;
        case "--capture" or "-c":
            if (i + 1 < args.Length) int.TryParse(args[++i], out simulatedCaptureTimeMs);
            break;
        case "--inference" or "-n":
            if (i + 1 < args.Length) int.TryParse(args[++i], out simulatedInferenceTimeMs);
            break;
        case "--help" or "-h":
            Console.WriteLine("用法: PlcTestSimulator [選項]");
            Console.WriteLine();
            Console.WriteLine("選項:");
            Console.WriteLine("  --ip, -i <IP>        PLC IP 位址 (預設: 192.168.250.1)");
            Console.WriteLine("  --port, -p <Port>    PLC Port (預設: 502)");
            Console.WriteLine("  --poll, -l <ms>      輪詢間隔 (預設: 50ms)");
            Console.WriteLine("  --capture, -c <ms>   模擬取像時間 (預設: 1500ms)");
            Console.WriteLine("  --inference, -n <ms> 模擬推論時間 (預設: 700ms)");
            Console.WriteLine();
            Console.WriteLine("範例:");
            Console.WriteLine("  PlcTestSimulator --ip 192.168.250.1");
            Console.WriteLine("  PlcTestSimulator -i 192.168.250.1 -c 2000 -n 500");
            return;
    }
}

Console.WriteLine($"PLC 位址: {plcIp}:{port}");
Console.WriteLine($"輪詢間隔: {pollIntervalMs}ms");
Console.WriteLine($"模擬取像時間: {simulatedCaptureTimeMs}ms");
Console.WriteLine($"模擬推論時間: {simulatedInferenceTimeMs}ms");
Console.WriteLine();
Console.WriteLine("按 Q 退出, R 讀取所有訊號, T 手動觸發一次流程");
Console.WriteLine();

var cts = new CancellationTokenSource();
TcpClient? client = null;
NetworkStream? stream = null;
ushort transactionId = 0;
bool lastTriggerValue = false;
int triggerCount = 0;
bool isProcessing = false;

// 背景處理鍵盤輸入
_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Q)
            {
                Console.WriteLine("\n正在退出...");
                cts.Cancel();
            }
            else if (key.Key == ConsoleKey.R && stream != null)
            {
                await ReadAllSignalsAsync();
            }
            else if (key.Key == ConsoleKey.T && stream != null && !isProcessing)
            {
                Console.WriteLine("\n[手動] 觸發一次完整流程...");
                triggerCount++;
                _ = SimulatePcResponseAsync();
            }
        }
        await Task.Delay(100);
    }
});

try
{
    // 連線到 PLC
    Console.WriteLine($"正在連線到 PLC {plcIp}:{port}...");
    client = new TcpClient();
    await client.ConnectAsync(plcIp, port);
    stream = client.GetStream();
    Console.WriteLine("✓ PLC 連線成功!");
    Console.WriteLine();

    // 讀取初始狀態
    await ReadAllSignalsAsync();
    Console.WriteLine();
    Console.WriteLine("開始輪詢觸發訊號...");
    Console.WriteLine();

    // 主輪詢迴圈
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            // 讀取 10001 (Discrete Input 0)
            var trigger = await ReadDiscreteInputAsync(0);

            // Rising Edge 偵測
            if (trigger && !lastTriggerValue && !isProcessing)
            {
                triggerCount++;
                Console.WriteLine($"\n========== 偵測到觸發 #{triggerCount}! (10001: 0→1) ==========");
                _ = SimulatePcResponseAsync();
            }

            lastTriggerValue = trigger;

            await Task.Delay(pollIntervalMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[錯誤] 輪詢失敗: {ex.Message}");
            await Task.Delay(1000, cts.Token);

            // 嘗試重連
            try
            {
                Console.WriteLine("嘗試重新連線...");
                client?.Close();
                client = new TcpClient();
                await client.ConnectAsync(plcIp, port);
                stream = client.GetStream();
                Console.WriteLine("✓ 重連成功!");
            }
            catch
            {
                Console.WriteLine("重連失敗，稍後重試...");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[錯誤] {ex.Message}");
}
finally
{
    stream?.Close();
    client?.Close();
    Console.WriteLine("模擬器已停止");
}

// ===== 輔助方法 =====

async Task<bool> ReadDiscreteInputAsync(ushort address)
{
    // Function Code 0x02: Read Discrete Inputs
    var request = BuildRequest(0x02, address, 1);
    await stream!.WriteAsync(request);

    var response = new byte[256];
    var bytesRead = await stream.ReadAsync(response);

    if (bytesRead >= 10 && response[7] == 0x02)
    {
        return (response[9] & 0x01) != 0;
    }
    throw new Exception($"讀取 Discrete Input 失敗");
}

async Task<bool> ReadCoilAsync(ushort address)
{
    // Function Code 0x01: Read Coils
    var request = BuildRequest(0x01, address, 1);
    await stream!.WriteAsync(request);

    var response = new byte[256];
    var bytesRead = await stream.ReadAsync(response);

    if (bytesRead >= 10 && response[7] == 0x01)
    {
        return (response[9] & 0x01) != 0;
    }
    throw new Exception($"讀取 Coil 失敗");
}

async Task WriteCoilAsync(ushort address, bool value)
{
    // Function Code 0x05: Write Single Coil
    var request = new byte[12];
    var tid = ++transactionId;
    request[0] = (byte)(tid >> 8);
    request[1] = (byte)(tid & 0xFF);
    request[2] = 0; request[3] = 0; // Protocol ID
    request[4] = 0; request[5] = 6; // Length
    request[6] = 1; // Unit ID
    request[7] = 0x05; // Function Code
    request[8] = (byte)(address >> 8);
    request[9] = (byte)(address & 0xFF);
    request[10] = value ? (byte)0xFF : (byte)0x00;
    request[11] = 0x00;

    await stream!.WriteAsync(request);

    var response = new byte[256];
    await stream.ReadAsync(response);
}

byte[] BuildRequest(byte functionCode, ushort startAddress, ushort quantity)
{
    var request = new byte[12];
    var tid = ++transactionId;
    request[0] = (byte)(tid >> 8);
    request[1] = (byte)(tid & 0xFF);
    request[2] = 0; request[3] = 0; // Protocol ID
    request[4] = 0; request[5] = 6; // Length
    request[6] = 1; // Unit ID
    request[7] = functionCode;
    request[8] = (byte)(startAddress >> 8);
    request[9] = (byte)(startAddress & 0xFF);
    request[10] = (byte)(quantity >> 8);
    request[11] = (byte)(quantity & 0xFF);
    return request;
}

async Task ReadAllSignalsAsync()
{
    try
    {
        Console.WriteLine();
        Console.WriteLine("===== 目前訊號狀態 =====");

        var trigger = await ReadDiscreteInputAsync(0);
        Console.WriteLine($"  10001 (TriggerCapture): {(trigger ? "ON" : "OFF")}");

        var capture = await ReadCoilAsync(0);
        Console.WriteLine($"  00001 (CaptureStatus):  {(capture ? "ON" : "OFF")}");

        var done = await ReadCoilAsync(1);
        Console.WriteLine($"  00002 (InspectDone):    {(done ? "ON" : "OFF")}");

        var ok = await ReadCoilAsync(2);
        Console.WriteLine($"  00003 (ResultOk):       {(ok ? "ON" : "OFF")}");

        var ng = await ReadCoilAsync(3);
        Console.WriteLine($"  00004 (ResultNg):       {(ng ? "ON" : "OFF")}");

        Console.WriteLine("=========================");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[錯誤] 讀取訊號失敗: {ex.Message}");
    }
}

async Task SimulatePcResponseAsync()
{
    isProcessing = true;
    try
    {
        // Step 1: 清除舊訊號 + 設定 CaptureStatus = ON
        Console.WriteLine($"  [PC] 清除舊訊號 (00002-00004=OFF)");
        await WriteCoilAsync(1, false);  // 00002 InspectDone
        await WriteCoilAsync(2, false);  // 00003 ResultOk
        await WriteCoilAsync(3, false);  // 00004 ResultNg

        Console.WriteLine($"  [PC] 設定 00001 (CaptureStatus) = ON");
        await WriteCoilAsync(0, true);   // 00001 CaptureStatus

        // Step 2: 模擬取像
        Console.WriteLine($"  [PC] 取像中... ({simulatedCaptureTimeMs}ms)");
        await Task.Delay(simulatedCaptureTimeMs);

        // Step 3: 取像完成，設定 CaptureStatus = OFF
        Console.WriteLine($"  [PC] 設定 00001 (CaptureStatus) = OFF");
        await WriteCoilAsync(0, false);

        // Step 4: 模擬推論
        Console.WriteLine($"  [PC] 推論中... ({simulatedInferenceTimeMs}ms)");
        await Task.Delay(simulatedInferenceTimeMs);

        // Step 5: 送出結果
        bool isOk = triggerCount % 2 == 1; // 交替 OK/NG

        if (isOk)
        {
            Console.WriteLine($"  [PC] 設定 00003 (ResultOk) = ON");
            await WriteCoilAsync(2, true);
        }
        else
        {
            Console.WriteLine($"  [PC] 設定 00004 (ResultNg) = ON");
            await WriteCoilAsync(3, true);
        }

        Console.WriteLine($"  [PC] 設定 00002 (InspectDone) = ON");
        await WriteCoilAsync(1, true);

        Console.WriteLine($"  [PC] ===== 觸發 #{triggerCount} 處理完成 (結果: {(isOk ? "OK" : "NG")}) =====");

        // Step 6: 等待並監控 PLC 是否清除 10001
        Console.WriteLine($"  [PC] 等待 PLC 清除 10001...");

        var startTime = DateTime.Now;
        while ((DateTime.Now - startTime).TotalSeconds < 10)
        {
            await Task.Delay(100);
            var trigger = await ReadDiscreteInputAsync(0);
            if (!trigger)
            {
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                Console.WriteLine($"  [PC] ✓ PLC 已清除 10001 (耗時 {elapsed:F0}ms)");
                lastTriggerValue = false;
                Console.WriteLine();
                return;
            }
        }

        Console.WriteLine($"  [PC] ⚠ 警告: PLC 10 秒內未清除 10001!");
        Console.WriteLine($"  [PC] 這可能是 PLC/HMI 程式的問題");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [PC] 錯誤: {ex.Message}");
    }
    finally
    {
        isProcessing = false;
    }
}
