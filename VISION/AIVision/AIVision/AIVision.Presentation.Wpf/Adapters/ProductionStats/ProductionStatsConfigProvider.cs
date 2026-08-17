using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.ProductionStats;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Adapters.ProductionStats;

public sealed class ProductionStatsConfigProvider : IProductionStatsConfigProvider, IDisposable
{
    private readonly ILogger<ProductionStatsConfigProvider> _logger;
    private readonly string _configDirectory;
    private readonly string _configPath;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _sync = new();
    private ProductionStatsUiConfig _current = new();

    public ProductionStatsConfigProvider(IHostEnvironment environment, ILogger<ProductionStatsConfigProvider> logger)
    {
        _logger = logger;
        _configDirectory = Path.Combine(environment.ContentRootPath, "configs");
        _configPath = Path.Combine(_configDirectory, "production-stats.json");
        Directory.CreateDirectory(_configDirectory);
        EnsureDefaultFileExists();

        LoadConfig();

        _watcher = new FileSystemWatcher(_configDirectory, Path.GetFileName(_configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnConfigChanged;
        _watcher.Created += OnConfigChanged;
        _watcher.Renamed += OnConfigChanged;
    }

    public ProductionStatsUiConfig Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event EventHandler<ProductionStatsUiConfig>? ConfigurationChanged;

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        Task.Delay(200).ContinueWith(_ =>
        {
            LoadConfig();
        });
    }

    private void LoadConfig()
    {
        try
        {
            ProductionStatsUiConfig? config = null;
            if (File.Exists(_configPath))
            {
                using var stream = File.OpenRead(_configPath);
                config = JsonSerializer.Deserialize<ProductionStatsUiConfig>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }

            if (config == null)
            {
                config = CreateDefault();
            }

            lock (_sync)
            {
                _current = config;
            }

            ConfigurationChanged?.Invoke(this, config);
            _logger.LogInformation("Production stats configuration reloaded from {Path}", _configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load production stats configuration from {Path}", _configPath);
        }
    }

    private void EnsureDefaultFileExists()
    {
        if (File.Exists(_configPath))
        {
            return;
        }

        var config = CreateDefault();
        using var stream = File.Create(_configPath);
        JsonSerializer.Serialize(stream, config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        stream.Flush();
    }

    private static ProductionStatsUiConfig CreateDefault() =>
        new()
        {
            SummaryFields =
            {
                new SummaryFieldConfig { Label = "工單", Path = "Order.Code", LabelWidth = "200" },
                new SummaryFieldConfig { Label = "產品", Path = "Order.Product", LabelWidth = "200" },
                new SummaryFieldConfig { Label = "開始時間", Path = "Order.StartAt", Format = "dt:yyyy/MM/dd HH:mm", LabelWidth = "200" },
                new SummaryFieldConfig { Label = "結束時間", Path = "Order.EndAt", Format = "dt:yyyy/MM/dd HH:mm", LabelWidth = "200" },
                new SummaryFieldConfig { Label = "總數(排除略過)", Path = "Total", Format = "n0", LabelWidth = "200" },
                new SummaryFieldConfig { Label = "良品數(Match+TrustInput)", Path = "Ok", Format = "n0", LabelWidth = "200" },
                new SummaryFieldConfig { Label = "混料剔除數(MixedAlarm)", Path = "Ng", Format = "n0", LabelWidth = "200" },
                new SummaryFieldConfig { Label = "良率", Path = "Yield", Format = "p2", LabelWidth = "200" }
            },
            // 模號三態核對結果(取代舊瑕疵類型 buckets);鍵 = MarkingVerifyOutcome 名稱。
            DefectBuckets = new List<string> { "Match", "TrustInput", "MixedAlarm", "Skip" },
            DefectLabelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Match"] = "相符 (Match)",
                ["TrustInput"] = "採信輸入 (TrustInput)",
                ["MixedAlarm"] = "混料警報 (MixedAlarm)",
                ["Skip"] = "略過 (Skip)"
            }
        };

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
