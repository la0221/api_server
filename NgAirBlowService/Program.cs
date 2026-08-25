using System.Net;
using System.Net.Sockets;
using System.Text;
using Advantech.Motion;
using NgAirBlowService;

const int TcpPort = 5000;
const ushort DoChannel = 0;
const int BlowMilliseconds = 300;
const ushort MaxRingsToScan = 2; // Ring 0 = Motion Ring, Ring 1 = Fast IO Ring

// DO0 極性：ON(1) = 關氣（待機狀態），OFF(0) = 吹氣
const byte AirClosed = 1;
const byte AirBlowing = 0;

IntPtr deviceHandle = IntPtr.Zero;
ushort doRing = 0;
ushort doSlaveId = 0;

var blowLock = new object();
CancellationTokenSource? blowCts = null;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Logger.Log($"[FATAL] Unhandled exception: {e.ExceptionObject}");

try
{
    Logger.Log("[Init] Opening Advantech EtherCAT device...");
    OpenDeviceAndDiscoverDo();
    Logger.Log($"[Init] DO module found at ring {doRing}, slave id 0x{doSlaveId:X}, channel {DoChannel}.");

    SetDoBit(AirClosed);
    Logger.Log("[Init] Air set to closed (idle state).");

    var listener = new TcpListener(IPAddress.Any, TcpPort);
    listener.Start();
    Logger.Log($"[TCP] Listening on port {TcpPort}. Waiting for NG signal...");

    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = HandleClientAsync(client);
    }
}
catch (Exception ex)
{
    Logger.Log($"[FATAL] Startup/main loop crashed: {ex}");
    throw;
}

async Task HandleClientAsync(TcpClient client)
{
    var remote = client.Client.RemoteEndPoint;
    Logger.Log($"[TCP] Client connected: {remote}");
    using (client)
    using (var stream = client.GetStream())
    {
        var buffer = new byte[256];
        try
        {
            while (client.Connected)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                var text = Encoding.ASCII.GetString(buffer, 0, read).Trim();
                Logger.Log($"[TCP] Received from {remote}: \"{text}\"");

                if (text.Contains("NG", StringComparison.OrdinalIgnoreCase))
                {
                    TriggerAirBlow();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[TCP] Client {remote} error: {ex}");
        }
    }
    Logger.Log($"[TCP] Client disconnected: {remote}");
}

void TriggerAirBlow()
{
    lock (blowLock)
    {
        // 若吹氣仍在進行中，取消先前的關閉排程，重新從 0.3 秒開始計時（延長吹氣）。
        blowCts?.Cancel();
        blowCts = new CancellationTokenSource();
        var token = blowCts.Token;

        SetDoBit(AirBlowing);
        Logger.Log("[Blow] Air ON (blowing)");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BlowMilliseconds, token);
                lock (blowLock)
                {
                    if (!token.IsCancellationRequested)
                    {
                        SetDoBit(AirClosed);
                        Logger.Log("[Blow] Air OFF (closed)");
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // 被新訊號取消，交由新排程負責關閉。
            }
            catch (Exception ex)
            {
                Logger.Log($"[Blow] Delayed-off task crashed: {ex}");
            }
        });
    }
}

void SetDoBit(byte value)
{
    var result = AdvMotionWrapper.DaqDoSetBit(deviceHandle, doRing, doSlaveId, DoChannel, value);
    if (result != 0)
    {
        Logger.Log($"[Error] Set DO failed (err=0x{result:X8})");
    }
}

void OpenDeviceAndDiscoverDo()
{
    var devList = new DEV_LIST[AdvMotionWrapper.MaxDevices];
    uint count = 0;
    var listResult = AdvMotionWrapper.GetAvailableDevs(devList, AdvMotionWrapper.MaxDevices, ref count);
    if (listResult != 0 || count == 0)
    {
        throw new InvalidOperationException($"No Advantech motion device found (err=0x{listResult:X8}).");
    }

    var openResult = AdvMotionWrapper.DevOpen(devList[0].DeviceNum, 1000, ref deviceHandle);
    if (openResult != 0 || deviceHandle == IntPtr.Zero)
    {
        throw new InvalidOperationException($"Failed to open device (err=0x{openResult:X8}).");
    }

    var doCandidates = new List<(ushort ring, ushort slaveId)>();
    for (ushort ring = 0; ring < MaxRingsToScan; ring++)
    {
        for (ushort slaveId = 0; slaveId < AdvMotionWrapper.MaxDevices; slaveId++)
        {
            byte probe = 0;
            if (AdvMotionWrapper.DaqDoGetBit(deviceHandle, ring, slaveId, 0, ref probe) == 0)
            {
                doCandidates.Add((ring, slaveId));
            }
        }
    }

    if (doCandidates.Count == 0)
    {
        throw new InvalidOperationException("Failed to discover DO module (AMAX-5057) on any ring.");
    }

    if (doCandidates.Count > 1)
    {
        Logger.Log($"[Warn] Found {doCandidates.Count} DO candidates, using the first one (ring {doCandidates[0].ring}, slave 0x{doCandidates[0].slaveId:X}).");
    }

    (doRing, doSlaveId) = doCandidates[0];
}
