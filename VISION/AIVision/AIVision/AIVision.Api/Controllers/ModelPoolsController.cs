using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIVision.Api.Services;
using AIVision.Application.Ports.MoldCode;
using Microsoft.AspNetCore.Mvc;

namespace AIVision.Api.Controllers;

/// <summary>
/// 模型池總覽與現用版本切換（2026-08-19）。
///
/// <para><b>為什麼要有</b>：父端監控原本只看得到 CRNN 一個行程池、而且<b>沒有任何地方可以選模型</b>。
/// 但模型是<b>按用途分池</b>的——模號穴號走自己的池、公母模走自己的、瑕疵再一個
/// （登錄庫早就是 task 化的，只是沒有一個端點把「每個用途各自有哪些版本、現在用哪個」講清楚）。</para>
///
/// <list type="bullet">
/// <item><c>GET /api/models/pools</c>：每個用途一張卡（版本清單／現用版本／已載入行程／能不能推論）</item>
/// <item><c>POST /api/models/{task}/current</c>：把某用途切到指定版本（執行期生效，免重啟）</item>
/// </list>
///
/// <para>與 <see cref="ModelsController"/> 的分工：那支管「倉庫」（列版本/上架/下載），
/// 這支管「現在這台機器實際在用什麼」。</para>
/// </summary>
[ApiController]
[Route("api/models")]
public sealed class ModelPoolsController : ControllerBase
{
    private readonly ModelRegistryService _registry;
    private readonly CrnnSidecarService _crnn;
    private readonly IMoldCodePairRecognizerPort _pair;
    private readonly ILogger<ModelPoolsController> _logger;

    public ModelPoolsController(
        ModelRegistryService registry,
        CrnnSidecarService crnn,
        IMoldCodePairRecognizerPort pair,
        ILogger<ModelPoolsController> logger)
    {
        _registry = registry;
        _crnn = crnn;
        _pair = pair;
        _logger = logger;
    }

