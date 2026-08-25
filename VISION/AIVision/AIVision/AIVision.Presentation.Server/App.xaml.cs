using System.IO;
using System.Windows;
using AIVision.Infrastructure.MoldCode;
using AIVision.Presentation.Server.ViewModels;
using AIVision.Presentation.Server.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AIVision.Presentation.Server;

/// <summary>
/// 中央推論機（父端）專用程式進入點。
/// <para>
/// 與站端 App 完全分開：這支裝在跑推論的那台機器上，只負責「看自己這台收到什麼、狀態如何」，
/// 不含相機／PLC／工單等產線功能。位址預設 localhost（本機就是中央機），可在 appsettings 改。
/// </para>
/// </summary>
/// <remarks>基底類別要寫完整名稱：本組件同時參考 <c>AIVision.Application</c> 命名空間，
/// 直接寫 <c>Application</c> 會被解析成命名空間而編譯失敗。</remarks>
public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                // 用程式目錄而非工作目錄：從捷徑/其他資料夾啟動時，
                // GetCurrentDirectory() 不等於 exe 所在地，appsettings 會靜默讀不到（位址退回預設 localhost）。
                cfg.SetBasePath(AppContext.BaseDirectory);
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<InferenceServerOptions>(
                    context.Configuration.GetSection("InferenceServer"));

                services.AddHttpClient<CrnnInferClient>(client =>
                {
                    // 冷啟可達 90 秒；實際逾時由 client 內部 CTS 控制。
                    client.Timeout = TimeSpan.FromMinutes(5);
                });

                // 監控用（不送圖，只問狀態）：最近辨識紀錄 + 各用途模型池 + 切換現用版本。
                services.AddHttpClient<InferenceMonitorClient>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(60);
                });

                services.AddSingleton<ServerMonitorViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // 視窗尺寸一律用「螢幕工作區的百分比」（預設 80%），不用寫死像素。
        // 比例可由 appsettings 的 Ui:WindowRatio 現場調整。
        // ⚠ 必須在任何視窗顯示前註冊，否則先開的視窗吃不到；但也要在 host 建好之後才讀得到設定。
        Services.WindowSizeAdapter.RegisterGlobal(
            _host.Services.GetRequiredService<IConfiguration>().GetValue<double?>("Ui:WindowRatio"));

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
