using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Devices.Blow;

/// <summary>
/// 吹氣派送實作（移植自 <c>模號檢驗/相機版/Blow/BlowController.cs</c>）。
///
/// <list type="bullet">
/// <item><b>不阻塞</b>：<see cref="Enqueue"/> 只把請求丟進佇列就返回，延遲與送出都在背景 worker。</item>
/// <item><b>去重</b>：同一 Id 只吹一次（多幀投票／重試會對同一片產生多次判定）。</item>
/// <item><b>不影響產線</b>：送出失敗只記 log；worker 例外不會往外傳。</item>
/// </list>
///
/// <para>⚠ 去重表會隨時間長大，所以有上限：超過就清掉最舊的一半
/// （產線跑整天可能上萬片，無上限等於記憶體洩漏）。</para>
/// </summary>
public sealed class BlowDispatcher : IBlowDispatcherPort, IDisposable
{
    /// <summary>去重表上限。超過清掉最舊一半——同一片鏡片不可能隔這麼久又判一次。</summary>
    private const int MaxSeenIds = 20_000;

    private readonly IBlowOutputPort _output;
    private readonly IOptionsMonitor<BlowOptions> _options;
    private readonly ILogger<BlowDispatcher>? _logger;

    private readonly BlockingCollection<BlowRequest> _queue = new(new ConcurrentQueue<BlowRequest>());
    private readonly LinkedList<string> _seenOrder = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _seenLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private bool _disposed;

    public BlowDispatcher(
        IBlowOutputPort output,
        IOptionsMonitor<BlowOptions> options,
        ILogger<BlowDispatcher>? logger = null)
    {
        _output = output;
        _options = options;
        _logger = logger;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public bool Enabled => _options.CurrentValue.Enabled;

    public bool Enqueue(BlowRequest request)
    {
        if (_disposed) return false;

        var o = _options.CurrentValue;
        if (!o.Enabled) return false;

        // 原因開關：混料／NG 各自可關（現場常常只吹混料，NG 走別的站處理）。
        if (request.Reason == BlowRequest.ReasonMismatch && !o.BlowOnMismatch) return false;
        if (request.Reason == BlowRequest.ReasonNg && !o.BlowOnNg) return false;

        lock (_seenLock)
        {
            if (!_seen.Add(request.Id))
            {
                _logger?.LogDebug("[Blow] 略過重複 id={Id}", request.Id);
                return false;
            }
            _seenOrder.AddLast(request.Id);
            if (_seen.Count > MaxSeenIds) TrimSeen();
        }

        try
        {
            _queue.Add(request);
        }
        catch (Exception ex)
        {
            // 佇列已關閉（關程式中）→ 不是錯誤，也不該吵。
            _logger?.LogDebug(ex, "[Blow] 佇列已關閉，略過 id={Id}", request.Id);
            return false;
        }

        _logger?.LogInformation(
            "[Blow] 排入 id={Id} reason={Reason} expect={Expect} got={Got} delay={Delay}ms",
            request.Id, request.Reason,
            $"{request.ExpectedMohao}/{request.ExpectedXuehao}",
            $"{request.DetectedMohao}/{request.DetectedXuehao}",
            request.DelayMs);
        return true;
    }

    /// <summary>清掉最舊一半的去重記錄。</summary>
    private void TrimSeen()
    {
        var remove = _seen.Count / 2;
        for (int i = 0; i < remove && _seenOrder.First is not null; i++)
        {
            _seen.Remove(_seenOrder.First.Value);
            _seenOrder.RemoveFirst();
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    // 延遲：等工件從拍照位走到吹嘴。
                    // 一律以設定檔的 DelayMs 為準（現場調一個地方就好）；
                    // request.DelayMs > 0 才視為呼叫端刻意覆寫（測試吹氣用）。
                    var delayMs = request.DelayMs > 0 ? request.DelayMs : _options.CurrentValue.DelayMs;
                    if (delayMs > 0)
                        await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false);

                    await _output.SendAsync(request, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;   // 關程式
                }
                catch (Exception ex)
                {
                    // 單筆失敗不能讓 worker 死掉，否則後面全部不吹了。
                    _logger?.LogWarning(ex, "[Blow] 送出失敗 id={Id}", request.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常關閉
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Blow] 派送 worker 異常結束");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { /* 已關閉 */ }
        _cts.Cancel();
        try { _worker.Wait(1500); } catch { /* 關程式時不等太久 */ }
        _queue.Dispose();
        _cts.Dispose();
    }
}