    /// <summary>每個用途一張卡：有哪些版本、現在用哪個、池裡載了誰、能不能推論。</summary>
    [HttpGet("pools")]
    [ProducesResponseType(typeof(ModelPoolsResponse), StatusCodes.Status200OK)]
    public IActionResult Pools()
    {
        var pairSwitch = _pair as IMoldCodePairModelSwitch;
        var pools = new List<ModelPoolDto>();

        foreach (var (task, opt) in _registry.Tasks)
        {
            var versions = _registry.ListVersions(task);
            var isCrnn = string.Equals(task, "ocr_crnn", StringComparison.OrdinalIgnoreCase);
            var isPair = string.Equals(task, "ocr_pair", StringComparison.OrdinalIgnoreCase);

            var loaded = isCrnn
                ? _crnn.LoadedVersions.Select(v => new ModelPoolLoadedDto
                {
                    Version = v.Version,
                    Ready = v.Ready,
                }).ToList()
                : isPair
                    // registry 的快取只涵蓋「指定版本推論」建出來的實例；
                    // 從畫面切版走的是 SwitchableTwoHeadRecognizer，不在那份快取裡 → 要併進來，
                    // 否則畫面會出現「現用 v6.7.2、已載入（尚未載入）」這種自相矛盾的顯示。
                    ? _registry.CachedOcrPairVersions
                        .Concat(pairSwitch?.CurrentVersionName is { Length: > 0 } cur ? new[] { cur } : Array.Empty<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(v => new ModelPoolLoadedDto { Version = v, Ready = true }).ToList()
                    : new List<ModelPoolLoadedDto>();

            // 能不能切/能不能推論：CRNN 要 sidecar 有開；雙 head 要辨識器支援執行期切換。
            var switchable = (isCrnn && _crnn.Enabled) || (isPair && pairSwitch is not null);

            var (groupKey, groupName, engineName) = ClassifyStation(task, opt.DisplayName);
            pools.Add(new ModelPoolDto
            {
                Task = task,
                GroupKey = groupKey,
                GroupName = groupName,
                EngineName = engineName,
                DisplayName = string.IsNullOrWhiteSpace(opt.DisplayName) ? task : opt.DisplayName,
                Root = opt.Root,
                RootExists = !string.IsNullOrWhiteSpace(opt.Root) && Directory.Exists(opt.Root),
                RequiredFiles = opt.Files.ToList(),
                Versions = versions.Select(v => v.Version).ToList(),
                CurrentVersion = isCrnn
                    ? (string.IsNullOrWhiteSpace(_crnn.DefaultVersion) ? null : _crnn.DefaultVersion)
                    : isPair ? pairSwitch?.CurrentVersionName : null,
                LoadedVersions = loaded,
                InferReady = (isCrnn && _crnn.Enabled) || isPair,
                CanSwitch = switchable,
                Note = BuildNote(task, isCrnn, isPair, versions.Count, opt.Root),
            });
        }

        return Ok(new ModelPoolsResponse { Pools = pools });
    }

    /// <summary>
    /// 把「用途」歸到**站點**——現場的心智模型是站點不是引擎：
    /// 模號穴號不管走 CRNN 還是雙 head，**都是同一個模號穴號站點**，只是換引擎，
    /// 不該在畫面上變成兩張並排的卡片（2026-08-19 使用者指正）。
    /// </summary>
    private static (string GroupKey, string GroupName, string EngineName) ClassifyStation(
        string task, string? displayName)
    {
        return task.ToLowerInvariant() switch
        {
            "ocr_crnn" => ("moldcode", "模號穴號", "CRNN 字元式"),
            "ocr_pair" => ("moldcode", "模號穴號", "雙 head 分類"),
            "gongmu" => ("gongmu", "公母模", "預設引擎"),
            "defect" => ("defect", "瑕疵檢查", "預設引擎"),
            // 未知用途自成一站，別硬塞進既有站點（新增用途時畫面自動長出一張卡）
            _ => (task.ToLowerInvariant(), string.IsNullOrWhiteSpace(displayName) ? task : displayName!, "預設引擎"),
        };
    }

    private string? BuildNote(string task, bool isCrnn, bool isPair, int versionCount, string root)
    {
        if (isCrnn && !_crnn.Enabled)
            return "CRNN sidecar 未啟用（appsettings CrnnSidecar:Enabled）——此用途目前無法推論。";
        if (!isCrnn && !isPair)
            return "此用途目前只有倉庫能力（列版本／上架／下載），推論端點待模型到位再開。";
        if (versionCount == 0)
            return $"登錄夾沒有任何完整版本：{root}（版本要一夾一版，且該用途要求的檔案要齊）。";
        return null;
    }

    /// <summary>
    /// 把某用途切到指定版本（執行期生效，免改設定檔免重啟）。
    /// <para>CRNN：只換預設版本，下一筆請求自然冷啟該版本（20-90s）。
    /// 雙 head：直接載入該版本的 ONNX（~1s）。其他用途尚無推論端點 → 400。</para>
    /// <para>⚠ 只影響本次執行；永久生效請改 appsettings。</para>
    /// </summary>
    [HttpPost("{task}/current")]
    [ProducesResponseType(typeof(SetCurrentVersionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult SetCurrent(string task, [FromBody] SetCurrentVersionRequest request)
    {
        if (!_registry.TaskExists(task))
            return Problem($"未知用途 '{task}'（可用：{string.Join("、", _registry.Tasks.Keys)}）。",
                statusCode: StatusCodes.Status404NotFound);

        var version = (request?.Version ?? "").Trim();
        if (version.Length == 0)
            return Problem("version 必填。", statusCode: StatusCodes.Status400BadRequest);

        if (string.Equals(task, "ocr_crnn", StringComparison.OrdinalIgnoreCase))
        {
            if (!_crnn.Enabled)
                return Problem("CRNN sidecar 未啟用，無法切換。", statusCode: StatusCodes.Status400BadRequest);
            var err = _crnn.SetDefaultVersion(version);
            if (err is not null)
                return Problem(err, statusCode: StatusCodes.Status400BadRequest);

            _logger.LogInformation("[ModelPools] ocr_crnn 現用版本 → {Version}", version);
            return Ok(new SetCurrentVersionResponse
            {
                Task = task,
                CurrentVersion = version,
                Message = $"已切到 {version}。下一筆未指定版本的送檢會用它；" +
                          "若該版本還沒在行程池中，第一張會冷啟（20–90 秒）屬正常。",
            });
        }

        if (string.Equals(task, "ocr_pair", StringComparison.OrdinalIgnoreCase))
        {
            if (_pair is not IMoldCodePairModelSwitch sw)
                return Problem("目前的雙 head 辨識器不支援執行期切換版本。",
                    statusCode: StatusCodes.Status400BadRequest);

            var mo = _registry.ResolveFile("ocr_pair", version, "mohao.onnx");
            var xu = _registry.ResolveFile("ocr_pair", version, "xuehao.onnx");
            if (mo is null || xu is null)
                return Problem($"登錄庫（ocr_pair）找不到版本 '{version}'——請先從發布頁上架。",
                    statusCode: StatusCodes.Status404NotFound);

            try
            {
                sw.LoadVersion(mo, xu, version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ModelPools] ocr_pair 切版失敗 {Version}", version);
                return Problem($"載入版本 '{version}' 失敗：{ex.Message}",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            _logger.LogInformation("[ModelPools] ocr_pair 現用版本 → {Version}", version);
            return Ok(new SetCurrentVersionResponse
            {
                Task = task,
                CurrentVersion = sw.CurrentVersionName ?? version,
                Message = $"已載入 {version}，即刻生效。",
            });
        }

        return Problem(
            $"用途 '{task}' 目前只有倉庫能力（列版本／上架／下載），還沒有推論端點可切換現用版本。",
            statusCode: StatusCodes.Status400BadRequest);
    }
}

/// <summary><c>GET /api/models/pools</c> 的回應。</summary>
public sealed class ModelPoolsResponse
{
    public List<ModelPoolDto> Pools { get; set; } = new();
}

/// <summary>一個用途的池狀態。</summary>
public sealed class ModelPoolDto
{
    public string Task { get; set; } = "";

    /// <summary>所屬**站點**代號（moldcode／gongmu／defect）。同站點的多個引擎會共用一張卡。</summary>
    public string GroupKey { get; set; } = "";

    /// <summary>站點顯示名（模號穴號／公母模／瑕疵檢查）。</summary>
    public string GroupName { get; set; } = "";

    /// <summary>這個用途在該站點裡扮演的**引擎**（CRNN 字元式／雙 head 分類…）。</summary>
    public string EngineName { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>登錄夾根目錄。</summary>
    public string Root { get; set; } = "";

    /// <summary>登錄夾是否存在（不存在＝這台機器沒有這個用途的模型）。</summary>
    public bool RootExists { get; set; }

    /// <summary>一個完整版本必備的檔案。</summary>
    public List<string> RequiredFiles { get; set; } = new();

    /// <summary>登錄夾中檔案齊全的版本。</summary>
    public List<string> Versions { get; set; } = new();

    /// <summary>目前現用版本（null = 未設定／此用途尚無推論端點）。</summary>
    public string? CurrentVersion { get; set; }

    /// <summary>已載入記憶體/行程池的版本。</summary>
    public List<ModelPoolLoadedDto> LoadedVersions { get; set; } = new();

    /// <summary>此用途是否已有可用的推論端點。</summary>
    public bool InferReady { get; set; }

    /// <summary>是否支援執行期切換現用版本。</summary>
    public bool CanSwitch { get; set; }

    /// <summary>給人看的說明（未啟用／沒版本／尚無端點…）。</summary>
    public string? Note { get; set; }
}

/// <summary>池中一個已載入版本。</summary>
public sealed class ModelPoolLoadedDto
{
    public string Version { get; set; } = "";
    public bool Ready { get; set; }
}

/// <summary><c>POST /api/models/{task}/current</c> 的請求。</summary>
public sealed class SetCurrentVersionRequest
{
    public string? Version { get; set; }
}

/// <summary><c>POST /api/models/{task}/current</c> 的回應。</summary>
public sealed class SetCurrentVersionResponse
{
    public string Task { get; set; } = "";
    public string? CurrentVersion { get; set; }
    public string? Message { get; set; }
}
