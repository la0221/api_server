using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Devices.Blow;

/// <summary>
/// 吹氣觸發輸出（TCP）——把觸發訊號送給現場的 IO 服務，由它驅動吹嘴。
///
/// <para><b>對接對象：<c>NgAirBlowService</c></b>（規格見 <c>NgAirBlowService/對接說明.md</c>）：</para>
/// <list type="bullet">
/// <item>對方是 <b>TCP Server，Port 5000</b>；我方是 client 主動連線。</item>
/// <item>資料是 <b>ASCII 純文字</b>；判定邏輯是「**內容含 <c>NG</c>（不分大小寫）就吹**」。</item>
/// <item>收到後吹 <b>0.3 秒</b>自動關；吹氣期間再收到會**重新計時**（延長，不會被打斷）。</item>
/// <item>對方**建議長連線**（少掉每次重連的開銷）→ 見 <see cref="BlowOptions.KeepAlive"/>。</item>
/// </list>
///
/// <para>⚠ <b>踩過的坑（2026-08-22 實測）</b>：本類別原本送的是相機版那套 JSON
/// （<c>{"type":"blow","reason":"MISMATCH",...}</c>）。那串字**整串沒有 NG 這兩個字**，
/// 送給 NgAirBlowService 會被當雜訊丟掉——**混料照判、log 照寫，就是不吹**，
/// 而且兩邊都不會報錯。所以 <see cref="BlowOptions.Format"/> 預設是 <c>NgText</c>，
/// 且 NgText 的內容**一定以 NG 開頭**。</para>
///
/// <para>⚠ 送不出去（對方沒開、網路不通）**絕不能影響辨識流程**——一律吞例外只記 log。</para>
/// </summary>
public sealed class TcpBlowOutput : IBlowOutputPort, IDisposable
{
    private readonly IOptionsMonitor<BlowOptions> _options;
    private readonly ILogger<TcpBlowOutput>? _logger;

    /// <summary>長連線用的連線。對方建議長連線，斷了就重連。</summary>
    private TcpClient? _client;
    private string _connectedTo = "";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public TcpBlowOutput(
        IOptionsMonitor<BlowOptions> options,
        ILogger<TcpBlowOutput>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public string DisplayName =>
        string.Equals(_options.CurrentValue.Format, "Json", StringComparison.OrdinalIgnoreCase)
            ? "TCP／JSON（相機版自製監聽程式）"
            : "TCP／NG 純文字（NgAirBlowService）";

    public async Task SendAsync(BlowRequest request, CancellationToken cancellationToken)
    {
        var o = _options.CurrentValue;
        var host = string.IsNullOrWhiteSpace(o.Host) ? "127.0.0.1" : o.Host.Trim();
        var payload = BuildPayload(request, o);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 長連線可能已被對方關掉／網路斷過 → 第一次失敗就重連再試一次。
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var stream = await EnsureConnectedAsync(host, o, cancellationToken).ConfigureAwait(false);
                    var bytes = Encoding.ASCII.GetBytes(payload);   // 對方用 ASCII 解，這裡就用 ASCII 編
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (!o.KeepAlive) Disconnect();

                    _logger?.LogInformation(
                        "[Blow→TCP] {Host}:{Port} reason={Reason} id={Id} 送出：{Payload}",
                        host, o.Port, request.Reason, request.Id, payload.TrimEnd());
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;   // 關程式
                }
                catch (Exception ex) when (attempt == 1)
                {
                    // 多半是長連線已失效：丟掉重來一次，不吵。
                    _logger?.LogDebug(ex, "[Blow→TCP] 第一次送出失敗，重連後重試");
                    Disconnect();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        "[Blow→TCP] 送出失敗（{Host}:{Port}）：{Message}。" +
                        "請確認 NgAirBlowService 已啟動、埠號一致、防火牆已放行。（辨識流程不受影響）",
                        host, o.Port, ex.Message);
                    Disconnect();
                    return;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 組要送出去的字串。
    /// <para><b>NgText</b>：對方只認「含 NG」，但它會把**收到的原始內容寫進自己的 log**——
    /// 所以這裡不送光禿禿的 <c>NG</c>，而是把判定脈絡一起帶過去，
    /// 出事時兩邊的 log 可以直接對帳（誰在幾點因為什麼吹了哪一片）。</para>
    /// <para>⚠ 一定要 <b>NG 開頭</b>，而且**全 ASCII**（對方用 ASCII 解碼；中文會變亂碼）。</para>
    /// </summary>
    private static string BuildPayload(BlowRequest request, BlowOptions o)
    {
        if (string.Equals(o.Format, "Json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new
            {
                type = "blow",
                channel = o.Channel,
                reason = request.Reason,
                id = request.Id,
                expect = $"{request.ExpectedMohao}/{request.ExpectedXuehao}",
                got = $"{request.DetectedMohao}/{request.DetectedXuehao}",
                confMohao = Math.Round(request.ConfMohao, 4),
                confXuehao = Math.Round(request.ConfXuehao, 4),
                ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
            }) + "\n";
        }

        // NG 開頭 → 保證觸發；後面是給對方 log 對帳用的脈絡。
        return "NG " +
               $"reason={Ascii(request.Reason)} " +
               $"id={Ascii(request.Id)} " +
               $"expect={Ascii(request.ExpectedMohao)}/{Ascii(request.ExpectedXuehao)} " +
               $"got={Ascii(request.DetectedMohao)}/{Ascii(request.DetectedXuehao)} " +
               $"conf={request.ConfMohao:0.00}/{request.ConfXuehao:0.00} " +
               $"ts={DateTime.Now:HH:mm:ss.fff}\n";
    }

    /// <summary>非 ASCII 一律換掉——對方用 <c>Encoding.ASCII</c> 解碼，中文過去會變 <c>?</c> 亂碼。</summary>
    private static string Ascii(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "-";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is >= (char)0x20 and < (char)0x7F && c != ' ' ? c : '_');
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    private async Task<NetworkStream> EnsureConnectedAsync(
        string host, BlowOptions o, CancellationToken ct)
    {
        var target = $"{host}:{o.Port}";
        if (_client is { Connected: true } && _connectedTo == target)
            return _client.GetStream();

        Disconnect();

        var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(o.ConnectTimeoutMs);
        await client.ConnectAsync(host, o.Port, cts.Token).ConfigureAwait(false);

        _client = client;
        _connectedTo = target;
        _logger?.LogInformation("[Blow→TCP] 已連上 {Target}（{Mode}）",
            target, o.KeepAlive ? "長連線" : "短連線");
        return client.GetStream();
    }

    private void Disconnect()
    {
        try { _client?.Close(); } catch { /* 已斷 */ }
        _client?.Dispose();
        _client = null;
        _connectedTo = "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _gate.Dispose();
    }
}
